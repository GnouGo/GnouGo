
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
    public void BuildAndRuntimeConfig_UseGithubCopilotMcpAfterRename()
    {
        var root = GetRepositoryRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "GnOuGo.Agent.Desktop", "GnOuGo.Agent.Desktop.csproj"),
            Path.Combine(root, "src", "GnOuGo.Agent.Server", "GnOuGo.Agent.Server.csproj"),
            Path.Combine(root, "src", "GnOuGo.Agent.Server", "appsettings.json"),
            Path.Combine(root, "src", "GnOuGo.Agent.Server", "appsettings.Desktop.json"),
            Path.Combine(root, ".github", "workflows", "build-agent-desktop-trimmed.yml"),
            Path.Combine(root, ".github", "workflows", "build-agent-server-linux-x64.yml")
        }.ToList();

        var developmentSettings = Path.Combine(root, "src", "GnOuGo.Agent.Server", "appsettings.Development.json");
        if (File.Exists(developmentSettings))
            files.Add(developmentSettings);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.Contains("GnOuGo.GithubCopilot.Mcp", text);
            Assert.DoesNotContain("GnOuGo.Code.Mcp", text);
        }
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
        Assert.Contains("rid: linux-x64", yaml);
        Assert.Contains("archive: gnougo-linux-x64.tar.gz", yaml);
        Assert.Contains("deb_arch: amd64", yaml);
        Assert.Contains("rid: linux-arm64", yaml);
        Assert.Contains("archive: gnougo-linux-arm64.tar.gz", yaml);
        Assert.Contains("deb_arch: arm64", yaml);
        Assert.Contains("rid: osx-arm64", yaml);
        Assert.Contains("archive: gnougo-osx-arm64.tar.gz", yaml);
        Assert.Contains("rid: osx-x64", yaml);
        Assert.Contains("archive: gnougo-osx-x64.tar.gz", yaml);
        Assert.Contains("artifacts/package/gnougo.app", yaml);
        Assert.Contains("dpkg-deb --build", yaml);
        Assert.Contains("inputs.package_version", yaml);
        Assert.Contains("public command name gnougo", yaml);
        Assert.Contains("gnougo_${packageVersion}_${{ matrix.deb_arch }}.deb", yaml);
    }

    [Fact]
    public void ReleaseWorkflow_PublishesArchivesAndChecksums()
    {
        var workflowFile = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "publish-github-release.yml");
        var yaml = File.ReadAllText(workflowFile);

        Assert.Contains("Generate release checksums", yaml);
        Assert.Contains("pattern: \"!*.dockerbuild\"", yaml);
        Assert.Contains("Get-FileHash -Algorithm SHA256", yaml);
        Assert.Contains("release-assets/**/*.zip", yaml);
        Assert.Contains("release-assets/**/*.tar.gz", yaml);
        Assert.Contains("release-assets/**/*.deb", yaml);
        Assert.Contains("release-assets/checksums.txt", yaml);
        Assert.Contains("publish_winget:", yaml);
        Assert.Contains("needs: publish_release_main", yaml);
        Assert.Contains("if: inputs.channel_tag == 'release'", yaml);
        Assert.Contains("actions: read", yaml);
        Assert.Contains("wingetcreate.exe update GnOuGo.Agent", yaml);
        Assert.Contains("secrets.WINGET_CREATE_GITHUB_TOKEN", yaml);
        Assert.Contains("publish_homebrew:", yaml);
        Assert.Contains("repository: GnouGo/homebrew-tap", yaml);
        Assert.Contains("secrets.HOMEBREW_TAP_GITHUB_TOKEN", yaml);
        Assert.Contains("gnougo-osx-arm64.tar.gz", yaml);
        Assert.Contains("gnougo-osx-x64.tar.gz", yaml);
        Assert.Contains("desc \"The Friendly Bear Agent\"", yaml);
        Assert.Contains("git push", yaml);
    }

    [Fact]
    public void PackageManagerTemplates_UsePublicPackageNames()
    {
        var root = GetRepositoryRoot();
        var wingetVersionFile = Path.Combine(root, "packaging", "winget", "GnOuGo.Agent", "GnOuGo.Agent.yaml");
        var wingetInstallerFile = Path.Combine(root, "packaging", "winget", "GnOuGo.Agent", "GnOuGo.Agent.installer.yaml");
        var wingetLocaleFile = Path.Combine(root, "packaging", "winget", "GnOuGo.Agent", "GnOuGo.Agent.locale.en-US.yaml");
        var homebrewCaskFile = Path.Combine(root, "packaging", "homebrew-tap", "Casks", "gnougo.rb");
        var readmeFile = Path.Combine(root, "README.md");

        var wingetVersion = File.ReadAllText(wingetVersionFile);
        var wingetInstaller = File.ReadAllText(wingetInstallerFile);
        var wingetLocale = File.ReadAllText(wingetLocaleFile);
        var homebrewCask = File.ReadAllText(homebrewCaskFile);
        var readme = File.ReadAllText(readmeFile);

        Assert.Contains("PackageIdentifier: GnOuGo.Agent", wingetVersion);
        Assert.Contains("PackageIdentifier: GnOuGo.Agent", wingetInstaller);
        Assert.Contains("PackageIdentifier: GnOuGo.Agent", wingetLocale);
        Assert.Contains("PackageName: GnOuGo", wingetLocale);
        Assert.Contains("Moniker: gnougo", wingetLocale);
        Assert.Contains("ShortDescription: The Friendly Bear AI Agent", wingetLocale);
        Assert.Contains("gnougo-win-x64.zip", wingetInstaller);
        Assert.Contains("gnougo-win-arm64.zip", wingetInstaller);
        Assert.Contains("cask \"gnougo\"", homebrewCask);
        Assert.Contains("gnougo-osx-#{arch}.tar.gz", homebrewCask);
        Assert.Contains("desc \"The Friendly Bear Agent\"", homebrewCask);
        Assert.Contains("app \"gnougo.app\"", homebrewCask);
        Assert.Contains("winget install GnOuGo.Agent", readme);
        Assert.Contains("brew install --cask gnougo", readme);
        Assert.Contains("Download the `gnougo-linux-*.tar.gz` archive from the GitHub Release first", readme);
        Assert.Contains("Download the matching `.deb` package from the GitHub Release first", readme);
        Assert.Contains("tar -xzf gnougo-linux-x64.tar.gz", readme);
        Assert.Contains("sudo apt install ./gnougo_*_amd64.deb", readme);
    }

    [Fact]
    public void MainWorkflow_PassesPackagePublishingSecrets()
    {
        var workflowFile = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "main.yaml");
        var yaml = File.ReadAllText(workflowFile);

        Assert.Contains("uses: ./.github/workflows/publish-github-release.yml", yaml);
        Assert.Contains("needs.tags.outputs.is_release == 'true'", yaml);
        Assert.Contains("actions: read", yaml);
        Assert.Contains("WINGET_CREATE_GITHUB_TOKEN: ${{ secrets.WINGET_CREATE_GITHUB_TOKEN }}", yaml);
        Assert.Contains("HOMEBREW_TAP_GITHUB_TOKEN: ${{ secrets.HOMEBREW_TAP_GITHUB_TOKEN }}", yaml);
    }

    [Fact]
    public void VersionWorkflow_CreatesReleaseTagAndChangelogOnlyForExplicitReleaseMarker()
    {
        var root = GetRepositoryRoot();
        var workflowFile = Path.Combine(root, ".github", "workflows", "compute-version-tag.yml");
        var yaml = File.ReadAllText(workflowFile);
        var changelogScript = File.ReadAllText(Path.Combine(root, "scripts", "generate-changelog.sh"));

        Assert.Contains("*'(release)'*", yaml);
        Assert.Contains("is_release: ${{ steps.tag.outputs.is_release }}", yaml);
        Assert.Contains("token: ${{ secrets.GIT_TOKEN || github.token }}", yaml);
        Assert.Contains("Publish changelog update to main", yaml);
        Assert.Contains("git fetch origin main", yaml);
        Assert.Contains("git push --force-with-lease origin \"HEAD:refs/heads/main\"", yaml);
        Assert.Contains("bash scripts/generate-changelog.sh", yaml);
        Assert.Contains("docs: update changelog for ${{ steps.tag.outputs.version_tag }} [skip ci]", yaml);
        Assert.Contains("Push release tag", yaml);
        Assert.Contains("git push origin \"refs/tags/$version_tag\"", yaml);
        Assert.DoesNotContain("peter-evans/create-pull-request@v6", yaml);
        Assert.DoesNotContain("Create changelog pull request", yaml);
        Assert.DoesNotContain("release/changelog/${{ steps.tag.outputs.version_tag }}", yaml);
        Assert.DoesNotContain("id: tag_release", yaml);

        Assert.Contains("Usage: scripts/generate-changelog.sh <version-tag> [output-file]", changelogScript);
        Assert.Contains("previous_tag = git(\"describe\", \"--tags\", \"--abbrev=0\", \"--match\", \"v[0-9]*\", check=False)", changelogScript);
    }


    [Fact]
    public void AgentDockerfile_AllowsRestoreDuringPublishForGeneratedOtlpProtos()
    {
        var dockerfile = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "src", "GnOuGo.Agent.Server", "Dockerfile"));

        Assert.Contains("GnOuGo.Observability.Core.csproj", dockerfile);
        Assert.Contains("Grpc.Tools must be allowed to refresh generated inputs", dockerfile);
        Assert.DoesNotContain("    --no-restore", dockerfile);
    }

    [Fact]
    public void OtlpCollectorDockerfile_BuildsClientAppAndPublishesGeneratedWwwroot()
    {
        var dockerfile = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "src", "GnOuGo.OtlpCollector.Server", "Dockerfile"));

        Assert.Contains("RUN pnpm --dir src/GnOuGo.OtlpCollector.Server/ClientApp build", dockerfile);
        Assert.Contains("COPY --from=clientapp /workspace/src/GnOuGo.OtlpCollector.Server/wwwroot src/GnOuGo.OtlpCollector.Server/wwwroot/", dockerfile);
        Assert.Contains("RUN test -f src/GnOuGo.OtlpCollector.Server/wwwroot/index.html", dockerfile);
        Assert.Contains("-p:SkipClientBuild=true", dockerfile);
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
