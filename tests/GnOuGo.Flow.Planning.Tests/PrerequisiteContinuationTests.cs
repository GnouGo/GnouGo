using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PrerequisiteContinuationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("alpha", "fetch", "consume")]
    [InlineData("unrelated", "inspect", "apply")]
    public async Task MissingObservationInsertsProducerAndReachesReviewWithoutDecision(string provider, string reader, string consumer)
    {
        var (factory, _) = Factory(provider, reader, consumer);
        var runtime = new TestRuntime(mcp: factory); var state = PlannerFixture.Session();
        state.Request.Prompt = "Observe an external token and use it to obtain the required value";
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var source = state.Catalog.Capabilities.Single(c => c.Method == reader).Id;
        var sink = state.Catalog.Capabilities.Single(c => c.Method == consumer).Id;
        state.SemanticPlan = new() { Actions = [new() { Id = "consume", Purpose = "Obtain the required value", Outputs = [new("value", "Required value")] }] };
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, [new("consume", "matched", [new(sink, "Consumes an observed token")], "Declared consumer")]));
        state.Grounding.Selections = [new("consume", [sink], "Declared consumer")];
        state.Diagnostics = [new("SEMANTIC_BINDING_BLOCKED", "/actions/consume", "Missing observed token")
            { Prerequisite = new("missing_observation", "An identifier does not establish the observed token", "value", sink, "/token") }];
        state.ModelCalls = 3;
        var replacement = new SemanticPlan { Actions = [new() { Id = "observe", Purpose = "Observe the current external token", Outputs = [new("observation", "Actual external observation")] },
            new() { Id = "consume", Purpose = "Obtain the required value using the observed token", After = ["observe"], Inputs = [new("observation", "observe.observation")], Outputs = [new("value", "Required value")] }] };
        var grounded = Plan(source, sink);
        runtime.Respond = request =>
        {
            if (request.Prompt.StartsWith("Replan this business", StringComparison.Ordinal))
            {
                Assert.Contains("/token", request.Prompt); Assert.Contains("selectedContracts", request.Prompt);
                var json = SemanticPlanning.Json(replacement);
                return new() { Json = new JsonObject { ["actions"] = json["actions"]!.DeepClone(), ["questions"] = new JsonArray() } };
            }
            if (request.StructuredOutputSchema?["properties"]?["decisions"] is not null)
            {
                var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
                return new() { Json = new JsonObject { ["decisions"] = new JsonArray(context["actions"]!.AsArray().Select(a => (JsonNode)new JsonObject
                { ["actionId"] = a!["id"]!.DeepClone(), ["outcome"] = "matched", ["matches"] = new JsonArray(new JsonObject
                    { ["capabilityId"] = a["id"]!.ToString() == "observe" ? source : sink, ["reason"] = "Declared operation" }), ["reason"] = "Complete coverage" }).ToArray()) } };
            }
            return new() { Json = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(grounded), request.StructuredOutputSchema!) };
        };
        var result = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(result.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(result.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Equal(6, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts); Assert.Equal(3, runtime.Calls.Count);
        Assert.Empty(result.Decisions); Assert.Null(result.PendingDecision); Assert.Null(result.ApprovedHash);
        Assert.NotEmpty(result.Scenarios); Assert.All(result.Scenarios, s => Assert.Equal("passed", s.Outcome));
        Assert.Empty(result.Catalog!.Capabilities.Single(c => c.Id == source).OutputSchema);
        Assert.Contains(result.GroundedPlan!.Operations, o => o is ValidateGroundedOperation);
        PlanningArtifactApproval.Verify(result);
    }

    [Theory]
    [InlineData("{\"token\":\"observed\"}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"token\":null}", false)]
    [InlineData("{\"token\":42}", false)]
    public async Task InvalidOpaqueObservationStopsBeforeTheConsumer(string observation, bool success)
    {
        var (factory, calls) = Factory("renamed", "read", "use", observation);
        var catalog = await new TestRuntime(mcp: factory).DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var plan = Plan(catalog.Capabilities.Single(c => c.Method == "read").Id, catalog.Capabilities.Single(c => c.Method == "use").Id);
        var graph = PlanningGraphBuilder.Build(GroundedPlanValidator.RequireValid(plan, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog, "prerequisite")));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.Equal(success, result.Success); Assert.Equal(success ? new[] { "read", "use" } : new[] { "read" }, calls);
        plan.Operations.RemoveAt(1);
        ((InvokeGroundedOperation)plan.Operations[1]).Arguments = [new("token", new() { Kind = "result", Source = "raw", Path = ["token"] })];
        Assert.Null(GroundedPlanValidator.Validate(plan, catalog).Plan);
    }

    private static GroundedPlan Plan(string source, string sink) => new() { Operations = [
        new InvokeGroundedOperation { Id = "raw", SemanticAction = "observe", Capability = source },
        new ValidateGroundedOperation { Id = "valid", SemanticAction = "observe", BusinessOutputs = [new("observation", [])], Value = new() { Kind = "result", Source = "raw" },
            ResultType = new() { Type = "object", Fields = [new("token", new() { Type = "string" })] } },
        new InvokeGroundedOperation { Id = "consume", SemanticAction = "consume", Capability = sink, BusinessOutputs = [new("value", ["value"])],
            Arguments = [new("token", new() { Kind = "result", Source = "valid", Path = ["token"] })] }], Outputs = [new("value", new() { Kind = "result", Source = "consume", Path = ["value"] })] };

    private static (InMemoryMcpClientFactory Factory, List<string> Calls) Factory(string provider, string reader, string consumer, string observation = "{\"token\":\"observed\"}")
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig(); var calls = new List<string>();
        server.Tools.Add(new() { Name = reader, Description = "Observe an external token", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","additionalProperties":false}"""), ExampleResponse = JsonNode.Parse("""{"token":"observed"}""") });
        server.Tools.Add(new() { Name = consumer, Description = "Consume an observed token", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{"token":{"type":"string"}},"required":["token"],"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}""") });
        server.ToolHandlers[reader] = _ => { calls.Add("read"); return new() { Content = JsonNode.Parse(observation) }; };
        server.ToolHandlers[consumer] = _ => { calls.Add("use"); return new() { Content = new JsonObject { ["value"] = 3 } }; };
        factory.RegisterServer(provider, server); return (factory, calls);
    }
}
