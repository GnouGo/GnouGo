using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CodePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-code-mcp-tests-" + Guid.NewGuid().ToString("N"));

    public CodePolicyTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sample.cs"), "public sealed class Sample {}\n");
        File.WriteAllText(Path.Combine(_root, "secret.bin"), "binary");
    }

    [Fact]
    public void ResolveReadableFile_AllowsFilesInsideRoot()
    {
        var policy = CreatePolicy();

        var file = policy.ResolveReadableFile(".", "sample.cs");

        Assert.Equal(Path.Combine(_root, "sample.cs"), file);
    }

    [Fact]
    public void ResolveReadableFile_RejectsTraversal()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveReadableFile(".", "..\\outside.cs"));

        Assert.Contains("parent traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveReadableFile_RejectsDisallowedExtension()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveReadableFile(".", "secret.bin"));

        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveConfiguredToken_PrefersSettingsThenEnvironment()
    {
        var settings = CreateSettings();
        settings.Copilot.ApiKey = "from-settings";
        var policy = new CodePolicy(settings, _root);

        Assert.Equal("from-settings", policy.ResolveConfiguredToken());
    }

    [Fact]
    public void WriteFile_IsDisabledByDefault()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveWritableFile(".", "new.cs"));

        Assert.Contains("Writes are disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultWorkingDirectory_UsesDesktopGnOuGoWhenConfiguredPathIsRelative()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = "GnOuGo";
        settings.AllowedWorkingRoots = [];

        var policy = new CodePolicy(settings, _root, desktop);
        var expected = Path.GetFullPath(Path.Combine(desktop, "GnOuGo"));

        Assert.Equal(expected, policy.DefaultWorkingDirectory);
        Assert.True(Directory.Exists(expected));
        Assert.Contains(expected, policy.DescribePolicy().AllowedWorkingRoots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultWorkingDirectory_UsesDesktopGnOuGoWhenNotConfigured()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = string.Empty;
        settings.AllowedWorkingRoots = [];

        var policy = new CodePolicy(settings, _root, desktop);
        var expected = Path.GetFullPath(Path.Combine(desktop, "GnOuGo"));

        Assert.Equal(expected, policy.DefaultWorkingDirectory);
        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public void ResolveProjectRoot_ResolvesRelativePathUnderDefaultWorkingDirectory()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = "GnOuGo";
        settings.AllowedWorkingRoots = [];
        var expectedProjectRoot = Path.GetFullPath(Path.Combine(desktop, "GnOuGo", "workspace", "oidc-client"));
        Directory.CreateDirectory(expectedProjectRoot);
        var policy = new CodePolicy(settings, _root, desktop);

        var projectRoot = policy.ResolveProjectRoot("workspace/oidc-client");

        Assert.Equal(expectedProjectRoot, projectRoot);
    }

    [Fact]
    public void ResolveProjectRoot_RejectsExplicitEmptyString()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(""));

        Assert.Contains("projectRoot is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectRoot_RejectsNull()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(null!));

        Assert.Contains("projectRoot is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectRoot_RejectsAbsolutePath()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(_root));

        Assert.Contains("must be relative", ex.Message, StringComparison.OrdinalIgnoreCase);
    }


    private CodePolicy CreatePolicy() => new(CreateSettings(), _root);

    private CodeServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowedExtensions = [".cs", ".md"],
        MaxFileSizeBytes = 1024 * 1024,
        AllowWrites = false,
        Copilot = new CodeCopilotSettings
        {
            ApiKey = null,
            TokenEnvironmentVariables = ["GNOU_GO_CODE_TEST_TOKEN"]
        }
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
