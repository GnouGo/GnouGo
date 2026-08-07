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
    public void ResolveProjectRoot_RejectsNull()
    {
        var policy = new GitPolicy(CreateSettings(), _root);

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(null!));

        Assert.Contains("projectRoot is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectRoot_RejectsExplicitEmptyString()
    {
        var policy = new GitPolicy(CreateSettings(), _root);

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(""));

        Assert.Contains("projectRoot is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectRoot_RejectsAbsolutePath()
    {
        var policy = new GitPolicy(CreateSettings(), _root);

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(_root));

        Assert.Contains("must be relative", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCloneTargetDirectory_ResolvesRelativeTargetsUnderDefaultWorkingDirectory()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var settings = CreateSettings();
        settings.DefaultWorkingDirectory = "GnOuGo";
        settings.AllowedWorkingRoots = [];
        var policy = new GitPolicy(settings, _root, desktop);

        var target = policy.ResolveCloneTargetDirectory("workflows/sample-repository");

        Assert.Equal(Path.GetFullPath(Path.Combine(desktop, "GnOuGo", "workflows", "sample-repository")), target);
        Assert.True(Directory.Exists(Path.Combine(desktop, "GnOuGo", "workflows")));
    }

    [Fact]
    public void ResolveCloneTargetDirectory_AllowsAbsoluteTargetInsideAllowedRoots()
    {
        var policy = new GitPolicy(CreateSettings(), _root);
        var absoluteTarget = Path.GetFullPath(Path.Combine(_root, "workflows", "issue-544"));

        var target = policy.ResolveCloneTargetDirectory(absoluteTarget);

        Assert.Equal(absoluteTarget, target);
    }

    [Fact]
    public void ResolveCloneTargetDirectory_RejectsAbsoluteTargetOutsideAllowedRoots()
    {
        var policy = new GitPolicy(CreateSettings(), _root);
        var outsideTarget = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gnougo-git-policy-outside-" + Guid.NewGuid().ToString("N")));

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveCloneTargetDirectory(outsideTarget));

        Assert.Contains("outside the allowed roots", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("reviews/repository")]
    [InlineData("workflows")]
    [InlineData(".GnOuGo/data/reviews/repository")]
    public void ResolveCloneTargetDirectory_RejectsTargetsOutsideWorkflowWorkspace(string relativePath)
    {
        var policy = new GitPolicy(CreateSettings(), _root);

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveCloneTargetDirectory(relativePath));

        Assert.True(
            ex.Message.Contains("workflow workspace", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveCloneTargetDirectory_ReportsReservedPolicyBeforeLegacyDirectoryState()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_root, ".GnOuGo", "data", "reviews", "repository"));
        File.WriteAllText(Path.Combine(legacyDirectory.FullName, "existing.txt"), "legacy");
        var policy = new GitPolicy(CreateSettings(), _root);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.ResolveCloneTargetDirectory(".GnOuGo/data/reviews/repository"));

        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workflows/<name>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectRoot_RejectsReservedGnOuGoDirectory()
    {
        var internalRoot = Directory.CreateDirectory(Path.Combine(_root, ".GnOuGo", "data", "project")).FullName;
        var policy = new GitPolicy(CreateSettings(), _root);

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveProjectRoot(Path.GetRelativePath(_root, internalRoot)));

        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCloneTargetDirectory_RejectsNonEmptyTargetDirectory()
    {
        var policy = new GitPolicy(CreateSettings(), _root);
        var targetDirectory = Path.Combine(_root, "workflows", "existing-target");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "README.md"), "not empty");

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveCloneTargetDirectory(targetDirectory));

        Assert.Contains("already exists and is not empty", ex.Message, StringComparison.OrdinalIgnoreCase);
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
