
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


        var browserPublishCommand = GetToolPublishCommand(xml, "BundledBrowserToolProject");

        Assert.NotNull(browserPublishCommand);
        Assert.Contains("-p:PublishTrimmed=false", browserPublishCommand);
        Assert.Contains("-p:PublishSingleFile=false", browserPublishCommand);
        Assert.DoesNotContain("-p:PublishTrimmed=true", browserPublishCommand);
        Assert.DoesNotContain("-p:PublishSingleFile=true", browserPublishCommand);
    }

    [Theory]
    [InlineData("src", "GnOuGo.Agent.Desktop", "GnOuGo.Agent.Desktop.csproj")]
    [InlineData("src", "GnOuGo.Agent.Server", "GnOuGo.Agent.Server.csproj")]
    public void BundledStdioMcpTools_DisableTrimAndSingleFile_ForReflectionBasedSchemas(string folder, string project, string fileName)
    {
        var projectFile = Path.Combine(GetRepositoryRoot(), folder, project, fileName);
        var xml = File.ReadAllText(projectFile);
        var bundledToolProjectProperties = new[]
        {
            "BundledBrowserToolProject",
            "BundledCmdToolProject",
            "BundledDocumentToolProject",
            "BundledCodeToolProject"
        };

        foreach (var toolProjectProperty in bundledToolProjectProperties)
        {
            var publishCommand = GetToolPublishCommand(xml, toolProjectProperty);

            Assert.NotNull(publishCommand);
            Assert.Contains("-p:PublishTrimmed=false", publishCommand);
            Assert.Contains("-p:PublishSingleFile=false", publishCommand);
            Assert.Contains("-p:PublishAot=false", publishCommand);
            Assert.DoesNotContain("-p:PublishTrimmed=true", publishCommand);
            Assert.DoesNotContain("-p:PublishSingleFile=true", publishCommand);
        }
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

    [Fact]
    public void DesktopTrimmedWorkflow_PackagesPublicGnougoReleaseArchives()
    {
        var workflowFile = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "build-agent-desktop-trimmed.yml");
        var yaml = File.ReadAllText(workflowFile);

        Assert.Contains("rid: win-x64", yaml);
        Assert.Contains("archive: gnougo-win-x64.zip", yaml);
        Assert.Contains("rid: win-arm64", yaml);
        Assert.Contains("archive: gnougo-win-arm64.zip", yaml);
        Assert.Contains("rid: osx-arm64", yaml);
        Assert.Contains("archive: gnougo-osx-arm64.tar.gz", yaml);
        Assert.Contains("rid: osx-x64", yaml);
        Assert.Contains("archive: gnougo-osx-x64.tar.gz", yaml);
        Assert.Contains("artifacts/package/gnougo.app", yaml);
    }

    [Fact]
    public void ReleaseWorkflow_PublishesArchivesAndChecksums()
    {
        var workflowFile = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "publish-github-release.yml");
        var yaml = File.ReadAllText(workflowFile);

        Assert.Contains("Generate release checksums", yaml);
        Assert.Contains("Get-FileHash -Algorithm SHA256", yaml);
        Assert.Contains("release-assets/**/*.zip", yaml);
        Assert.Contains("release-assets/**/*.tar.gz", yaml);
        Assert.Contains("release-assets/checksums.txt", yaml);
    }

    [Fact]
    public void PackageManagerTemplates_UsePublicPackageNames()
    {
        var root = GetRepositoryRoot();
        var wingetInstallerFile = Path.Combine(root, "packaging", "winget", "GnouGo.GnouGo", "GnouGo.GnouGo.installer.yaml");
        var wingetLocaleFile = Path.Combine(root, "packaging", "winget", "GnouGo.GnouGo", "GnouGo.GnouGo.locale.en-US.yaml");
        var homebrewCaskFile = Path.Combine(root, "packaging", "homebrew-tap", "Casks", "gnougo.rb");
        var readmeFile = Path.Combine(root, "README.md");

        var wingetInstaller = File.ReadAllText(wingetInstallerFile);
        var wingetLocale = File.ReadAllText(wingetLocaleFile);
        var homebrewCask = File.ReadAllText(homebrewCaskFile);
        var readme = File.ReadAllText(readmeFile);

        Assert.Contains("PackageIdentifier: GnouGo.GnouGo", wingetInstaller);
        Assert.Contains("PackageName: gnougo", wingetLocale);
        Assert.Contains("gnougo-win-x64.zip", wingetInstaller);
        Assert.Contains("gnougo-win-arm64.zip", wingetInstaller);
        Assert.Contains("cask \"gnougo\"", homebrewCask);
        Assert.Contains("gnougo-osx-#{arch}.tar.gz", homebrewCask);
        Assert.Contains("app \"gnougo.app\"", homebrewCask);
        Assert.Contains("winget install GnouGo.GnouGo", readme);
        Assert.Contains("brew install --cask gnougo", readme);
    }

    private static string GetRepositoryRoot()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));

        Assert.True(File.Exists(Path.Combine(root, "GnOuGo.Agent.sln")), $"Repository root not found from {AppContext.BaseDirectory}.");
        return root;
    }

    private static string? GetToolPublishCommand(string xml, string bundledToolProjectProperty)
        => xml
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.Contains($"dotnet publish &quot;$({bundledToolProjectProperty})&quot;", StringComparison.Ordinal));
}


