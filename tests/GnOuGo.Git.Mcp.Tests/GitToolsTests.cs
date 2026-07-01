using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.Git.Mcp.Tests;

public sealed class GitToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-git-tools-tests-" + Guid.NewGuid().ToString("N"));

    public GitToolsTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ParseGitPaths_RemovesEmptyAndDuplicateValues()
    {
        var paths = GitTools.ParseGitPaths("[\"src/Program.cs\", \"\", \"src/Program.cs\", \"README.md\"]");

        Assert.Equal(["src/Program.cs", "README.md"], paths);
    }

    [Fact]
    public void GetPolicy_ReturnsConfiguredGitPolicy()
    {
        var settings = CreateSettings();
        settings.AllowMutations = true;
        settings.AllowNetworkOperations = true;
        var policy = new GitPolicy(settings, _root);
        var tools = new GitTools(policy, new GitRepositoryService(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);

        var result = tools.GetPolicy();

        Assert.True(result.AllowMutations);
        Assert.True(result.AllowNetworkOperations);
        Assert.Contains(_root, result.AllowedWorkingRoots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitStatus_ReturnsPolicyErrorForNonRepository()
    {
        var settings = CreateSettings();
        var policy = new GitPolicy(settings, _root);
        var tools = new GitTools(policy, new GitRepositoryService(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);

        var error = Assert.Throws<ModelContextProtocol.McpException>(() => tools.GitStatus("."));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void GitStage_ReturnsPolicyErrorForInvalidPathsJson()
    {
        var settings = CreateSettings();
        var policy = new GitPolicy(settings, _root);
        var tools = new GitTools(policy, new GitRepositoryService(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);

        var error = Assert.Throws<ModelContextProtocol.McpException>(() => tools.GitStage(".", "{"));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void McpToolRegistration_CreatesToolDescriptorsWithGitJsonContext()
    {
        var settings = CreateSettings();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(settings));
        services.AddSingleton(new GitPolicy(settings, _root));
        services.AddSingleton<GitRepositoryService>();
        services.AddTransient<GitTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Git.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<GitTools>(GitMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    private GitServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowMutations = true,
        AllowNetworkOperations = false
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
