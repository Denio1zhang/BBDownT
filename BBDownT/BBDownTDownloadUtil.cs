using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.Tasks;
using static BBDownT.Core.Entity.Entity;
using static BBDownT.Core.Logger;
using static BBDownT.Core.Util.HTTPUtil;
using System.Collections.Concurrent;
using System.Threading;

namespace BBDownT;

internal static class BBDownTDownloadUtil
{
    public class DownloadConfig
    {
        public bool UseAria2c { get; set; } = false;
        public string Aria2cArgs { get; set; } = string.Empty;
        public bool ForceHttp { get; set; } = false;
        public bool MultiThread { get; set; } = false;
        public DownloadTask? RelatedTask { get; set; } = null;
    }

    /// <summary>
    /// 多线程分片的续传设置
    /// </summary>
    internal sealed class ClipResumeOptions
    {
        /// <summary>
        /// 分片下载完成后保留 .resume 校验器：中断后再次下载时，整段已完成的分片经远端校验一致即可直接沿用；
        /// 整条轨道合并成功后由 <see cref="MergeTrackClips"/> 删除
        /// </summary>
        public bool KeepValidator { get; init; }

        /// <summary>
        /// 旧版本留下的完整分片没有自己的校验器(下载完成时被删除了)。同一轨道其他分片的校验器与本次远端文件一致时，
        /// 传入本次探测到的远端校验器，用它校验这类分片；末尾抽样字节也必须与远端一致才沿用
        /// </summary>
        public DownloadResumeValidator? LegacyValidator { get; init; }

        /// <summary>
        /// 沿用已有字节时调用：参数为沿用的字节数，以及是否整段沿用(否则是从中途续传)。
        /// 进度条据此不把这些字节算进下载速度和本次下载量
        /// </summary>
        public Action<long, bool>? OnReused { get; init; }
    }

    /// <summary>
    /// 默认分片大小(20 MiB)
    /// </summary>
    internal const int DefaultClipSize = 20 * 1024 * 1024;

    /// <summary>
    /// 校验完整旧分片时从远端读取并与本地比较的末尾字节数
    /// </summary>
    internal const int ClipSampleBytes = 64;

    /// <summary>
    /// 分片下载失败后重试前的等待(第 n 次重试等 n 倍)
    /// </summary>
    internal static readonly TimeSpan ClipRetryDelay = TimeSpan.FromMilliseconds(500);

