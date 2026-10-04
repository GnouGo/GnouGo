using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FiniteInputDomainTests
{
    private static PlanningSession State(bool resolved, string contract = """{"type":"string","enum":["alpha","beta"]}""")
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        var capability = new PlanningCapability { Id = "op", Version = "v1", StepType = "mcp.call", Kind = "tool", Server = "source", Method = "arbitrary",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["selector"] = JsonNode.Parse(contract), ["text"] = new JsonObject { ["type"] = "string" } } },
            OutputSchema = new() { ["type"] = "object" } };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] }; state.Discovery.Sources = [new("source", "Declared metadata")];
        state.Discovery.Pages = [new("source", null, [new("op", "source", "arbitrary", "No inferred semantics", "mcp.call", "none", "v1", Operation: TaskOperations.Describe(capability))], null)];
        if (resolved) state.Discovery.Resolved.Add(capability);
        return state;
    }

    private static JsonObject Proposal(JsonNode value, string operation = "op") => new JsonObject
    {
        ["discoveryRequests"] = null, ["clarifications"] = null,
        ["plan"] = JsonNode.Parse("""{"inputs":[],"root":{"tasks":[],"outputs":[],"always":[]},"groups":[],"choices":[]}""")
    }.WithTask(value, operation);

    [Fact]
    public void ResolvedFiniteLiteralsHaveNoUnrestrictedOperationOrValueAlternative()
    {
        var schema = PlanningSchemas.Proposal(State(true));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        foreach (var text in new[] { "alpha", "beta" })
            Assert.Empty(PlanningContractValidation.ValidateInstance(Proposal(new JsonObject { ["kind"] = "string", ["text"] = text }), schema));
        foreach (var value in new[] { """{"kind":"string","text":"other"}""", """{"kind":"number","number":1}""", """{"kind":"null"}""", """{"kind":"object","members":[]}""" })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Proposal(JsonNode.Parse(value)!), schema));
        foreach (var value in new[] { """{"kind":"input","source":"choice"}""", """{"kind":"output","source":"producer","port":"decision"}""", """{"kind":"field","items":[{"kind":"item"}],"port":"decision"}""" })
            Assert.Empty(PlanningContractValidation.ValidateInstance(Proposal(JsonNode.Parse(value)!), schema));
    }

    [Theory]
    [InlineData("{\"const\":\"exact\"}", "{\"kind\":\"string\",\"text\":\"exact\"}", "{\"kind\":\"string\",\"text\":\"other\"}")]
    [InlineData("{\"enum\":[1,2]}", "{\"kind\":\"number\",\"number\":2}", "{\"kind\":\"number\",\"number\":3}")]
    [InlineData("{\"const\":true}", "{\"kind\":\"boolean\",\"boolean\":true}", "{\"kind\":\"boolean\",\"boolean\":false}")]
    [InlineData("{\"const\":null}", "{\"kind\":\"null\"}", "{\"kind\":\"string\",\"text\":\"null\"}")]
    [InlineData("{\"type\":[\"string\",\"null\"],\"enum\":[\"x\",null]}", "{\"kind\":\"null\"}", "{\"kind\":\"string\",\"text\":\"wrong\"}")]
    public void ScalarDomainsPreserveJsonTypesAndExplicitNull(string contract, string valid, string invalid)
    {
        var schema = PlanningSchemas.Proposal(State(true, contract));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Proposal(JsonNode.Parse(valid)!), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Proposal(JsonNode.Parse(invalid)!), schema));
    }

    [Fact]
    public void EqualPortNamesDoNotMergeDifferentOperationDomains()
    {
        var state = State(true); var other = State(true, """{"enum":["different"]}""").Discovery.Resolved[0];
        other.Id = "other"; state.Discovery.Resolved.Add(other);
        var schema = PlanningSchemas.Proposal(state);
        var value = JsonNode.Parse("""{"kind":"string","text":"different"}""")!;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Proposal(value.DeepClone()), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Proposal(value, "other"), schema));
    }

    [Fact]
    public void IndexShowsDeclaredDomainsWithoutFullSchemasOrReceiptMutation()
    {
        var state = State(false); var receipt = state.Discovery.Pages[0].Capabilities[0].Operation!;
        receipt.Inputs[0].Schema["description"] = "full-contract-only";
        var before = receipt.Inputs[0].Schema.ToJsonString();
        var prompt = HybridWorkflowPlanner.BuildPrompt(state, []);
        Assert.Contains("\"enum\":[\"alpha\",\"beta\"]", prompt);
        Assert.DoesNotContain("full-contract-only", prompt);
        Assert.Equal(before, receipt.Inputs[0].Schema.ToJsonString());
    }

    [Fact]
    public void KnownOwnedDomainsNeverBecomeEditableOrDisclosed()
    {
        var state = State(true); state.Discovery.Resolved[0].RequestBindings.Add(new("/selector", JsonValue.Create("alpha")));
        var schema = PlanningSchemas.Proposal(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Proposal(JsonNode.Parse("""{"kind":"string","text":"alpha"}""")!), schema));
        Assert.DoesNotContain("alpha", HybridWorkflowPlanner.BuildPrompt(state, []));
    }

    [Fact]
    public void ExactResolutionConstrainsPreviouslyIndexOnlyOperationsWithoutMutatingReceipts()
    {
        var state = State(false); var invalid = Proposal(JsonNode.Parse("""{"kind":"string","text":"invented"}""")!);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, PlanningSchemas.Proposal(state)));
        state.Discovery.Resolved.Add(State(true).Discovery.Resolved[0]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, PlanningSchemas.Proposal(state)));
        state.Catalog!.Capabilities.Add(state.Discovery.Resolved[0]);
        var result = new TaskPlanCompiler().Compile(invalid["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!, state.Catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_TYPE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulBatchOnlyClearsResolvedDiscoveryResponseErrors(bool recover)
    {
        var state = PlannerFixture.Session(); state.Catalog = new(); state.Requirements = PlannerFixture.Requirements();
        state.Discovery.Sources = [new("source", "Declared source")];
        state.Diagnostics = [new("DISCOVERY_NO_PROGRESS", "/discoveryRequests/0", "Cached page"),
            new("PLANNING_RESPONSE_INVALID", "/discoveryRequests/0/query", "Malformed query"),
            new("TASK_INPUT_TYPE", "/tasks/keep/inputs/value", "Keep semantic error"),
            new("SOURCE_UNAVAILABLE", "/sources/other", "Keep availability finding")];
        state.Discovery.Limitations = ["other: unavailable"];
        var schema = PlanningSchemas.Proposal(state);
        if (recover)
        {
            state.ModelCalls = 1; state.ReplanAttempts = 1;
            state.PendingCall = new() { Id = "recorded", Purpose = "replan", Request = new() { ClientRequestId = "recorded", StructuredOutputSchema = schema } };
        }
        var runtime = new TestRuntime { Capabilities = new DiscoveryCallBudgetTests.Pages(),
            Respond = (request, _) => TestRuntime.Response(request, new() { DiscoveryRequests = [new("source", Query: "a new page")] }) };
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new(), runtime, PlannerFixture.Ct);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(new[] { "TASK_INPUT_TYPE", "SOURCE_UNAVAILABLE" }, state.Diagnostics.Select(d => d.Code));
        Assert.Contains("other: unavailable", state.Discovery.Limitations);
        if (recover) { Assert.Equal("recorded", Assert.Single(runtime.Calls).ClientRequestId); Assert.True(JsonNode.DeepEquals(schema, runtime.Calls[0].StructuredOutputSchema)); }
    }

    [Fact]
    public async Task SuccessfulDiscoveryCorrectionDoesNotConsumeTheProposalRepair()
    {
        var runtime = new TestRuntime { Capabilities = new DiscoveryCallBudgetTests.Pages() };
        runtime.Respond = (request, _) => runtime.Calls.Count switch
        {
            <= 4 => TestRuntime.Response(request, DiscoveryCallBudgetTests.Discovery(runtime.Calls.Count)),
            5 => TestRuntime.Response(request, DiscoveryCallBudgetTests.Discovery(4)),
            6 => TestRuntime.Response(request, DiscoveryCallBudgetTests.Discovery(6)),
            7 => new() { Json = new JsonObject { ["malformed"] = true } },
            _ => TestRuntime.Response(request, runtime.Proposal)
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(8, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Contains(runtime.Checkpoints, s => s.ModelCalls == 5 && s.Diagnostics.Any(d => d.Code == "DISCOVERY_NO_PROGRESS"));
        Assert.Contains(runtime.Checkpoints, s => s.ModelCalls == 6 && s.PendingCall is null && s.Diagnostics.Count == 0);
        Assert.Contains(runtime.Checkpoints, s => s.ModelCalls == 7 && s.ReplanAttempts == 1);
    }
}

internal static class FiniteInputProposal
{
    internal static JsonObject WithTask(this JsonObject proposal, JsonNode value, string operation)
    {
        proposal["plan"]!["root"]!["tasks"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "consume", ["kind"] = "operation", ["objective"] = "Use a declared value", ["dependsOn"] = new JsonArray(),
            ["operation"] = operation, ["inputs"] = new JsonArray(new JsonObject { ["name"] = "selector", ["value"] = value })
        });
        return proposal;
    }
}
