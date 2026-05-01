using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Code.Mcp.Tests;

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

        var file = policy.ResolveReadableFile(_root, "sample.cs");

        Assert.Equal(Path.Combine(_root, "sample.cs"), file);
    }

    [Fact]
    public void ResolveReadableFile_RejectsTraversal()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveReadableFile(_root, "..\\outside.cs"));

        Assert.Contains("parent traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveReadableFile_RejectsDisallowedExtension()
    {
        var policy = CreatePolicy();

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveReadableFile(_root, "secret.bin"));

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

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveWritableFile(_root, "new.cs"));

        Assert.Contains("Writes are disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        catch { }
    }
}


