using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using BBDownT.Core;

namespace BBDownT;

/// <summary>
/// 未完成下载的说明文件 .bbdownt-task.json 的内容：视频标题、封面，以及继续下载用的请求(不含Cookie、Token等登录信息)
/// </summary>
public sealed record DownloadWorkMetadata
{
    public int Version { get; init; } = DownloadWorkFolder.MetadataVersion;
    /// <summary>
    /// engine：下载开始时由下载流程写入；lookup：旧版本留下的临时文件夹没有这个文件，按 av 号查询标题后补写(没有 Request)
    /// </summary>
    public string Source { get; init; } = DownloadWorkFolder.SourceEngine;
    public string? Title { get; init; }
    public string? Owner { get; init; }
    public string? Pic { get; init; }
    /// <summary>
    /// 提交的链接或编号，已去掉分享跟踪参数和登录凭证参数
    /// </summary>
    public string? Url { get; init; }
    public string? Aid { get; init; }
    public string? Bvid { get; init; }
    public string? Cid { get; init; }
    /// <summary>
    /// 最近开始下载的分P
    /// </summary>
    public int? Page { get; init; }
    public string? PageTitle { get; init; }
    /// <summary>
    /// 实际使用的解析接口：WEB/TV/APP/INTL
    /// </summary>
    public string? Api { get; init; }
    /// <summary>
    /// 第一次开始下载的时间，Unix时间戳(秒)
    /// </summary>
    public long StartedAt { get; init; }
    public long UpdatedAt { get; init; }
    /// <summary>
    /// 继续下载用的请求，字段与 /add-task 相同，可直接提交
    /// </summary>
    public DownloadHistoryRequest? Request { get; init; }
}

/// <summary>
/// 下载时的临时工作文件夹(工作目录下的 &lt;aid&gt;/)：多线程下载的分片 .vclip/.aclip 及其 .resume 校验器、封面、
/// 合并好的音视频轨道都放在这里，分P完成后清理。下载开始时在里面写入 <see cref="MetadataFileName"/>，
/// 「已下载文件」据此把中断的下载显示为一个带标题、可继续下载的组；文件夹清理时一并删除。
/// </summary>
internal static partial class DownloadWorkFolder
{
    internal const string MetadataFileName = ".bbdownt-task.json";
    internal const int MetadataVersion = 1;
    internal const string SourceEngine = "engine";
    internal const string SourceLookup = "lookup";
    /// <summary>
    /// 说明文件最大读取大小；正常只有几百字节
    /// </summary>
    private const long MaxMetadataBytes = 64 * 1024;

    /// <summary>
    /// 说明文件及写入时的临时文件(.bbdownt-task.json.&lt;随机&gt;.tmp)：文件接口不列出、不允许读取或单独删除
    /// </summary>
    internal static bool IsMetadataFileName(string fileName) => MetadataNameRegex().IsMatch(fileName);

    /// <summary>
    /// 多线程下载的分片(00000_&lt;轨道&gt;.vclip/.aclip)
    /// </summary>
    internal static bool TryParseClip(string fileName, out ClipFileName clip)
    {
        var match = ClipRegex().Match(fileName);
        if (!match.Success)
        {
            clip = default;
            return false;
        }
        var trackBase = match.Groups["base"].Value;
        var page = TrackBaseRegex().Match(trackBase) is { Success: true } track && int.TryParse(track.Groups["page"].Value, out var p) ? p : (int?)null;
        clip = new ClipFileName(int.Parse(match.Groups["index"].Value), trackBase, match.Groups["kind"].Value == "vclip", page);
        return true;
    }

    /// <summary>
    /// 分片或分片的 .resume 校验器
    /// </summary>
    internal static bool IsClipFile(string fileName) =>
        TryParseClip(fileName.EndsWith(".resume", StringComparison.Ordinal) ? fileName[..^".resume".Length] : fileName, out _);

    /// <summary>
    /// 从轨道名(&lt;aid&gt;.P1.&lt;cid&gt;)取 av 号
    /// </summary>
    internal static string? AidFromTrackBase(string trackBase) =>
        TrackBaseRegex().Match(trackBase) is { Success: true } match ? match.Groups["aid"].Value : null;

