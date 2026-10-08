using System.Net;
using System.Net.Http.Headers;

namespace BBDownT.Tests;

public class DownloadResumeValidatorTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsEntityTagAndLastModified()
    {
        var path = Path.GetTempFileName();
        var expected = new DownloadResumeValidator(
            "\"entity-v1\"",
            new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        try
        {
            await expected.SaveAsync(path);

            var actual = await DownloadResumeValidator.LoadAsync(path);

            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Matches_PrefersEntityTag()
    {
        var validator = new DownloadResumeValidator("\"entity-v1\"", null);
        using var matching = CreateResponse("\"entity-v1\"");
        using var changed = CreateResponse("\"entity-v2\"");

        Assert.True(validator.Matches(matching));
        Assert.False(validator.Matches(changed));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsTheTotalLengthAndStillReadsTwoLineFiles()
    {
        var path = Path.GetTempFileName();
        try
        {
            var expected = new DownloadResumeValidator("\"entity-v1\"", new DateTimeOffset(2025, 8, 18, 14, 27, 5, TimeSpan.Zero), 1_400_000_000);
            await expected.SaveAsync(path);
            Assert.Equal(3, (await File.ReadAllLinesAsync(path)).Length);
            Assert.Equal(expected, await DownloadResumeValidator.LoadAsync(path));
            Assert.Equal(expected, DownloadResumeValidator.Load(path));

            // 旧版本写的两行文件(真实格式：Base64 的 ETag 和 Last-Modified)
            await File.WriteAllTextAsync(path, "IjA5Q0E4MDUyMTNDNzM2RDM4MkJDMDVDQjU3M0E0MUU4Ig==\n2025-08-18T14:27:05.0000000+00:00\n");
            var legacy = DownloadResumeValidator.Load(path)!;
            Assert.Equal("\"09CA805213C736D382BC05CB573A41E8\"", legacy.EntityTag);
            Assert.Null(legacy.TotalLength);

            await File.WriteAllTextAsync(path, "garbage");
            Assert.Null(DownloadResumeValidator.Load(path));
            Assert.Null(DownloadResumeValidator.Load(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SameEntity_ComparesValidatorsAndKnownTotalLengthsButNotUrls()
    {
        var lastModified = new DateTimeOffset(2025, 8, 18, 0, 0, 0, TimeSpan.Zero);
        var remote = new DownloadResumeValidator("\"e1\"", lastModified, 700);

        Assert.True(new DownloadResumeValidator("\"e1\"", null).SameEntity(remote));
        Assert.True(new DownloadResumeValidator("\"e1\"", lastModified, 700).SameEntity(remote));
        Assert.False(new DownloadResumeValidator("\"e1\"", lastModified, 701).SameEntity(remote));
        Assert.False(new DownloadResumeValidator("\"e2\"", lastModified, 700).SameEntity(remote));
        Assert.False(new DownloadResumeValidator(null, lastModified).SameEntity(remote));
        Assert.True(new DownloadResumeValidator(null, lastModified).SameEntity(new DownloadResumeValidator(null, lastModified, 9)));
        Assert.False(new DownloadResumeValidator(null, null).SameEntity(remote));
        Assert.False(remote.SameEntity(null));
    }

    private static HttpResponseMessage CreateResponse(string entityTag)
    {
        return new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Headers = { ETag = EntityTagHeaderValue.Parse(entityTag) },
            Content = new ByteArrayContent([])
        };
    }
}