    internal static async Task RangeDownloadToTmpAsync(
        int id,
        string url,
        string tmpName,
        long fromPosition,
        long? toPosition,
        Action<int, long, long> onProgress,
        bool failOnRangeNotSupported = false,
        HttpClient? httpClient = null,
        ClipResumeOptions? resume = null)
    {
        var client = httpClient ?? AppHttpClient;
        var validatorPath = tmpName + ".resume";
        var resumeValidator = await DownloadResumeValidator.LoadAsync(validatorPath);
        if (toPosition is long clipEnd && clipEnd >= fromPosition && FileLength(tmpName) == clipEnd - fromPosition + 1)
        {
            // 完整的旧分片：远端仍是同一个文件(校验器与总长度一致)、且末尾抽样字节相同时直接沿用；
            // 没有校验器或确认远端已变化时从头重新下载，避免把不同画质、编码或已更新的文件拼在一起。
            // 无法确认(限流、5xx、连接中断等)时 ConfirmCompleteClipAsync 抛出异常，由外层重试，不动本地分片和校验器
            var clipLength = clipEnd - fromPosition + 1;
            var candidate = resumeValidator ?? resume?.LegacyValidator;
            var confirmed = candidate is null
                ? null
                : await ConfirmCompleteClipAsync(client, url, tmpName, fromPosition, clipEnd, candidate);
            if (confirmed is not null)
            {
                if (resume?.KeepValidator == true)
                {
                    if (confirmed != resumeValidator) await confirmed.SaveAsync(validatorPath);
                }
                else
                {
                    File.Delete(validatorPath);
                }
                resume?.OnReused?.Invoke(clipLength, true);
                onProgress(id, clipLength, clipEnd + 1);
                return;
            }
            File.Delete(validatorPath);
            resumeValidator = null;
            await File.WriteAllBytesAsync(tmpName, []);
        }

        var restarted = false;
        while (true)
        {
            using var fileStream = new FileStream(tmpName, FileMode.OpenOrCreate);
            fileStream.Seek(0, SeekOrigin.End);
            if (fileStream.Position > 0 && resumeValidator is null)
            {
                fileStream.SetLength(0);
                fileStream.Position = 0;
            }
            if (toPosition > 0 && fileStream.Position >= toPosition - fromPosition + 1)
            {
                // 长度不对的旧分片(正常不会出现)：从头请求可避免跨版本拼接
                fileStream.SetLength(0);
                fileStream.Position = 0;
                resumeValidator = null;
            }
            var existingLength = fileStream.Position;
            var downloadedBytes = fromPosition + existingLength;
            // 校验器只有 Last-Modified(没有强 ETag)时，同一秒生成的另一个文件也会被 If-Range 当成同一个：
            // 从已下载部分的末尾往前多请求几十个字节，与本地末尾比对一致才接着追加
            var overlap = existingLength > 0 && resumeValidator is not null && string.IsNullOrEmpty(resumeValidator.EntityTag)
                ? (int)Math.Min(ClipSampleBytes, existingLength)
                : 0;

            using var httpRequestMessage = CreateMediaRequest(url);
            httpRequestMessage.Headers.Range = new(downloadedBytes - overlap, toPosition);
            if (existingLength > 0)
            {
                resumeValidator?.Apply(httpRequestMessage);
            }

            using var response = await client.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                var remoteLength = response.Content.Headers.ContentRange?.Length;
                if (existingLength > 0
                    && remoteLength == downloadedBytes
                    && resumeValidator is not null
                    && resumeValidator.Matches(response))
                {
                    if (resume?.KeepValidator != true) File.Delete(validatorPath);
                    resume?.OnReused?.Invoke(existingLength, false);
                    onProgress(id, existingLength, downloadedBytes);
                    return;
                }

                fileStream.SetLength(0);
                fileStream.Position = 0;
                File.Delete(validatorPath);
                throw new IOException("续传位置不再有效，已清空临时文件以便重试");
            }
            response.EnsureSuccessStatusCode();
            long? responseContentLength = response.Content.Headers.ContentLength;
            long? entityLength;

            if (response.StatusCode == HttpStatusCode.OK) // server doesn't response a partial content
            {
                if (existingLength > 0 && toPosition is not null && !restarted)
                {
                    // If-Range 发现远端文件已变化，服务器返回了整个文件：清空这一段，改用普通范围请求从头下载
                    fileStream.SetLength(0);
                    File.Delete(validatorPath);
                    resumeValidator = null;
                    restarted = true;
                    continue;
                }
                if (failOnRangeNotSupported && (downloadedBytes > 0 || toPosition != null)) throw new NotSupportedException("Range request is not supported.");
                downloadedBytes = 0;
                existingLength = 0;
                fileStream.SetLength(0);
                fileStream.Position = 0;
                entityLength = responseContentLength;
            }
            else if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (existingLength > 0 && resumeValidator is not null
                    && (!resumeValidator.Matches(response)
                        || (resumeValidator.TotalLength is long expectedTotal && contentRange?.Length is long remoteTotal && remoteTotal != expectedTotal)))
                {
                    if (restarted) throw new InvalidDataException("续传响应的远端实体校验器已变化");
                    // 服务器没有按 If-Range 处理、但远端文件已变化：清空这一段后从头下载
                    fileStream.SetLength(0);
                    File.Delete(validatorPath);
                    resumeValidator = null;
                    restarted = true;
                    continue;
                }
                responseContentLength = ValidatePartialContentRange(
                    contentRange,
                    responseContentLength,
                    downloadedBytes - overlap,
                    toPosition);
                entityLength = contentRange?.Length;
            }
            else
            {
                throw new InvalidDataException($"不支持的下载响应状态: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            if (overlap > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                // 重叠的字节读不全时抛出 EndOfStreamException，由外层重试
                var remoteOverlap = new byte[overlap];
                await stream.ReadExactlyAsync(remoteOverlap);
                var localOverlap = new byte[overlap];
                fileStream.Seek(-overlap, SeekOrigin.End);
                await fileStream.ReadExactlyAsync(localOverlap);
                fileStream.Seek(0, SeekOrigin.End);
                if (!remoteOverlap.AsSpan().SequenceEqual(localOverlap))
                {
                    if (restarted) throw new InvalidDataException("续传位置之前的字节与远端不一致");
                    // 远端已是另一个文件(只是 Last-Modified 相同)：清空这一段后从头下载
                    fileStream.SetLength(0);
                    File.Delete(validatorPath);
                    resumeValidator = null;
                    restarted = true;
                    continue;
                }
                responseContentLength -= overlap;
            }

            var responseValidator = DownloadResumeValidator.FromResponse(response, entityLength);
            if (responseValidator.IsUsable)
            {
                await responseValidator.SaveAsync(validatorPath);
            }
            else
            {
                File.Delete(validatorPath);
            }
            if (existingLength > 0) resume?.OnReused?.Invoke(existingLength, false);

            var totalBytes = downloadedBytes + (responseContentLength ?? long.MaxValue - downloadedBytes);

            const int blockSize = 1048576 / 4;
            var buffer = new byte[blockSize];

            while (downloadedBytes < totalBytes)
            {
                var recevied = await stream.ReadAsync(buffer);
                if (recevied == 0) break;
                await fileStream.WriteAsync(buffer.AsMemory(0, recevied));
                await fileStream.FlushAsync();
                downloadedBytes += recevied;
                onProgress(id, downloadedBytes - fromPosition, totalBytes);
            }

            var expectedTempLength = GetExpectedTempLength(existingLength, responseContentLength);
            if (expectedTempLength != null && expectedTempLength != fileStream.Length)
                throw new Exception("Retry...");
            if (resume?.KeepValidator != true) File.Delete(validatorPath);
            return;
        }
    }

    /// <summary>
    /// 校验一个完整的旧分片(或合并好的整条轨道)是否仍是远端的同一个文件：带 If-Range 请求末尾的几个字节。结果有三种：
    /// 1. 沿用：返回 206、校验器一致、总长度一致(已知时)，并且这几个字节与本地文件末尾相同，返回带总长度的校验器；
    /// 2. 已变化：带 If-Range 返回 200(服务器确认远端文件已变化)，或返回 206 但校验器、总长度、抽样字节不一致，返回null，由调用方重新下载；
    /// 3. 无法确认：其他状态码(403/404/416/429/5xx 等)、Content-Range 不符合请求、响应体没读完、网络错误，
    ///    抛出 HttpRequestException/InvalidDataException/IOException，由调用方重试，本地文件和校验器保持不动
    /// </summary>
    internal static async Task<DownloadResumeValidator?> ConfirmCompleteClipAsync(HttpClient client, string url, string localPath,
        long fromPosition, long toPosition, DownloadResumeValidator validator)
    {
        var sampleLength = (int)Math.Min(ClipSampleBytes, toPosition - fromPosition + 1);
        var sampleFrom = toPosition - sampleLength + 1;
        using var request = CreateMediaRequest(url);
        request.Headers.Range = new(sampleFrom, toPosition);
        validator.Apply(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.OK && request.Headers.IfRange is not null) return null;
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"校验已下载的部分时服务器返回 {(int)response.StatusCode}，稍后重试", null, response.StatusCode);
        }
        if (!validator.Matches(response)) return null;
        var range = response.Content.Headers.ContentRange;
        if (range?.From != sampleFrom || range.To != toPosition || range.Length is not long total)
        {
            throw new InvalidDataException("校验已下载的部分时服务器返回的 Content-Range 与请求不符，稍后重试");
        }
        if (total <= toPosition || (validator.TotalLength is long expected && expected != total)) return null;

        var remote = new byte[sampleLength];
        await using (var body = await response.Content.ReadAsStreamAsync())
        {
            // 响应体提前结束时抛出 EndOfStreamException(IOException)
            await body.ReadExactlyAsync(remote);
        }
        var local = new byte[sampleLength];
        await using (var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            file.Seek(-sampleLength, SeekOrigin.End);
            await file.ReadExactlyAsync(local);
        }
        return remote.AsSpan().SequenceEqual(local) ? validator with { TotalLength = total } : null;
    }

    private static long FileLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    private static HttpRequestMessage CreateMediaRequest(string url)
    {
        var request = new HttpRequestMessage();
        if (!url.Contains("platform=android_tv_yst") && !url.Contains("platform=android"))
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        TryAddCookieHeader(request, url);
        request.RequestUri = new(url);
        return request;
    }

    internal static long? GetExpectedTempLength(long existingLength, long? responseContentLength)
    {
        return responseContentLength is null
            ? null
            : checked(existingLength + responseContentLength.Value);
    }

    internal static long ValidatePartialContentRange(
        ContentRangeHeaderValue? contentRange,
        long? contentLength,
        long requestedFrom,
        long? requestedTo)
    {
        if (contentRange?.From != requestedFrom || contentRange.To is null)
        {
            throw new InvalidDataException("服务器返回的 Content-Range 与请求起点不一致");
        }

        if (requestedTo is not null && contentRange.To != requestedTo)
        {
            throw new InvalidDataException("服务器返回的 Content-Range 未完整覆盖请求范围");
        }

        if (requestedTo is null
            && (contentRange.Length is null || contentRange.To != contentRange.Length - 1))
        {
            throw new InvalidDataException("服务器返回的 Content-Range 未到达资源末尾");
        }

        var declaredRangeLength = contentRange.To.Value - contentRange.From.Value + 1;
        if (contentLength is not null && declaredRangeLength != contentLength)
        {
            throw new InvalidDataException("服务器返回的 Content-Range 与 Content-Length 不一致");
        }

        return declaredRangeLength;
    }

    public static async Task DownloadFileAsync(string url, string path, DownloadConfig config)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (config.ForceHttp) url = ReplaceUrl(url);
        LogDebug("Start downloading: {0}", url);
        string desDir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(desDir) && !Directory.Exists(desDir)) Directory.CreateDirectory(desDir);
        if (config.UseAria2c)
        {
            await DownloadWithAria2cAsync(url, path, config.Aria2cArgs);
            Console.WriteLine();
            return;
        }
        int retry = 0;
        string tmpName = Path.Combine(desDir, Path.GetFileNameWithoutExtension(path) + ".tmp");
        reDown:
        try
        {
            using var progress = new ProgressBar(config.RelatedTask);
            await RangeDownloadToTmpAsync(0, url, tmpName, 0, null, (_, downloaded, total) => progress.Report((double)downloaded / total, downloaded));
            File.Move(tmpName, path, true);
            // 之前多线程下载合并时留下的整条轨道校验器已不再对应这个文件
            File.Delete(path + ".resume");
        }
        catch (Exception)
        {
            if (++retry == 3) throw;
            goto reDown;
        }
    }

    public static async Task<string[]> MultiThreadDownloadFileAsync(string url, string path, DownloadConfig config, HttpClient? httpClient = null,
        int clipSize = DefaultClipSize)
    {
        if (config.ForceHttp) url = ReplaceUrl(url);
        LogDebug("Start downloading: {0}", url);
        if (config.UseAria2c)
        {
            await DownloadWithAria2cAsync(url, path, config.Aria2cArgs);
            DeleteStaleClipFiles(path);
            Console.WriteLine();
            return [];
        }
        long fileSize;
        DownloadResumeValidator remote;
        try
        {
            (fileSize, remote) = await ProbeRemoteFileAsync(url, httpClient);
        }
        catch (InvalidDataException ex)
        {
            LogWarn($"{ex.Message}，自动切换为单线程下载");
            await DownloadFileAsync(url, path, new DownloadConfig
            {
                ForceHttp = false,
                RelatedTask = config.RelatedTask
            });
            DeleteStaleClipFiles(path);
            return [];
        }
        LogDebug("文件大小：{0} bytes", fileSize);
        // 整条轨道上次已下载并合并完成(中断发生在下载其他轨道或混流时)：远端还是同一个文件就不再下载。
        // 同样大小的轨道可能是另一种画质或编码，所以只认合并时留下的校验器，并抽样比对末尾字节
        if (await IsCompleteTrackAsync(httpClient ?? AppHttpClient, url, path, fileSize, remote))
        {
            Log($"{Path.GetFileName(path)} 上次已下载完成，远端文件未变化，直接沿用");
            DeleteStaleClipFiles(path);
            return [];
        }
        List<Clip> allClips = GetAllClips(fileSize, clipSize);
        var clipPaths = allClips.Select(clip => Path.Combine(Path.GetDirectoryName(path)!,
            clip.index.ToString("00000") + "_" + Path.GetFileNameWithoutExtension(path)
            + (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? ".vclip" : ".aclip")))
            .ToArray();
        int total = allClips.Count;
        LogDebug("分段数量：{0}", total);
        // 旧版本下载完成的分片没有校验器：只有同一轨道还有分片的校验器与本次远端文件一致(说明上次下载的就是这个文件)时，
        // 才用本次的远端校验器去校验它们(另外还要抽样比对字节)；否则重新下载
        var legacyValidator = remote.IsUsable
            && clipPaths.Any(clip => DownloadResumeValidator.Load(clip + ".resume")?.SameEntity(remote) == true)
            ? remote
            : null;
        ConcurrentDictionary<int, long> clipProgress = new();
        foreach (var i in allClips) clipProgress[i.index] = 0;

        using var progress = new ProgressBar(config.RelatedTask);
        progress.Report(0);
        var reusedClips = 0;
        long reusedBytes = 0;
        // 沿用的字节只算本次下载开始前就在磁盘上的部分，每个分片只算一次：
        // 重试时 RangeDownloadToTmpAsync 看到的已有长度包含本次前几次尝试刚下载的字节，那些已经计入下载量
        var initialLengths = clipPaths.Select(clip => Math.Max(0, FileLength(clip))).ToArray();
        var reportedReuse = new long[clipPaths.Length];
        var countedWholeClip = new bool[clipPaths.Length];
        ClipResumeOptions ResumeFor(int index) => new()
        {
            KeepValidator = true,
            LegacyValidator = legacyValidator,
            OnReused = (bytes, wholeClip) =>
            {
                var fresh = Math.Min(bytes, initialLengths[index]) - reportedReuse[index];
                if (fresh <= 0) return;
                reportedReuse[index] += fresh;
                progress.ReportReused(fresh);
                Interlocked.Add(ref reusedBytes, fresh);
                if (wholeClip && !countedWholeClip[index])
                {
                    countedWholeClip[index] = true;
                    Interlocked.Increment(ref reusedClips);
                }
            }
        };
        await Parallel.ForEachAsync(allClips, async (clip, _) =>
        {
            int retry = 0;
            string tmp = clipPaths[clip.index];
            var resume = ResumeFor(clip.index);
            reDown:
            try
            {
                await RangeDownloadToTmpAsync(clip.index, url, tmp, clip.from, clip.to == -1 ? null : clip.to, (index, downloaded, _) =>
                {
                    clipProgress[index] = downloaded;
                    progress.Report((double)clipProgress.Values.Sum() / fileSize, clipProgress.Values.Sum());
                }, true, httpClient, resume);
            }
            catch (NotSupportedException)
            {
                if (++retry == 3) throw new Exception($"服务器可能并不支持多线程下载, 请使用 --multi-thread false 关闭多线程");
                goto reDown;
            }
            catch (Exception e)
            {
                if (++retry == 3) throw new Exception($"Failed to download clip {clip.index}", e);
                LogDebug("分片 {0} 下载失败，稍后重试: {1}", clip.index, e.Message);
                // 限流(429)、5xx 等通常过一会儿就好：稍等再试，不立刻连发
                await Task.Delay(ClipRetryDelay * retry);
                goto reDown;
            }
        });
        if (reusedBytes > 0)
        {
            Log($"续传：沿用了 {reusedClips} 个已下载完成的分片，共沿用已下载的 {BBDownTUtil.FormatFileSize(reusedBytes)}");
        }
        return clipPaths;
    }

    /// <summary>
    /// 本地的整条轨道是否就是远端这个文件：大小相同、合并分片时留下的校验器与本次远端一致，且末尾抽样字节相同
    /// </summary>
    private static async Task<bool> IsCompleteTrackAsync(HttpClient client, string url, string path, long fileSize, DownloadResumeValidator remote)
    {
        if (FileLength(path) != fileSize) return false;
        var saved = DownloadResumeValidator.Load(path + ".resume");
        if (saved is null || saved.TotalLength != fileSize || !saved.SameEntity(remote)) return false;
        // 确认远端已变化才重新下载；暂时无法确认(限流、5xx、连接中断)时稍后重试，仍不行就让这次下载失败，
        // 不为一次网络波动把整条轨道(可能有一两个GB)重新下载一遍
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ConfirmCompleteClipAsync(client, url, path, 0, fileSize - 1, saved) is not null;
            }
            catch (Exception e) when (attempt < 3 && e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                LogDebug("校验已下载的轨道失败，稍后重试: {0}", e.Message);
                await Task.Delay(ClipRetryDelay * attempt);
            }
        }
    }

    internal static void MergeTrackClips(string[] files, string destination)
    {
        if (files.Length == 0) return;
        // 合并后的整条轨道也留一个校验器：中断发生在之后下载其他轨道或混流时，再次下载可以直接沿用这条轨道
        var trackValidator = DownloadResumeValidator.Load(files[0] + ".resume");
        // 旧的整条轨道校验器先删掉：合并到一半中断时，不会留下旧校验器配新内容
        File.Delete(destination + ".resume");
        BBDownTUtil.CombineMultipleFilesIntoSingleFile(files, destination);
        foreach (var file in files)
        {
            MediaOutput.DeleteInput(file, destination);
            File.Delete(file + ".resume");
        }
        // 分片和它们的校验器只在合并成功前有用；之前不同大小的下载留下的多余分片也一并删除
        DeleteStaleClipFiles(destination);
        var length = FileLength(destination);
        if (trackValidator is not null && length > 0 && (trackValidator.TotalLength is null || trackValidator.TotalLength == length))
        {
            (trackValidator with { TotalLength = length }).Save(destination + ".resume");
        }
        else
        {
            File.Delete(destination + ".resume");
        }
    }

    private static async Task DownloadWithAria2cAsync(string url, string path, string extraArgs)
    {
        var exitCode = await BBDownTAria2c.DownloadFileByAria2cAsync(url, path, extraArgs);
        EnsureAria2cDownloadSucceeded(exitCode, path);
    }

    internal static void EnsureAria2cDownloadSucceeded(int exitCode, string path)
    {
        if (exitCode != 0 || File.Exists(path + ".aria2") || !File.Exists(path))
        {
            throw new InvalidOperationException($"aria2下载失败，退出码: {exitCode}");
        }
    }

    internal static int DeleteStaleClipFiles(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var destinationName = Path.GetFileNameWithoutExtension(destinationPath);
        var clipExtension = Path.GetExtension(destinationPath).EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ".vclip"
            : ".aclip";
        var expectedSuffix = $"_{destinationName}{clipExtension}";
        var deletedCount = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(candidate);
            var generatedSuffix = fileName.Length >= 5 ? fileName[5..] : string.Empty;
            if (fileName.Length < 5
                || (generatedSuffix != expectedSuffix && generatedSuffix != expectedSuffix + ".resume")
                || !fileName.AsSpan(0, 5).ToString().All(char.IsDigit))
            {
                continue;
            }

            File.Delete(candidate);
            deletedCount++;
        }
        return deletedCount;
    }

    //此函数主要是切片下载逻辑
    internal static List<Clip> GetAllClips(long fileSize, int perSize = DefaultClipSize)
    {
        if (perSize <= 0) throw new ArgumentOutOfRangeException(nameof(perSize));
        List<Clip> clips = [];
        int index = 0;
        long from = 0;
        while (from < fileSize)
        {
            var to = Math.Min(checked(from + perSize - 1), fileSize - 1);
            clips.Add(new Clip
            {
                index = index,
                from = from,
                to = to
            });
            from = checked(to + 1);
            index++;
        }
        return clips;
    }

    /// <summary>
    /// 不带 Range 请求一次，只读响应头：远端文件大小，以及用来判断续传的文件是否还是同一个的校验器(ETag/Last-Modified)
    /// </summary>
    private static async Task<(long Size, DownloadResumeValidator Validator)> ProbeRemoteFileAsync(string url, HttpClient? httpClient = null)
    {
        using var httpRequestMessage = CreateMediaRequest(url);
        using var response = (await (httpClient ?? AppHttpClient).SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
        var size = GetTotalFileSize(
            response.StatusCode,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentRange);
        return (size, DownloadResumeValidator.FromResponse(response, size));
    }

    internal static long GetTotalFileSize(
        HttpStatusCode statusCode,
        long? contentLength,
        ContentRangeHeaderValue? contentRange)
    {
        return statusCode switch
        {
            HttpStatusCode.OK => EnsureKnownPositiveFileSize(contentLength),
            HttpStatusCode.PartialContent => EnsureKnownPositiveFileSize(contentRange?.Length),
            _ => throw new InvalidDataException($"不支持的文件大小响应状态: {(int)statusCode}")
        };
    }

    internal static long EnsureKnownPositiveFileSize(long? contentLength)
    {
        if (contentLength is null or <= 0)
        {
            throw new InvalidDataException("服务器未返回有效的 Content-Length，无法进行多线程分段下载");
        }

        return contentLength.Value;
    }

    /// <summary>
    /// 将下载地址强制转换为HTTP
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static string ReplaceUrl(string url)
    {
        if (url.Contains(".mcdn.bilivideo.cn:"))
        {
            LogDebug("对[*.mcdn.bilivideo.cn:xxx]域名不做处理");
            return url;
        }

        LogDebug("将https更改为http");
        return url.Replace("https:", "http:");
    }
}
