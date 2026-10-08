using System;
using System.IO;
using System.Text.RegularExpressions;
using BBDownT.Core;

namespace BBDownT;

internal static partial class MediaOutput
{
    /// <summary>
    /// 暂存文件多久没有变化算是中断留下的(正在写入的文件会不断更新修改时间)
    /// </summary>
    internal static readonly TimeSpan StaleStagedAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 先写到同一文件夹里的隐藏暂存文件 .&lt;名&gt;.&lt;guid&gt;.partial&lt;扩展名&gt;，成功后再改名为 destination；
    /// 开始前顺便删除这个文件之前中断留下的暂存文件
    /// </summary>
    internal static bool Write(string destination, Func<string, int> write)
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        DeleteStaleStagedFiles(directory, StaleStagedAge, Path.GetFileName(destination));
        var staged = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.partial{Path.GetExtension(destination)}");
        try
        {
            if (write(staged) != 0 || !File.Exists(staged) || new FileInfo(staged).Length == 0)
                return false;
            File.Move(staged, destination, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    /// <summary>
    /// <see cref="Write"/> 的暂存文件名；是的话给出原来的文件名
    /// </summary>
    internal static bool TryParseStagedName(string fileName, out string originalName)
    {
        var match = StagedNameRegex().Match(fileName);
        originalName = match.Success ? match.Groups["stem"].Value + match.Groups["ext"].Value : "";
        return match.Success;
    }

    /// <summary>
    /// 删除文件夹里超过 olderThan 没有变化的暂存文件(崩溃或被强制结束时没来得及删除，可能有整条轨道那么大)；
    /// 指定 originalName 时只删除这个文件的暂存文件。返回删除的个数，失败不影响下载
    /// </summary>
    internal static int DeleteStaleStagedFiles(string directory, TimeSpan olderThan, string? originalName = null)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(directory, ".*.partial*"))
            {
                if (!TryParseStagedName(Path.GetFileName(file), out var original)) continue;
                if (originalName is not null && !string.Equals(original, originalName, StringComparison.Ordinal)) continue;
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                deleted++;
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug("清理中断留下的暂存文件失败: {0}", e.Message);
        }
        return deleted;
    }

    internal static void DeleteInput(string input, string destination)
    {
        if (string.IsNullOrEmpty(input)) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(input), Path.GetFullPath(destination), comparison))
        {
            File.Delete(input);
            // 多线程下载合并出的轨道旁有续传校验器(.resume)，输入用完后一并删除
            File.Delete(input + ".resume");
        }
    }

    [GeneratedRegex(@"^\.(?<stem>.+)\.[0-9a-f]{32}\.partial(?<ext>\.[^.]*)?$")]
    private static partial Regex StagedNameRegex();
}
