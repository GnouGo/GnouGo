using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticGroundingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static SemanticPlan Request() => new() { Summary = "Collect evidence and release the resource", Actions = [
        new() { Id = "collect", Purpose = "Read the observation", Outputs = [new("evidence", "Observed data")] },
        new() { Id = "release", Purpose = "Release the acquired resource", Outputs = [new("released", "Resource released")] }] };
    internal static PlanningSession State(int count = 2)
    {
        var state = PlannerFixture.Session(); state.SemanticPlan = Request();
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = Enumerable.Range(0, count).Select(i => new PlanningCapability
        { Id = "cap_" + i, Method = "renamed_" + i, StepType = "mcp.call", Description = "Declared operation " + i, EffectKind = i == 0 ? "read" : "lifecycle" }).ToList() };
        return state;
    }
    [Fact]
    public void SemanticResponseCannotSelectCapabilitiesOrExecutableFields()
    {
        var state = State(); var schema = SemanticPlanning.Schema(); var prompt = SemanticPlanning.Prompt(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.DoesNotContain("cap_0", prompt); Assert.DoesNotContain("renamed_0", prompt);
        foreach (var field in new[] { "capability", "arguments", "outputSchema", "retry", "expression", "stepType" })
        {
            var json = SemanticPlanning.Json(Request()); json["actions"]![0]![field] = "forbidden";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(json, schema));
        }
    }
    [Fact]
    public void CoverageIncludesEveryCapabilityAndUnabridgedDescription()
    {
        var state = State(14); state.Request.Generation.MaxInputTokensPerRequest = 3500;
        foreach (var capability in state.Catalog!.Capabilities) capability.Description = new string('x', 1400) + "\nDeclared release operation at the end.";
        var coverage = CapabilityGrounder.Create(state);
        Assert.InRange(coverage.Pages.Count, 2, 7);
        foreach (var action in state.SemanticPlan!.Actions)
            Assert.Equal(state.Catalog.Capabilities.Select(c => c.Id).Order(), coverage.Pages.Where(p => p.ActionIds.Contains(action.Id)).SelectMany(p => p.CapabilityIds).Order());
        foreach (var page in coverage.Pages)
        {
            var prompt = CapabilityGrounder.Prompt(state, page);
            Assert.Contains("Declared release operation at the end.", prompt);
            Assert.True(PlanningJsonTransport.EstimateInputTokens(prompt, CapabilityGrounder.Schema(page)) <= 3500);
        }
    }
    [Fact]
    public void CompleteMissStaysNoneOfTheAboveEvenWithCompatibleArguments()
    {
        var state = State(); state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "none_of_the_above", [], "No declared capability performs the required action.")).ToList()));
        Assert.All(CapabilityGrounder.Decisions(state), d => { Assert.Equal("none_of_the_above", d.Outcome); Assert.Empty(d.Matches); });
    }
    [Fact]
    public void PartialCoverageAndChangedCatalogCannotAuthorizeBinding()
    {
        var state = State(); state.Grounding = CapabilityGrounder.Create(state);
        Assert.Throws<PlanningConflictException>(() => CapabilityGrounder.Decisions(state));
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "none_of_the_above", [], "No match.")).ToList()));
        state.Catalog!.Capabilities[0].EffectKind = "write";
        Assert.Throws<PlanningConflictException>(() => CapabilityGrounder.Decisions(state));
    }
    [Theory]
    [InlineData("unissued")]
    [InlineData("duplicate")]
    [InlineData("forced")]
    public void InvalidGroundingClassificationsFailDeterministically(string defect)
    {
        var page = new GroundingPage("page", ["collect"], ["cap_0"]);
        var decision = new JsonObject { ["actionId"] = "collect", ["outcome"] = "matched", ["reason"] = "Declared reader.",
            ["matches"] = new JsonArray(new JsonObject { ["capabilityId"] = defect == "unissued" ? "tool_name" : "cap_0", ["reason"] = "Declared behavior." }) };
        if (defect == "forced") decision["outcome"] = "none_of_the_above";
        var json = new JsonObject { ["decisions"] = new JsonArray(decision) };
        if (defect == "duplicate") json["decisions"]!.AsArray().Add(decision.DeepClone());
        Assert.Throws<PlanningResponseException>(() => CapabilityGrounder.Read(page, json));
    }
    [Fact]
    public void AnOversizedCatalogStopsBeforeReservingOrCharging()
    {
        var state = State(1); state.Catalog!.Capabilities[0].Description = new string('z', 60000);
        Assert.Throws<WorkflowRuntimeException>(() => CapabilityGrounder.Create(state));
        Assert.Null(state.PendingCall); Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }
    [Fact]
    public async Task NativePlanUsesSeparateSemanticAndBindingCallsAndSurvivesRestart()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Equal(2, state.ModelCalls); Assert.NotNull(state.SemanticPlan); Assert.NotNull(state.GroundedPlan);
        Assert.Equal(PlanningPhase.Review, state.Phase); Assert.All(state.Scenarios, s => Assert.Equal("passed", s.Outcome));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var second = new TestRuntime(); var next = await new TypedWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, second, Ct);
        Assert.Empty(second.Calls); Assert.Equal(state.ComputeArtifactHash(), next.ComputeArtifactHash());
    }
    [Theory]
    [InlineData("opaque")]
    [InlineData("conditional")]
    [InlineData("expression")]
    [InlineData("cycle")]
    public async Task RecordedInvalidDataflowNeverReachesTheBuilder(string defect)
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var plan = PlannerFixture.Greeting();
        switch (defect)
        {
            case "opaque":
                catalog.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" } });
                plan.Operations.Insert(0, new InvokeGroundedOperation { Id = "source", Capability = "source" });
                ((CalculateGroundedOperation)plan.Operations[1]).Value = new() { Kind = "result", Source = "source", Path = ["invented"] }; break;
            case "conditional": plan.Operations[0].When = new() { Kind = "boolean", Boolean = false }; plan.Operations.Add(new CalculateGroundedOperation { Id = "consume", Value = new() { Kind = "result", Source = "greet" } }); break;
            case "expression": ((CalculateGroundedOperation)plan.Operations[0]).Value = new() { Kind = "compute", Text = "undeclaredDecision === true" }; break;
            case "cycle": plan.Operations[0].After = ["other"]; plan.Operations.Add(new CalculateGroundedOperation { Id = "other", After = ["greet"], Value = new() { Kind = "number", Number = 1 } }); break;
        }
        var result = GroundedPlanValidator.Validate(plan, catalog);
        Assert.Null(result.Plan); Assert.NotEmpty(result.Diagnostics);
    }
    [Fact]
    public async Task BusinessDefaultsAreLiteralAndMustSatisfyTheirContracts()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, Ct);
        foreach (var value in new GroundedValue[] { new(), new() { Kind = "input", Source = "a" }, new() { Kind = "string", Text = "wrong" } })
        {
            var plan = PlannerFixture.Greeting(); plan.Inputs.Add(new("a", new() { Type = "number" }, true, value));
            Assert.Null(GroundedPlanValidator.Validate(plan, catalog).Plan);
        }
        var valid = PlannerFixture.Greeting(); valid.Inputs.Add(new("a", new() { Type = "number", Nullable = true }, true, new()));
        Assert.NotNull(GroundedPlanValidator.Validate(valid, catalog).Plan);
    }
    [Fact]
    public void PromptContractsRetainEveryConstraintAndLiteralAnnotationNamedProperty()
    {
        var schema = JsonNode.Parse("""{"type":"object","description":"Documentation","properties":{"description":{"type":"string","description":"Label","enum":["a","b"],"default":"a"},"value":{"const":{"description":"Literal data","examples":[1]}}},"required":["description"],"additionalProperties":false}""")!.AsObject();
        var compact = PlanningJsonTransport.ContractPrompt(schema);
        Assert.Null(compact["description"]); Assert.NotNull(compact["properties"]!["description"]);
        Assert.True(JsonNode.DeepEquals(schema["properties"]!["value"]!["const"], compact["properties"]!["value"]!["const"]));
        foreach (var sample in new[] { "{}", "{\"description\":\"a\"}", "{\"description\":\"wrong\"}", "{\"description\":\"b\",\"extra\":1}" })
            Assert.Equal(PlanningContractValidation.ValidateInstance(JsonNode.Parse(sample), schema).Count, PlanningContractValidation.ValidateInstance(JsonNode.Parse(sample), compact).Count);
        var data = new JsonObject { ["text"] = "évaluer <not HTML>" };
        Assert.True(JsonNode.DeepEquals(data, JsonNode.Parse(PlanningJsonTransport.Prompt(data))));
    }
    [Fact]
    public async Task CompleteMatchesAreSelectedBeforeLargeBindingContractsAreLoaded()
    {
        var state = State(12); state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", page.CapabilityIds.Select(id => new GroundingMatch(id, "Declared viable implementation")).ToList(), "Viable implementations")).ToList()));
        var runtime = new TestRuntime { Respond = _ => new() { Json = new JsonObject { ["selections"] = new JsonArray(state.SemanticPlan!.Actions.Select(a => (JsonNode)new JsonObject {
            ["actionId"] = a.Id, ["capabilityIds"] = new JsonArray("cap_0"), ["reason"] = "Direct declared implementation" }).ToArray()) } } };
        await CapabilitySelection.ApplyAsync(state, runtime, Ct);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(12, CapabilityGrounder.Decisions(state)[0].Matches.Count);
        var prompt = CapabilityGrounder.BindingPrompt(state);
        Assert.Contains("cap_0", prompt); Assert.DoesNotContain("cap_11", prompt);
        Assert.Throws<PlanningResponseException>(() => CapabilitySelection.Validate(state, [new("collect", ["unissued"], "Guess")]));
    }
    [Fact]
    public void GroundingCanRealizeBusinessEvaluationAndLeafCleanupWithDeclaredCapabilities()
    {
        var state = State(); state.SemanticPlan!.Actions[0].Kind = "calculate"; state.SemanticPlan.Actions[1].Kind = "cleanup";
        var pages = CapabilityGrounder.Create(state).Pages;
        Assert.All(state.SemanticPlan.Actions, a => Assert.Contains(pages, p => p.ActionIds.Contains(a.Id)));
        Assert.False(SemanticPlanning.External(state.SemanticPlan.Actions[0])); Assert.True(SemanticPlanning.External(state.SemanticPlan.Actions[1]));
    }
    [Fact]
    public async Task AnUnchangedSemanticReplanStopsAndPreservesUnrelatedActions()
    {
        var state = State(); var original = SemanticPlanning.Hash(state.SemanticPlan!);
        state.Diagnostics = [new("NONE_OF_THE_ABOVE", "/actions/collect", "No semantic match")];
        var runtime = new TestRuntime { Respond = _ => new() { Json = new JsonObject { ["actions"] = new JsonArray(SemanticPlanning.Json(new() { Actions = [state.SemanticPlan!.Actions[0]] })["actions"]![0]!.DeepClone()), ["questions"] = new JsonArray() } } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(original, SemanticPlanning.Hash(state.SemanticPlan!));
        Assert.Equal(2, state.SemanticPlan!.Actions.Count); Assert.Equal(1, state.ReplanAttempts);
    }
}
