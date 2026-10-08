using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BBDownT.Tests;

/// <summary>
/// 多线程下载的续传：整段已完成的分片保留 .resume 校验器，再次下载时用带 If-Range 的小范围请求校验远端文件
/// (ETag/Last-Modified、总长度、末尾抽样字节)，一致才沿用，否则整段重新下载。用本机的HTTP服务器模拟CDN。
/// 分片大小用 256 字节，文件 700 字节 → 3 段：0-255、256-511、512-699
/// </summary>
public class ClipResumeTests : IAsyncLifetime
{
    private const int ClipSize = 256;
    private readonly MediaTestDirectory files = new();
    private RangeFileServer server = null!;
    private HttpClient client = null!;
    private string destination = "";

    public async Task InitializeAsync()
    {
        server = await RangeFileServer.StartAsync(Bytes(700, 1));
        client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false });
        destination = Path.Combine(files.Root, "10.P1.20.mp4");
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await server.DisposeAsync();
        foreach (var file in Directory.GetFiles(files.Root)) File.Delete(file);
        files.Dispose();
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string Clip(int index) => Path.Combine(files.Root, $"{index:00000}_10.P1.20.vclip");

    private Task<string[]> DownloadAsync() =>
        BBDownTDownloadUtil.MultiThreadDownloadFileAsync(server.Url, destination, new(), client, ClipSize);

    /// <summary>
    /// 第一次下载完所有分片后模拟中断：最后一段只剩前3个字节(校验器还在)，没有合并
    /// </summary>
    private async Task InterruptedFirstRunAsync()
    {
        var clips = await DownloadAsync();
        Assert.Equal([Clip(0), Clip(1), Clip(2)], clips);
        var partial = (await File.ReadAllBytesAsync(Clip(2)))[..3];
        await File.WriteAllBytesAsync(Clip(2), partial);
        server.ClearRequests();
    }

    private void AssertMergedEquals(string[] clips, byte[] expected)
    {
        BBDownTDownloadUtil.MergeTrackClips(clips, destination);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.All(new[] { 0, 1, 2 }, i => Assert.False(File.Exists(Clip(i) + ".resume")));
    }

    [Fact]
    public async Task CompletedClipsKeepAValidatorWithTheTotalLength()
    {
        await DownloadAsync();

        foreach (var i in new[] { 0, 1, 2 })
        {
            var validator = await DownloadResumeValidator.LoadAsync(Clip(i) + ".resume");
            Assert.NotNull(validator);
            Assert.Equal("\"v1\"", validator!.EntityTag);
            Assert.Equal(700, validator.TotalLength);
        }
    }

    [Fact]
    public async Task SameEntityTag_CompleteClipsAreReusedAndOnlyTheMissingBytesAreDownloaded()
    {
        await InterruptedFirstRunAsync();

        var clips = await DownloadAsync();

        var ranges = server.Requests.Select(r => r.Range).ToList();
        // 完整的两段只请求了末尾64字节做校验，没有重新下载
        Assert.Contains("bytes=192-255", ranges);
        Assert.Contains("bytes=448-511", ranges);
        Assert.DoesNotContain("bytes=0-255", ranges);
        Assert.DoesNotContain("bytes=256-511", ranges);
        // 最后一段从第3个字节续传
        Assert.Contains(server.Requests, r => r.Range == "bytes=515-699" && r.IfRange == "\"v1\"" && r.Status == 206);
        Assert.All(server.Requests.Where(r => r.Range is "bytes=192-255" or "bytes=448-511"), r => Assert.Equal("\"v1\"", r.IfRange));
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task ChangedEntityTag_CompleteClipsAreDownloadedAgain()
    {
        await InterruptedFirstRunAsync();
        server.Content = Bytes(700, 2);
        server.ETag = "\"v2\"";

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255" && r.IfRange is null && r.Status == 206);
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null && r.Status == 206);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task ChangedTotalLength_CompleteClipsAreDownloadedAgain()
    {
        await InterruptedFirstRunAsync();
        // 同一个 ETag 但总长度变了(不该发生，按不同文件处理)
        server.Content = Bytes(720, 3);

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255" && r.IfRange is null);
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task NoValidator_CompleteClipsAreDownloadedAgain()
    {
        await InterruptedFirstRunAsync();
        foreach (var i in new[] { 0, 1, 2 }) File.Delete(Clip(i) + ".resume");

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255" && r.IfRange is null);
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null);
        Assert.Contains(server.Requests, r => r.Range == "bytes=512-699" && r.IfRange is null);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task FullResponseInsteadOfPartial_CompleteClipsAreDownloadedAgain()
    {
        await InterruptedFirstRunAsync();
        // 服务器对带 If-Range 的请求一律返回整个文件(200)：无法确认是同一个文件
        server.IfRangeAlwaysFull = true;

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=192-255" && r.Status == 200);
        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255" && r.IfRange is null && r.Status == 206);
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null && r.Status == 206);
        // 续传中的一段遇到 200 时清空后从头下载这一段(不再当作服务器不支持多线程)
        Assert.Contains(server.Requests, r => r.Range == "bytes=512-699" && r.IfRange is null && r.Status == 206);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task LegacyCompleteClipsWithoutValidator_AreReusedOnlyWhenASiblingValidatorMatchesAndTheBytesMatch()
    {
        await InterruptedFirstRunAsync();
        // 旧版本：完成的分片没有校验器，未完成的一段的校验器只有 ETag 和 Last-Modified 两行
        File.Delete(Clip(0) + ".resume");
        File.Delete(Clip(1) + ".resume");
        await new DownloadResumeValidator("\"v1\"", null).SaveAsync(Clip(2) + ".resume");
        // 第2段的末尾被改坏：抽样字节不一致，必须重新下载
        var second = await File.ReadAllBytesAsync(Clip(1));
        second[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(Clip(1), second);

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=192-255" && r.IfRange == "\"v1\"" && r.Status == 206);
        Assert.DoesNotContain(server.Requests, r => r.Range == "bytes=0-255");
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null);
        // 沿用的第1段补写了校验器
        Assert.Equal(700, (await DownloadResumeValidator.LoadAsync(Clip(0) + ".resume"))!.TotalLength);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task LegacyCompleteClipsWithoutAnyMatchingSiblingValidator_AreDownloadedAgain()
    {
        await InterruptedFirstRunAsync();
        File.Delete(Clip(0) + ".resume");
        File.Delete(Clip(1) + ".resume");
        // 未完成的一段的校验器来自另一个文件
        await new DownloadResumeValidator("\"other\"", null).SaveAsync(Clip(2) + ".resume");

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255" && r.IfRange is null);
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task MergedTrackIsReusedWhenTheRemoteFileIsUnchanged()
    {
        var clips = await DownloadAsync();
        BBDownTDownloadUtil.MergeTrackClips(clips, destination);
        // 合并后分片的校验器删除，整条轨道留一个
        Assert.False(File.Exists(Clip(0) + ".resume"));
        Assert.Equal(700, (await DownloadResumeValidator.LoadAsync(destination + ".resume"))!.TotalLength);
        server.ClearRequests();

        var again = await DownloadAsync();

        Assert.Empty(again);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(destination));
        Assert.Equal([null, "bytes=636-699"], server.Requests.Select(r => r.Range));

        // 远端文件变了：重新下载
        server.Content = Bytes(700, 4);
        server.ETag = "\"v2\"";
        server.ClearRequests();
        var changed = await DownloadAsync();
        Assert.Equal(3, changed.Length);
        BBDownTDownloadUtil.MergeTrackClips(changed, destination);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task MergedTrackWithoutValidatorIsDownloadedAgain()
    {
        await File.WriteAllBytesAsync(destination, server.Content);

        var clips = await DownloadAsync();

        Assert.Equal(3, clips.Length);
        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255");
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(403)]
    [InlineData(416)]
    public async Task TailCheckThatCannotBeConfirmed_KeepsTheCompleteClipAndReusesItOnRetry(int status)
    {
        await InterruptedFirstRunAsync();
        // 第1段的末尾校验第一次失败(限流、5xx、链接过期等)：不能当成远端已变化而清空这一段
        server.FailOnce("bytes=192-255", status);

        var clips = await DownloadAsync();

        Assert.Contains(server.Requests, r => r.Range == "bytes=192-255" && r.Status == status);
        Assert.Contains(server.Requests, r => r.Range == "bytes=192-255" && r.Status == 206);
        Assert.DoesNotContain(server.Requests, r => r.Range == "bytes=0-255");
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task TailCheckWhoseBodyIsCutShort_KeepsTheCompleteClip()
    {
        await InterruptedFirstRunAsync();
        server.TruncateOnce("bytes=448-511");

        var clips = await DownloadAsync();

        Assert.Equal(2, server.Requests.Count(r => r.Range == "bytes=448-511"));
        Assert.DoesNotContain(server.Requests, r => r.Range == "bytes=256-511");
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task MergedTrackCheckThatFailsOnce_IsRetriedInsteadOfDownloadingTheTrackAgain()
    {
        var clips = await DownloadAsync();
        BBDownTDownloadUtil.MergeTrackClips(clips, destination);
        server.ClearRequests();
        server.FailOnce("bytes=636-699", 503);

        var again = await DownloadAsync();

        Assert.Empty(again);
        Assert.Equal([null, "bytes=636-699", "bytes=636-699"], server.Requests.Select(r => r.Range));
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task RetriedClip_BytesDownloadedInThisSessionAreNotCountedAsReusedAgain()
    {
        await InterruptedFirstRunAsync();
        // 最后一段续传时连接中途断开：重试时已有长度包含这次刚下载的字节，它们不能再算作沿用
        server.TruncateOnce("bytes=515-699");
        var task = new DownloadTask("t", "10", "fixture", 0);

        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(server.Url, destination,
            new BBDownTDownloadUtil.DownloadConfig { RelatedTask = task }, client, ClipSize);

        Assert.Equal(2, server.Requests.Count(r => r.Range?.EndsWith("-699") == true && r.Range != "bytes=636-699" && r.Status == 206));
        Assert.Equal(185, task.CreateSnapshot().TotalDownloadedBytes);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task LastModifiedOnlyValidator_ResumesWithAnOverlapCheck()
    {
        server.ETag = null;
        await InterruptedFirstRunAsync();

        var clips = await DownloadAsync();

        // 只有 Last-Modified：从已下载的3个字节之前开始请求，比对重叠的字节后再追加
        Assert.Contains(server.Requests, r => r.Range == "bytes=512-699" && r.IfRange is not null && r.Status == 206);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task LastModifiedOnlyValidator_SameSecondDifferentFileIsDownloadedAgain()
    {
        server.ETag = null;
        await InterruptedFirstRunAsync();
        // 另一个文件：长度相同、Last-Modified 也相同(同一秒生成)，If-Range 分辨不出来
        server.Content = Bytes(700, 9);

        var clips = await DownloadAsync();

        // 完整的两段末尾字节不一致，重新下载；未完成的一段重叠字节不一致，清空后从头下载
        Assert.Contains(server.Requests, r => r.Range == "bytes=0-255" && r.IfRange is null);
        Assert.Contains(server.Requests, r => r.Range == "bytes=256-511" && r.IfRange is null);
        Assert.Contains(server.Requests, r => r.Range == "bytes=512-699" && r.IfRange is null);
        AssertMergedEquals(clips, server.Content);
    }

    [Fact]
    public async Task MergeThatFailsHalfway_DoesNotLeaveTheOldTrackValidatorBehind()
    {
        var clips = await DownloadAsync();
        // 上一次合并好的轨道和它的校验器(另一个远端文件)
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);
        await new DownloadResumeValidator("\"old\"", null, 3).SaveAsync(destination + ".resume");
        File.Delete(clips[1]);

        Assert.ThrowsAny<Exception>(() => BBDownTDownloadUtil.MergeTrackClips(clips, destination));

        Assert.False(File.Exists(destination + ".resume"));
    }

    [Fact]
    public async Task ReusedBytesAreNotCountedAsDownloadedBytesOfTheTask()
    {
        await InterruptedFirstRunAsync();
        var task = new DownloadTask("t", "10", "fixture", 0);

        var clips = await BBDownTDownloadUtil.MultiThreadDownloadFileAsync(server.Url, destination,
            new BBDownTDownloadUtil.DownloadConfig { RelatedTask = task }, client, ClipSize);

        // 这次实际只下载了最后一段剩下的 185 字节
        Assert.Equal(185, task.CreateSnapshot().TotalDownloadedBytes);
        AssertMergedEquals(clips, server.Content);
    }

    /// <summary>
    /// 支持 Range 和 If-Range 的极简文件服务器；记录每个请求的 Range、If-Range 和响应状态
    /// </summary>
    internal sealed class RangeFileServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly List<RecordedRequest> requests = [];

        private RangeFileServer(WebApplication app, string url, byte[] content)
        {
            this.app = app;
            Url = url;
            Content = content;
        }

        public string Url { get; }
        public byte[] Content { get; set; }
        public string? ETag { get; set; } = "\"v1\"";
        public DateTimeOffset LastModified { get; set; } = new(2025, 8, 18, 14, 27, 5, TimeSpan.Zero);
        public bool IfRangeAlwaysFull { get; set; }
        private readonly Dictionary<string, int> failOnce = [];
        private readonly HashSet<string> truncateOnce = [];

        /// <summary>
        /// 下一次请求这个范围时返回 status(不带内容)
        /// </summary>
        public void FailOnce(string range, int status)
        {
            lock (requests) failOnce[range] = status;
        }

        /// <summary>
        /// 下一次请求这个范围时只发出一半内容就断开连接
        /// </summary>
        public void TruncateOnce(string range)
        {
            lock (requests) truncateOnce.Add(range);
        }

        public List<RecordedRequest> Requests
        {
            get { lock (requests) return [.. requests]; }
        }

        public void ClearRequests()
        {
            lock (requests) requests.Clear();
        }

        public static async Task<RangeFileServer> StartAsync(byte[] content)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var server = new RangeFileServer(app, $"http://127.0.0.1:{port}/media.mp4", content);
            app.Run(server.HandleAsync);
            app.Urls.Add($"http://127.0.0.1:{port}");
            await app.StartAsync();
            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            var content = Content;
            var range = context.Request.Headers.Range.ToString();
            var ifRange = context.Request.Headers.IfRange.ToString();
            var response = context.Response;
            int failStatus;
            bool truncate;
            lock (requests)
            {
                failStatus = failOnce.Remove(range, out var fail) ? fail : 0;
                truncate = truncateOnce.Remove(range);
            }
            if (failStatus != 0)
            {
                response.StatusCode = failStatus;
                Record(range, ifRange, failStatus);
                return;
            }
            if (ETag is not null) response.Headers.ETag = ETag;
            response.Headers.LastModified = LastModified.ToString("R");
            // If-Range：有 ETag 时比较 ETag，否则比较 Last-Modified
            var validator = ETag ?? LastModified.ToString("R");
            var full = range.Length == 0
                || (ifRange.Length > 0 && (IfRangeAlwaysFull || !string.Equals(ifRange, validator, StringComparison.Ordinal)));
            int status;
            if (full)
            {
                status = 200;
                response.StatusCode = status;
                response.ContentLength = content.Length;
                Record(range, ifRange, status);
                await response.Body.WriteAsync(content);
                return;
            }
            var spec = range["bytes=".Length..].Split('-');
            var from = long.Parse(spec[0]);
            var to = spec[1].Length == 0 ? content.Length - 1 : Math.Min(long.Parse(spec[1]), content.Length - 1);
            if (from >= content.Length)
            {
                status = 416;
                response.StatusCode = status;
                response.Headers.ContentRange = $"bytes */{content.Length}";
                Record(range, ifRange, status);
                return;
            }
            status = 206;
            response.StatusCode = status;
            response.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length).ToString();
            response.ContentLength = to - from + 1;
            Record(range, ifRange, status);
            if (truncate)
            {
                await response.Body.WriteAsync(content.AsMemory((int)from, (int)(to - from + 1) / 2));
                await response.Body.FlushAsync();
                await Task.Delay(50);
                context.Abort();
                return;
            }
            await response.Body.WriteAsync(content.AsMemory((int)from, (int)(to - from + 1)));
        }

        private void Record(string range, string ifRange, int status)
        {
            lock (requests) requests.Add(new RecordedRequest(range.Length == 0 ? null : range, ifRange.Length == 0 ? null : ifRange, status));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    internal sealed record RecordedRequest(string? Range, string? IfRange, int Status);
}
