using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
