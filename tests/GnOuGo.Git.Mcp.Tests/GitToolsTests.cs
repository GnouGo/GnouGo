using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GnOuGo.Mcp.Core;
using System.Text.Json.Nodes;
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

        var error = tools.GitStatus(".");

        Assert.False(error.Success);
        Assert.False(error.Ok);
        Assert.Equal("INVALID_INPUT", error.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(error.ErrorMessage));
    }

    [Fact]
    public void GitStage_ReturnsPolicyErrorForInvalidPathsJson()
    {
        var settings = CreateSettings();
        var policy = new GitPolicy(settings, _root);
        var tools = new GitTools(policy, new GitRepositoryService(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);

        var error = tools.GitStage(".", "{");

        Assert.False(error.Success);
        Assert.False(error.Ok);
        Assert.Equal("INVALID_INPUT", error.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(error.ErrorMessage));
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
        Assert.All(tools, tool => AssertValidRequiredDeclarations(
            JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText()),
            tool.ProtocolTool.Name));
        var compare = Assert.Single(tools, static tool =>
            string.Equals(tool.ProtocolTool.Name, "git_compare_refs", StringComparison.Ordinal));
        var artifactValidation = McpArtifactContractParser.ParseAndValidate(
            compare.ProtocolTool.Meta,
            JsonNode.Parse(compare.ProtocolTool.InputSchema.GetRawText()),
            JsonNode.Parse(compare.ProtocolTool.OutputSchema!.Value.GetRawText()));
        Assert.True(artifactValidation.IsValid, string.Join(Environment.NewLine, artifactValidation.Errors));
    }

    private static void AssertValidRequiredDeclarations(JsonNode? schema, string path)
    {
        if (schema is not JsonObject obj)
            return;

        var properties = obj["properties"] as JsonObject;
        if (obj["required"] is JsonArray required)
        {
            Assert.NotNull(properties);
            var names = required.Select((node, index) =>
            {
                var value = Assert.IsAssignableFrom<JsonValue>(node);
                Assert.True(value.TryGetValue<string>(out var name), $"{path}.required[{index}] must be a string.");
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.True(properties!.ContainsKey(name!), $"{path}.required[{index}] references undeclared property '{name}'.");
                return name!;
            }).ToArray();
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }

        if (properties != null)
            foreach (var (name, property) in properties)
                AssertValidRequiredDeclarations(property, $"{path}.properties.{name}");
        foreach (var keyword in new[] { "$defs", "definitions" })
            if (obj[keyword] is JsonObject definitions)
                foreach (var (name, definition) in definitions)
                    AssertValidRequiredDeclarations(definition, $"{path}.{keyword}.{name}");
        foreach (var keyword in new[] { "items", "additionalProperties" })
            AssertValidRequiredDeclarations(obj[keyword], $"{path}.{keyword}");
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            if (obj[keyword] is JsonArray variants)
                for (var index = 0; index < variants.Count; index++)
                    AssertValidRequiredDeclarations(variants[index], $"{path}.{keyword}[{index}]");
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
