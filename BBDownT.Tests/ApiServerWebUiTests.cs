using Microsoft.AspNetCore.Http;

namespace BBDownT.Tests;

public class ApiServerWebUiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bbdownt-webui-" + Guid.NewGuid().ToString("N"));

    public ApiServerWebUiTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
    }

    private BBDownTApiServer CreateServer() => new(new BBDownTServerOptions { DownloadRoot = root });

    [Theory]
    [InlineData("video.mp4")]
    [InlineData("sub/视频.mp4")]
    [InlineData("sub/../video.mp4")]
    public void ResolveDownloadPath_AcceptsPathsInsideRoot(string relativePath)
    {
        var fullPath = CreateServer().ResolveDownloadPath(relativePath);

        Assert.Equal(Path.GetFullPath(Path.Combine(root, relativePath)), fullPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("sub/../../secret.txt")]
    [InlineData("/etc/passwd")]
    public void ResolveDownloadPath_RejectsPathsOutsideRoot(string? relativePath)
    {
        Assert.Null(CreateServer().ResolveDownloadPath(relativePath));
    }

    [Fact]
    public void ResolveDownloadPath_RejectsSiblingDirectoryWithSamePrefix()
    {
        Assert.Null(CreateServer().ResolveDownloadPath("../" + Path.GetFileName(root) + "-other/video.mp4"));
    }

    [Fact]
    public void ListDownloadedFiles_ReturnsRelativePathsNewestFirst()
    {
        var older = Path.Combine(root, "sub", "a.mp4");
        var newer = Path.Combine(root, "b.m4a");
        File.WriteAllText(older, "a");
        File.WriteAllText(newer, "bb");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));

        var files = CreateServer().ListDownloadedFiles();

        Assert.Equal(["b.m4a", "sub/a.mp4"], files.Select(f => f.Path));
        Assert.Equal(2, files[0].Size);
    }

    [Theory]
    [InlineData("BBDownT.data", true)]
    [InlineData("BBDownTTV.data", true)]
    [InlineData("BBDownTApp.data", true)]
    [InlineData("BBDownT.config", true)]
    [InlineData("BBDownT.archives", true)]
    [InlineData("BBDownT.web.json", true)]
    [InlineData("BBDown.data", true)]
    [InlineData("BBDownT 教程.mp4", false)]
    [InlineData("video.mp4", false)]
    public void IsProtectedFile_ProtectsCredentialAndConfigFilesInAppDirectory(string fileName, bool expected)
    {
        Assert.Equal(expected, BBDownTApiServer.IsProtectedFile(Path.Combine(Program.APP_DIR, fileName)));
    }

    [Fact]
    public void IsProtectedFile_AllowsSameNameOutsideAppDirectory()
    {
        Assert.False(BBDownTApiServer.IsProtectedFile(Path.Combine(root, "BBDownT.data")));
    }

    [Theory]
    [InlineData("GET", "/", true)]
    [InlineData("GET", "/index.html", true)]
    [InlineData("POST", "/ui/session", true)]
    [InlineData("DELETE", "/ui/session", true)]
    [InlineData("GET", "/ui/status", false)]
    [InlineData("POST", "/", false)]
    [InlineData("GET", "/get-tasks/", false)]
    [InlineData("GET", "/files/download", false)]
    [InlineData("POST", "/ui/bili-login", false)]
    public void IsPublicPath_OnlyExemptsPageAndSessionEndpoint(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, BBDownTApiServer.IsPublicPath(context.Request));
    }

    [Fact]
    public void ResolveAppDir_UsesDataDirectoryWhenConfigured()
    {
        var dataDir = Path.Combine(root, "data");

        Assert.Equal(dataDir, Program.ResolveAppDir(dataDir, "/opt/bbdownt"));
        Assert.True(Directory.Exists(dataDir));
        Assert.Equal("/opt/bbdownt", Program.ResolveAppDir(" ", "/opt/bbdownt"));
    }
}