    /// <summary>
    /// 下载流程在临时工作文件夹 &lt;aid&gt;/ 里产生的工作文件(文件名以文件夹名即 av 号开头)：
    /// 分片 NNNNN_&lt;aid&gt;.P&lt;n&gt;.&lt;cid&gt;.vclip/.aclip，以及 .resume 校验器
    /// (去掉后缀后是分片、轨道 &lt;aid&gt;.P&lt;n&gt;.&lt;cid&gt;.* 或单线程下载的 &lt;aid&gt;….tmp)。
    /// 别的下载工具留下的 foo.resume 之类不算，免得把普通文件夹当成未完成的下载整个删掉
    /// </summary>
    internal static bool IsWorkFileOf(string fileName, string folderName)
    {
        if (!IsAidFolderName(folderName)) return false;
        var prefix = folderName + ".";
        var isValidator = fileName.EndsWith(".resume", StringComparison.Ordinal);
        var name = isValidator ? fileName[..^".resume".Length] : fileName;
        if (TryParseClip(name, out var clip)) return clip.TrackBase.StartsWith(prefix, StringComparison.Ordinal);
        return isValidator && name.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 合并分片中断留下的隐藏暂存文件(见 <see cref="MediaOutput"/>)是否属于工作文件夹 &lt;aid&gt;/ 里的轨道
    /// </summary>
    internal static bool IsStagedTrackOf(string fileName, string folderName) =>
        IsAidFolderName(folderName) && MediaOutput.TryParseStagedName(fileName, out var original)
        && original.StartsWith(folderName + ".", StringComparison.Ordinal);

    /// <summary>
    /// 工作文件夹的名字就是 av 号(纯数字)
    /// </summary>
    internal static bool IsAidFolderName(string folderName) =>
        folderName.Length is > 0 and <= 20 && folderName.All(char.IsAsciiDigit);

    // ---------- 正在下载的工作文件夹 ----------
    // 下载流程开始下载某个视频前登记它的工作文件夹(完整路径)，结束(成功、失败或异常)后注销。
    // 「已下载文件」据此判断哪个未完成的下载正在进行(不能删除)，不再按文件修改时间估计
    private static readonly ConcurrentDictionary<string, int> ActiveFolders = new(PathComparer);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string ActiveKey(string folder) =>
        Path.TrimEndingDirectorySeparator(BBDownTApiServer.ResolveRealPath(Path.GetFullPath(folder)));

    /// <summary>
    /// 登记正在使用的工作文件夹(相对当前目录或完整路径)，Dispose 时注销；同一文件夹可以重复登记
    /// </summary>
    internal static IDisposable MarkActive(IEnumerable<string> folders)
    {
        var keys = folders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Select(ActiveKey).Distinct(PathComparer).ToList();
        foreach (var key in keys) ActiveFolders.AddOrUpdate(key, 1, (_, count) => count + 1);
        return new ActiveRegistration(keys);
    }

    internal static bool IsActive(string fullPath) =>
        !ActiveFolders.IsEmpty && ActiveFolders.TryGetValue(ActiveKey(fullPath), out var count) && count > 0;

    private sealed class ActiveRegistration(List<string> keys) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (var key in keys)
            {
                while (ActiveFolders.TryGetValue(key, out var count))
                {
                    if (count <= 1 ? ActiveFolders.TryRemove(new KeyValuePair<string, int>(key, count))
                        : ActiveFolders.TryUpdate(key, count - 1, count)) break;
                }
            }
        }
    }

    internal static string MetadataPath(string folder) => Path.Combine(folder, MetadataFileName);

    internal static DownloadWorkMetadata? Read(string folder)
    {
        try
        {
            var file = new FileInfo(MetadataPath(folder));
            if (!file.Exists || file.Length == 0 || file.Length > MaxMetadataBytes) return null;
            using var stream = file.OpenRead();
            var metadata = JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.DownloadWorkMetadata);
            return metadata is null ? null : metadata with { Title = Clean(metadata.Title), Owner = Clean(metadata.Owner), Pic = Clean(metadata.Pic) };
        }
        catch (Exception e)
        {
            Logger.LogDebug("读取未完成下载的说明文件失败: {0}", e.Message);
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 下载开始：写入(或更新)说明文件，保留第一次开始的时间。写入失败不影响下载
    /// </summary>
    internal static void Begin(string folder, DownloadWorkMetadata metadata)
    {
        try
        {
            var existing = Read(folder);
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            var startedAt = existing is { Source: SourceEngine, StartedAt: > 0 } ? existing.StartedAt : now;
            Write(folder, metadata with { Version = MetadataVersion, Source = SourceEngine, StartedAt = startedAt, UpdatedAt = now }, overwrite: true);
        }
        catch (Exception e)
        {
            Logger.LogDebug("写入未完成下载的说明文件失败: {0}", e.Message);
        }
    }

    /// <summary>
    /// 只在说明文件不存在时写入(给旧版本留下的文件夹补写查到的标题)；已存在(例如下载流程刚写入)时不覆盖
    /// </summary>
    internal static bool TryCreate(string folder, DownloadWorkMetadata metadata)
    {
        try
        {
            if (!Directory.Exists(folder) || File.Exists(MetadataPath(folder))) return false;
            return Write(folder, metadata, overwrite: false);
        }
        catch (Exception e)
        {
            Logger.LogDebug("写入未完成下载的说明文件失败: {0}", e.Message);
            return false;
        }
    }

    /// <summary>
    /// 先写临时文件再改名，写到一半退出也不会留下半个文件；非 Windows 系统上权限为 0600
    /// </summary>
    private static bool Write(string folder, DownloadWorkMetadata metadata, bool overwrite)
    {
        var path = MetadataPath(folder);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, metadata, AppJsonSerializerContext.Default.DownloadWorkMetadata);
            }
            try
            {
                File.Move(temp, path, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(path))
            {
                return false;
            }
            return true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// 一个分P结束时清理工作文件夹：删除对应文件已不存在的 .resume 校验器；
    /// 文件夹里不再有未完成的工作(分片、.tmp 临时文件、带校验器等待混流的轨道)时删除说明文件。
    /// 文件夹是否删除由调用方在之后判断(为空才删)
    /// </summary>
    internal static void CleanUp(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var sidecar in Directory.EnumerateFiles(folder, "*.resume"))
            {
                if (!File.Exists(sidecar[..^".resume".Length])) File.Delete(sidecar);
            }
            // 合并分片中断留下的隐藏暂存文件(可能有整条轨道那么大)；正在写入的不会这么久没有变化
            MediaOutput.DeleteStaleStagedFiles(folder, MediaOutput.StaleStagedAge);
            if (!HasPendingWork(folder)) File.Delete(MetadataPath(folder));
        }
        catch (Exception e)
        {
            Logger.LogDebug("清理下载临时文件夹失败: {0}", e.Message);
        }
    }

    /// <summary>
    /// 文件夹里是否还有未完成的下载：分片、.tmp 临时文件、.resume 校验器或合并中断留下的暂存文件
    /// </summary>
    internal static bool HasPendingWork(string folder) =>
        Directory.EnumerateFiles(folder).Select(Path.GetFileName).Any(name => name is not null
            && (IsClipFile(name) || name.EndsWith(".resume", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal)
                || MediaOutput.TryParseStagedName(name, out _))
            && !IsMetadataFileName(name));

    /// <summary>
    /// 只下载不混流(SkipMux)时轨道本身就是输出：删除它们旁边的续传校验器后照常清理
    /// </summary>
    internal static void FinishTracks(string folder, params string[] tracks)
    {
        foreach (var track in tracks.Where(track => !string.IsNullOrEmpty(track)))
        {
            try { File.Delete(track + ".resume"); }
            catch (Exception e) { Logger.LogDebug("删除续传校验器失败: {0}", e.Message); }
        }
        CleanUp(folder);
    }

    [GeneratedRegex(@"^(?<index>\d{5})_(?<base>.+)\.(?<kind>vclip|aclip)$")]
    private static partial Regex ClipRegex();

    [GeneratedRegex(@"^(?<aid>\d+)\.P(?<page>\d+)\.(?<cid>\d+)")]
    private static partial Regex TrackBaseRegex();

    [GeneratedRegex(@"^\.bbdownt-task\.json(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataNameRegex();
}

/// <param name="Index">分片序号</param>
/// <param name="TrackBase">轨道文件名(不含扩展名)，如 115050127886063.P1.31783979182</param>
/// <param name="IsVideo">.vclip 为视频轨道，.aclip 为音频轨道</param>
/// <param name="Page">能从轨道名认出的分P序号</param>
internal readonly record struct ClipFileName(int Index, string TrackBase, bool IsVideo, int? Page);
