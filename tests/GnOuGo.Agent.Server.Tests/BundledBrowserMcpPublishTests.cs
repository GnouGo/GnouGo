
namespace GnOuGo.Agent.Server.Tests;

public sealed class BundledBrowserMcpPublishTests
{
    [Theory]
    [InlineData("src", "GnOuGo.Agent.Desktop", "GnOuGo.Agent.Desktop.csproj")]
    [InlineData("src", "GnOuGo.Agent.Server", "GnOuGo.Agent.Server.csproj")]
    public void BrowserMcpBundlePublish_DisablesTrimAndSingleFile(string folder, string project, string fileName)
    {
        var projectFile = Path.Combine(GetRepositoryRoot(), folder, project, fileName);
        var xml = File.ReadAllText(projectFile);

        var browserPublishCommand = xml
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.Contains("dotnet publish &quot;$(BundledBrowserToolProject)&quot;", StringComparison.Ordinal));

        Assert.NotNull(browserPublishCommand);
        Assert.Contains("-p:PublishTrimmed=false", browserPublishCommand);
        Assert.Contains("-p:PublishSingleFile=false", browserPublishCommand);
        Assert.DoesNotContain("-p:PublishTrimmed=true", browserPublishCommand);
        Assert.DoesNotContain("-p:PublishSingleFile=true", browserPublishCommand);
    }

    [Fact]
    public void DesktopTrimmedWorkflow_ValidatesPlaywrightNodeDriver()
    {
        var workflowFile = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "build-agent-desktop-trimmed.yml");
        var yaml = File.ReadAllText(workflowFile);

        Assert.Contains(".playwright", yaml);
        Assert.Contains("Missing bundled Playwright Node driver", yaml);
        Assert.Contains("node.exe", yaml);
    }

    private static string GetRepositoryRoot()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));

        Assert.True(File.Exists(Path.Combine(root, "GnOuGo.Agent.sln")), $"Repository root not found from {AppContext.BaseDirectory}.");
        return root;
    }
}



