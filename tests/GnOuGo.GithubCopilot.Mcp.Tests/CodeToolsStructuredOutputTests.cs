using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using GnOuGo.GithubCopilot.Core;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CodeToolsStructuredOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-code-tools-structured-output-tests-" + Guid.NewGuid().ToString("N"));

    public CodeToolsStructuredOutputTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AllCodeMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(CodeTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(item => item.Attribute != null)
            .ToArray();

        Assert.NotEmpty(toolMethods);

        foreach (var item in toolMethods)
        {
            Assert.True(item.Attribute!.UseStructuredContent, item.Method.Name);
            Assert.NotNull(item.Attribute.OutputSchemaType);
            Assert.NotEqual(typeof(object), item.Method.ReturnType);
            Assert.Equal(UnwrapToolReturnType(item.Method.ReturnType), item.Attribute.OutputSchemaType);
        }
    }

    [Fact]
    public void McpToolRegistration_CreatesToolDescriptorsWithOutputSchemas()
    {
        var settings = CreateSettings();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(settings));
        services.AddSingleton(new CodePolicy(settings, _root));
        services.AddSingleton<CodeProjectService>();
        services.AddSingleton<ICodeAssistantClient, NoopAssistantClient>();
        services.AddTransient<CodeTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.GithubCopilot.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<CodeTools>(CodeMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    [Fact]
    public void ExistingReviewComments_AcceptsDirectArraysAndThreadEnvelopes()
    {
        var parser = typeof(CopilotTools).GetMethod(
            "ParseExistingReviewComments",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(parser);

        static IReadOnlyList<ExistingReviewComment> Parse(MethodInfo method, string json) =>
            Assert.IsAssignableFrom<IReadOnlyList<ExistingReviewComment>>(method.Invoke(null, [json]));

        var direct = Parse(parser!, """
            [{"path":"src/a.cs","side":"Right","startLine":4,"endLine":5,"body":"Existing issue","fingerprint":"fp-1"}]
            """);
        var enveloped = Parse(parser!, """
            {
              "review_threads": [
                {
                  "path": "src/b.cs",
                  "side": "RIGHT",
                  "line": 17,
                  "comments": [{"body":"Thread comment"}]
                }
              ],
              "totalCount": 1,
              "pageInfo": {"hasNextPage": false}
            }
            """);
        var emptyEnvelope = Parse(parser!, """
            {"review_threads":[],"totalCount":0,"pageInfo":{"hasNextPage":false}}
            """);
        var absent = Parse(parser!, "null");

        Assert.Single(direct);
        Assert.Equal("fp-1", direct[0].Fingerprint);
        var thread = Assert.Single(enveloped);
        Assert.Equal("src/b.cs", thread.Path);
        Assert.Equal(17, thread.EndLine);
        Assert.Equal(ReviewDiffSide.Right, thread.Side);
        Assert.Equal("Thread comment", thread.Body);
        Assert.Empty(emptyEnvelope);
        Assert.Empty(absent);
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    private CodeServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowedExtensions = [".cs", ".md"],
        MaxFileSizeBytes = 1024 * 1024,
        MaxPromptCharacters = 24_000,
        AllowWrites = false
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class NoopAssistantClient : ICodeAssistantClient
    {
        public Task<CodeSuggestionResult> SuggestChangeAsync(
            string task,
            string projectRoot,
            IReadOnlyList<CodeFileContent> contextFiles,
            string? providerName,
            CancellationToken cancellationToken)
            => Task.FromResult(new CodeSuggestionResult(task, [], "", null, null, []));

        public Task<CodeAgentEditResult> AgentEditAsync(
            string task,
            string projectRoot,
            IReadOnlyList<CodeFileContent> contextFiles,
            string? providerName,
            CancellationToken cancellationToken)
            => Task.FromResult(new CodeAgentEditResult(task, [], [], "", null, null, []));
    }
}
