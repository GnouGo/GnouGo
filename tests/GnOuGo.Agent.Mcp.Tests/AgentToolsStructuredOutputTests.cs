using System.Reflection;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class AgentToolsStructuredOutputTests
{
    [Fact]
    public void AllAgentMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(AgentTools)
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
    public void McpToolRegistration_CreatesAgentToolDescriptorsWithOutputSchemas()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IAgentRepository, NoopAgentRepository>();
        services.AddTransient<AgentTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.Agent.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<AgentTools>(AgentMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    private sealed class NoopAgentRepository : IAgentRepository
    {
        public Task<AgentDefinition> AddAgentAsync(
            string name,
            string workflow,
            string? originalPrompt = null,
            CancellationToken ct = default)
            => Task.FromResult(CreateAgent(name, workflow, originalPrompt));

        public Task<AgentDefinition> UpdateAgentAsync(
            Guid id,
            string name,
            string workflow,
            string? originalPrompt = null,
            CancellationToken ct = default)
        {
            var agent = CreateAgent(name, workflow, originalPrompt);
            agent.Id = id;
            return Task.FromResult(agent);
        }

        public Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<AgentDefinition>());

        public Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult<AgentDefinition?>(null);

        public Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
            => Task.CompletedTask;

        private static AgentDefinition CreateAgent(string name, string workflow, string? originalPrompt)
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                Workflow = workflow,
                OriginalPrompt = originalPrompt,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }
}
