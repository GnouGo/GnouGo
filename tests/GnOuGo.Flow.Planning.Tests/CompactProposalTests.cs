using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactProposalTests
{
    [Fact]
    public async Task CompactTypesRetainTheirSemanticMeaningAndRejectMalformedShapes()
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        var schema = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.False(schema["properties"]!.AsObject().ContainsKey("requirements"));
        Assert.False(schema["properties"]!.AsObject().ContainsKey("explanation"));
        var wire = JsonNode.Parse("""
            {"discoveryRequests":null,"plan":{"inputs":[],"groups":[],"choices":[],"root":{"tasks":[
              {"id":"interpret","kind":"transform","objective":"Interpret supplied text","dependsOn":[],
               "inputs":[{"name":"text","value":{"kind":"string","text":"one"}}],
               "resultType":{"kind":"object","fields":[{"name":"items","type":{"kind":"array","nullable":false,"items":{"kind":"string","nullable":true}}}]}}
            ],"always":[],"outputs":[{"name":"items","value":{"kind":"output","source":"interpret","port":"items"}}]}}}
            """)!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, schema));
        var proposal = wire.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        var field = Assert.Single(proposal.Plan!.Root.Tasks[0].ResultType!.Fields);
        Assert.True(field.Required); Assert.Null(field.Default); Assert.Empty(field.Type.Fields);
        Assert.True(field.Type.Items!.Nullable);
        var full = JsonSerializer.SerializeToNode(proposal.Plan, PlanningJsonContext.Default.TaskPlan)!;
        var old = full.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var compiler = new TaskPlanCompiler();
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var a = compiler.Compile(proposal.Plan, catalog); var b = compiler.Compile(old, catalog);
        Assert.Empty(a.Diagnostics); Assert.Empty(b.Diagnostics);
        Assert.Equal(new PlanningGraphCompiler().Compile(a.Graph!, catalog), new PlanningGraphCompiler().Compile(b.Graph!, catalog));
        foreach (var mutation in new Action<JsonNode>[] { t => t["items"] = null, t => t["fields"] = new JsonArray(), t => t.AsObject().Remove("nullable") })
        {
            var bad = wire.DeepClone(); mutation(bad["plan"]!["root"]!["tasks"]![0]!["resultType"]!["fields"]![0]!["type"]!);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(bad, schema));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedRequestSchemaOwnsRecoveryAndAcceptedRequirementsCannotBeReplaced(bool changed)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        var historicalSchema = PlanningSchemas.Proposal(state);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements = PlannerFixture.Requirements();
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        state.ModelCalls = 2; state.ReplanAttempts = 1;
        state.Usage = new() { Calls = 2, TotalTokens = 500, EstimatedCost = 0.1m, EstimatedCostCurrency = "EUR" };
        state.PendingCall = new() { Id = "retained", Purpose = "tasks", Request = new() { ClientRequestId = "retained", StructuredOutputSchema = historicalSchema } };
        runtime.Proposal.Requirements!.Summary = changed ? "Different intent" : state.Requirements.Summary;
        var recovered = PlannerFixture.Clone(state);
        var result = await new HybridWorkflowPlanner().AdvanceAsync(recovered, new() { ExpectedRevision = recovered.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal("retained", Assert.Single(runtime.Calls).ClientRequestId);
        Assert.True(JsonNode.DeepEquals(historicalSchema, runtime.Calls[0].StructuredOutputSchema));
        Assert.Equal(2, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
        Assert.Equal(500, result.Usage!.TotalTokens); Assert.Equal(0.1m, result.Usage.EstimatedCost);
        Assert.Equal(original, JsonSerializer.Serialize(result.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        if (changed) { Assert.Contains(result.Diagnostics, d => d.Code == "REQUIREMENTS_CHANGED"); Assert.Null(result.Plan); }
        else { Assert.Equal(PlanningStatus.FinalReview, result.Status); Assert.Empty(result.Diagnostics); }
    }

    [Fact]
    public async Task RepairOmitsRequirementsAndRetainsAcceptedIntent()
    {
        var runtime = new TestRuntime(); runtime.Proposal.Plan!.Root.Outputs.Add(new("broken", PlanningCorpus.Business("output", "absent", "value")));
        var planner = new HybridWorkflowPlanner(); var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        var accepted = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        runtime.Proposal.Plan.Root.Outputs[^1] = new("broken", PlanningCorpus.String("fixed"));
        runtime.Respond = (request, _) =>
        {
            Assert.False(request.StructuredOutputSchema!["properties"]!.AsObject().ContainsKey("requirements"));
            var response = TestRuntime.Response(request, runtime.Proposal);
            Assert.False(response.Json!.AsObject().ContainsKey("requirements"));
            return response;
        };
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
    }
}
