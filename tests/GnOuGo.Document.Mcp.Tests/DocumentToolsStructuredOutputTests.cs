using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
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

    [Fact]
    public async Task SuccessfulWriteContractAndErrorsAreDisjointOverStdio()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = Environment.GetEnvironmentVariable("GNOUGO_DOCUMENT_MCP_TEST_EXECUTABLE")
                ?? Path.Combine(AppContext.BaseDirectory, "GnOuGo.Document.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")),
            Name = "document-contract", WorkingDirectory = _root,
            EnvironmentVariables = new Dictionary<string, string?> { ["Document__DefaultWorkingDirectory"] = _root, ["OpenTelemetry__Enabled"] = "false" }
        }), cancellationToken: ct);
        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: ct), t => t.Name == "document_write");
        var schema = tool.ReturnJsonSchema!.Value;
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("filePath").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("bytesWritten").GetProperty("type").GetString());
        Assert.True(properties.GetProperty("success").GetProperty("const").GetBoolean());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), p => p.GetString() == "filePath");
        var result = await client.CallToolAsync("document_write", new Dictionary<string, object?> { ["filePath"] = "observed.txt", ["content"] = "exact observed value" }, cancellationToken: ct);
        Assert.NotEqual(true, result.IsError);
        var written = result.StructuredContent!.Value;
        Assert.Equal(Path.Combine(_root, "observed.txt"), written.GetProperty("filePath").GetString());
        Assert.Equal("exact observed value", await File.ReadAllTextAsync(written.GetProperty("filePath").GetString()!, ct));
        foreach (var path in new[] { "../outside.txt", "forbidden.exe" })
        {
            var denied = await client.CallToolAsync("document_write", new Dictionary<string, object?> { ["filePath"] = path, ["content"] = "must not be written" }, cancellationToken: ct);
            Assert.True(denied.IsError);
            Assert.Null(denied.StructuredContent); // An error is not a successful output-schema instance.
            using var payload = JsonDocument.Parse(Assert.Single(denied.Content.OfType<TextContentBlock>()).Text);
            Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("filePath").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("errorCode").GetString()));
        }
        Assert.False(File.Exists(Path.Combine(_root, "forbidden.exe")));
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
