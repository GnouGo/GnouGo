using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserToolsStructuredOutputTests
{
    [Fact]
    public void AllBrowserMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(BrowserTools)
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
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IOptions<BrowserServerSettings>>(Options.Create(new BrowserServerSettings()));
        services.AddSingleton<PlaywrightBrowserHost>();
        services.AddTransient<BrowserTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Browser.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<BrowserTools>(BrowserMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    [Fact]
    public void BrowserToolFailure_ReturnsStructuredFailure()
    {
        var result = BrowserToolFailure.Action("click", "#missing", new InvalidOperationException("selector missing"));

        Assert.False(result.Success);
        Assert.False(result.Ok);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
        Assert.Contains("selector missing", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#missing", result.Selector);
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;
}
