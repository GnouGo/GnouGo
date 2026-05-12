using Xunit;

namespace GnOuGo.Git.Mcp.Tests;

public sealed class GitPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-git-policy-tests-" + Guid.NewGuid().ToString("N"));

    public GitPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DefaultWorkingDirectory_UsesDesktopGnOuGoWhenConfiguredPathIsRelative()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = "GnOuGo";
        settings.AllowedWorkingRoots = [];

        var policy = new GitPolicy(settings, _root, desktop);
        var expected = Path.GetFullPath(Path.Combine(desktop, "GnOuGo"));

        Assert.Equal(expected, policy.DefaultWorkingDirectory);
        Assert.True(Directory.Exists(expected));
        Assert.Contains(expected, policy.DescribePolicy().AllowedWorkingRoots, StringComparer.OrdinalIgnoreCase);
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
        var policy = new GitPolicy(settings, _root, desktop);

        var projectRoot = policy.ResolveProjectRoot("workspace/oidc-client");

        Assert.Equal(expectedProjectRoot, projectRoot);
    }

    [Fact]
    public void ResolveCloneTargetDirectory_ResolvesRelativeTargetsUnderDefaultWorkingDirectory()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = "GnOuGo";
        settings.AllowedWorkingRoots = [];
        var policy = new GitPolicy(settings, _root, desktop);

        var target = policy.ResolveCloneTargetDirectory("sample-repository");

        Assert.Equal(Path.GetFullPath(Path.Combine(desktop, "GnOuGo", "sample-repository")), target);
    }

    [Fact]
    public void ResolveCloneTargetDirectory_RejectsTraversalOutsideDefaultWorkingDirectory()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = "GnOuGo";
        settings.AllowedWorkingRoots = [];
        var policy = new GitPolicy(settings, _root, desktop);

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveCloneTargetDirectory("..\\outside"));

        Assert.Contains("parent traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveGitToken_PrefersSettingsThenEnvironment()
    {
        var settings = CreateSettings();
        settings.Token = "from-settings";
        var policy = new GitPolicy(settings, _root);

        Assert.Equal("from-settings", policy.ResolveGitToken());
    }

    private GitServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowMutations = false,
        AllowNetworkOperations = false,
        TokenEnvironmentVariables = ["GNOU_GO_GIT_TEST_TOKEN"]
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

