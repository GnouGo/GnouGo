using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class McpResponsePreservationTests
{
    [Theory]
    [InlineData("progressEvents")]
    [InlineData("progress_events")]
    [InlineData("progress")]
    [InlineData("events")]
    [InlineData("Events")]
    public async Task TelemetryDoesNotDeleteRequiredBusinessFieldsOrMutateTheProducer(string field)
    {
        var content = new JsonObject { ["success"] = true, [field] = new JsonArray(new JsonObject { ["message"] = "Observed event", ["recordId"] = 17 }), ["other"] = new JsonArray(4, 9) };
        var before = content.DeepClone();
        var result = await Run(content, field);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(before, result.Outputs!["payload"]));
        Assert.True(JsonNode.DeepEquals(before, content));
        result.Outputs["payload"]![field]!.AsArray().Add(new JsonObject { ["recordId"] = 99 });
        Assert.True(JsonNode.DeepEquals(before, content));
    }

    [Fact]
    public async Task MissingRequiredFieldsRemainErrorsAndAreNeverSynthesized()
    {
        var result = await Run(new JsonObject { ["success"] = true, ["other"] = new JsonArray(4, 9) }, "progressEvents");
        Assert.False(result.Success); Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("progressEvents", result.Error.Message);
    }

    [Fact]
    public async Task ErrorEnvelopesKeepTheirOriginalPayloadAndClassification()
    {
        var content = new JsonObject { ["success"] = false, ["error_code"] = "DECLINED", ["error_message"] = "Scripted refusal", ["events"] = new JsonArray("retained") };
        var before = content.DeepClone(); var result = await Run(content, "events");
        Assert.False(result.Success); Assert.Equal(ErrorCodes.McpCallError, result.Error!.Code);
        Assert.Contains("DECLINED", result.Error.Details!.ToJsonString());
        Assert.Contains("retained", result.Error.Details.ToJsonString());
        Assert.True(JsonNode.DeepEquals(before, content));
    }

    private static async Task<RunResult> Run(JsonObject content, string field)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("business-data", new() { Tools = [new() { Name = "read", InputSchema = JsonNode.Parse("""{"type":"object"}""")!.AsObject() }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = content } } });
        var document = WorkflowParser.Parse($$"""
            version: 1
            workflows:
              main:
                steps:
                  - id: read
                    type: mcp.call
                    input: { server: business-data, method: read, request: {} }
                  - id: consume
                    type: set
                    input: "${data.steps.read.response}"
                    output_schema:
                      type: object
                      required: [success, {{field}}, other]
                      properties:
                        success: { type: boolean }
                        {{field}}: { type: array, items: { type: object } }
                        other: { type: array, items: { type: integer } }
                outputs:
                  payload: "${data.steps.consume}"
            """);
        var compiled = new WorkflowCompiler().Compile(document);
        return await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), TestContext.Current.CancellationToken);
    }
}
