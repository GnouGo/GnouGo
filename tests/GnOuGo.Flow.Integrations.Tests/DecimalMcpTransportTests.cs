using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using ProtocolServerOptions = ModelContextProtocol.Server.McpServerOptions;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class DecimalMcpTransportTests
{
    [Fact]
    public async Task DeclaredDecimalRoundTripsAsANumberThroughRealMcpAndFlow()
    {
        decimal? observed = null; var calls = 0;
        var tool = McpServerTool.Create((decimal amount) => { calls++; observed = amount; return new JsonObject { ["result"] = amount + 0.1m }; },
            new McpServerToolCreateOptions { Name = "measure", UseStructuredContent = true });
        tool.ProtocolTool.InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"amount":{"type":"number","format":"decimal"}},"required":["amount"]}""").RootElement.Clone();
        tool.ProtocolTool.OutputSchema = JsonDocument.Parse("""{"type":"object","properties":{"result":{"type":"number","format":"decimal"}},"required":["result"]}""").RootElement.Clone();
        var incoming = new Pipe(); var outgoing = new Pipe();
        await using var transport = new StreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream(), "wire");
        await using var server = McpServer.Create(transport, new ProtocolServerOptions
        { ServerInfo = new() { Name = "wire", Version = "1" }, ToolCollection = [tool] });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serving = server.RunAsync(cancellation.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
            ConfiguredMcpClientFactory.CreateClientOptions(), cancellationToken: TestContext.Current.CancellationToken);
        var adapter = new McpSessionAdapter("wire", client);
        var contract = Assert.Single(await adapter.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("decimal", contract.InputSchema!["properties"]!["amount"]!["format"]!.ToString());
        var engine = new WorkflowEngine { McpClientFactory = new Factory(adapter) };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: request
                    type: mcp.call
                    input: {server: wire, method: measure, request: {amount: "${data.inputs.amount}"}}
                outputs:
                  result: "${data.steps.request.response.result}"
            """));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["amount"] = 1.25 }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message + result.Error?.Details?.ToJsonString());
        Assert.Equal(1.25m, observed); Assert.Equal(1.35, result.Outputs!["result"]!.GetValue<double>()); Assert.Equal(1, calls);
        var invalid = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["amount"] = 1e100 }, TestContext.Current.CancellationToken);
        Assert.False(invalid.Success); Assert.Equal("INPUT_VALIDATION", invalid.Error!.Code); Assert.Equal(1, calls);
        cancellation.Cancel(); await serving;
    }

    [Theory]
    [InlineData("0.123456789012345678901234567891", "0.1234567890123456789012345678", null)]
    [InlineData("1e100", "1", "input")]
    [InlineData("1e-100", "1", "input")]
    [InlineData("\"1\"", "1", "input")]
    [InlineData("1", "1e100", "output")]
    [InlineData("1", "1e-100", "output")]
    [InlineData("1", "\"1\"", "output")]
    public async Task NestedWireConversionsValidateRoundingAndCommitFailures(string input, string output, string? failureDirection)
    {
        var calls = 0;
        const string schema = """{"type":"object","properties":{"values":{"type":"array","items":{"$ref":"#/$defs/decimal"}}},"required":["values"],"$defs":{"decimal":{"type":"number","format":"decimal"}}}""";
        var incoming = new Pipe(); var outgoing = new Pipe();
        await using var transport = new StreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream(), "wire");
        var options = new ProtocolServerOptions { ServerInfo = new() { Name = "wire", Version = "1" } };
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
        { Tools = [new() { Name = "exchange", InputSchema = JsonDocument.Parse(schema).RootElement.Clone(), OutputSchema = JsonDocument.Parse(schema).RootElement.Clone() }] });
        options.Handlers.CallToolHandler = (request, _) =>
        {
            calls++;
            var received = request.Params!.Arguments!["values"][0];
            Assert.Equal(JsonValueKind.Number, received.ValueKind);
            Assert.Equal(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture), received.GetDecimal());
            var response = "{\"values\":[" + output + "]}";
            return ValueTask.FromResult(new CallToolResult { StructuredContent = JsonDocument.Parse(response).RootElement.Clone(), Content = [new TextContentBlock { Text = response }] });
        };
        await using var server = McpServer.Create(transport, options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serving = server.RunAsync(cancellation.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                ConfiguredMcpClientFactory.CreateClientOptions(), cancellationToken: TestContext.Current.CancellationToken);
            var store = new InMemoryWorkflowRunStore();
            var engine = new WorkflowEngine { McpClientFactory = new Factory(new McpSessionAdapter("wire", client)), RunStore = store,
                Limits = new() { TenantId = "wire-tenant", RunId = "numeric-boundary" } };
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: exchange
                        type: mcp.call
                        input: {server: wire, method: exchange, request: "${data.inputs.request}"}
                    outputs:
                      result: "${data.steps.exchange.response}"
                """));
            var result = await engine.ExecuteAsync(document.Workflows["main"],
                new JsonObject { ["request"] = JsonNode.Parse("{\"values\":[" + input + "]}") }, TestContext.Current.CancellationToken);
            Assert.Equal(failureDirection is null, result.Success);
            Assert.Equal(failureDirection == "input" ? 0 : 1, calls);
            var saved = (await store.ReadAsync("wire-tenant", "numeric-boundary", TestContext.Current.CancellationToken))!;
            var receipt = Assert.Single(saved.Invocations.Values, i => i.StepType == "mcp.call");
            Assert.True(receipt.ExternalCompletionObserved);
            if (failureDirection is not null)
            {
                Assert.Equal(failureDirection == "input" ? "INPUT_VALIDATION" : "MCP_CALL_ERROR", result.Error!.Code);
                Assert.False(result.Error.Retryable);
                Assert.Contains("/values/0", result.Error.Details!.ToJsonString());
                var resumed = await engine.ResumeAsync("wire-tenant", "numeric-boundary", saved.Revision, document.Workflows["main"], TestContext.Current.CancellationToken);
                Assert.Equal(result.Error.Code, resumed.Error!.Code);
                Assert.Equal(failureDirection == "input" ? 0 : 1, calls);
            }
            else
            {
                Assert.Equal((double)0.1234567890123456789012345678m, result.Outputs!["result"]!["values"]![0]!.GetValue<double>());
                Assert.Equal(output, receipt.Observation!["raw_response"]!["values"]![0]!.ToJsonString());
            }
        }
        finally { cancellation.Cancel(); await serving; }
    }

    private sealed class Factory(IMcpSession session) : IMcpClientFactory
    {
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "wire" }];
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult(session);
    }
}
