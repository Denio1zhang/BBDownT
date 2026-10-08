using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace BBDownT;

/// <summary>
/// 断点续传用的远端实体校验器，保存在分片(或临时文件)旁的 .resume 文件里：
/// 第一行 ETag(Base64)，第二行 Last-Modified，第三行(可选)远端文件总长度。
/// 下载地址带签名、每次解析都会变，所以只按校验器和总长度判断是不是同一个文件，不比较地址。
/// </summary>
/// <param name="TotalLength">远端文件总长度；旧版本写的 .resume 文件没有这一行，为null</param>
internal sealed record DownloadResumeValidator(string? EntityTag, DateTimeOffset? LastModified, long? TotalLength = null)
{
    public bool IsUsable => !string.IsNullOrEmpty(EntityTag) || LastModified is not null;

    public static DownloadResumeValidator FromResponse(HttpResponseMessage response, long? totalLength = null)
    {
        return new(
            response.Headers.ETag is { IsWeak: false } entityTag ? entityTag.ToString() : null,
            response.Content.Headers.LastModified,
            totalLength);
    }

    public void Apply(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(EntityTag)
            && EntityTagHeaderValue.TryParse(EntityTag, out var entityTag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
        }
        else if (LastModified is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(LastModified.Value);
        }
    }

    public bool Matches(HttpResponseMessage response)
    {
        if (!string.IsNullOrEmpty(EntityTag))
        {
            return string.Equals(
                EntityTag,
                response.Headers.ETag?.ToString(),
                StringComparison.Ordinal);
        }

        return LastModified is not null
            && response.Content.Headers.LastModified == LastModified;
    }

    /// <summary>
    /// 与另一个校验器(如本次探测远端文件大小时取得的)是否指向同一个远端实体：
    /// 有 ETag 时比较 ETag，否则比较 Last-Modified；两边都知道总长度时总长度也要相同
    /// </summary>
    public bool SameEntity(DownloadResumeValidator? other)
    {
        if (other is null || !IsUsable || !other.IsUsable) return false;
        if (TotalLength is not null && other.TotalLength is not null && TotalLength != other.TotalLength) return false;
        if (!string.IsNullOrEmpty(EntityTag) || !string.IsNullOrEmpty(other.EntityTag))
            return string.Equals(EntityTag, other.EntityTag, StringComparison.Ordinal);
        return LastModified == other.LastModified;
    }

    public static async Task<DownloadResumeValidator?> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(await File.ReadAllLinesAsync(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 同步读取(列出已下载文件、合并分片时用)；文件不存在或内容无效时返回null
    /// </summary>
    public static DownloadResumeValidator? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DownloadResumeValidator? Parse(string[] lines)
    {
        if (lines.Length is not (2 or 3))
        {
            return null;
        }

        var entityTag = Decode(lines[0]);
        DateTimeOffset? lastModified = DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        long? totalLength = lines.Length == 3 && long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0
            ? total
            : null;
        var validator = new DownloadResumeValidator(entityTag, lastModified, totalLength);
        return validator.IsUsable ? validator : null;
    }

    public async Task SaveAsync(string path)
    {
        await File.WriteAllLinesAsync(path, Lines());
    }

    public void Save(string path)
    {
        File.WriteAllLines(path, Lines());
    }

    private string[] Lines()
    {
        string[] lines = [Encode(EntityTag), LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? ""];
        return TotalLength is long total ? [.. lines, total.ToString(CultureInfo.InvariantCulture)] : lines;
    }

    private static string Encode(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string? Decode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
