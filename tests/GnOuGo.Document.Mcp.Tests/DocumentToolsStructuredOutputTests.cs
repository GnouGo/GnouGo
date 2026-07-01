using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.Document.Mcp.Tests;

public sealed class DocumentToolsStructuredOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-document-tools-structured-output-tests-" + Guid.NewGuid().ToString("N"));

    public DocumentToolsStructuredOutputTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AllDocumentMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(DocumentTools)
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
        services.AddSingleton(new DocumentPolicy(settings, _root));
        services.AddSingleton<DocumentOperationHost>();
        services.AddTransient<DocumentTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Document.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<DocumentTools>(DocumentMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    private DocumentServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowedExtensions = [".txt", ".md", ".csv", ".json"],
        MaxFileSizeBytes = 1024 * 1024
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
