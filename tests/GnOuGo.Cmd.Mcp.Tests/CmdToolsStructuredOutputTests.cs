using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public sealed class CmdToolsStructuredOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-cmd-tools-structured-output-tests-" + Guid.NewGuid().ToString("N"));

    public CmdToolsStructuredOutputTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AllCmdMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(CmdTools)
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
        services.AddSingleton(new CommandPolicy(settings, _root));
        services.AddSingleton<CommandExecutionHost>();
        services.AddTransient<CmdTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Cmd.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<CmdTools>(CmdMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    [Fact]
    public async Task RunAsync_WhenPolicyInputFails_ReturnsStructuredFailure()
    {
        var settings = CreateSettings();
        var policy = new CommandPolicy(settings, _root);
        var host = new CommandExecutionHost(policy, NullLogger<CommandExecutionHost>.Instance);
        var tools = new CmdTools(host, NullLogger<CmdTools>.Instance);

        var result = await tools.RunAsync("echo_test", "{", cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Ok);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal(result.ErrorMessage, result.Message);
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    private CmdServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedShells = ["sh"],
        AllowedWorkingRoots = [_root],
        AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["echo_test"] = new()
            {
                Shell = "sh",
                Script = "printf '%s\\n' ok",
                WorkingDirectory = _root
            }
        }
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
