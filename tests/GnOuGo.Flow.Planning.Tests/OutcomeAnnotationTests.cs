using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OutcomeAnnotationTests
{
    [Theory]
    [InlineData("normal", false)]
    [InlineData("cleanup", true)]
    public async Task SelectedContractDeterminesEffectIndependentlyOfBusinessWordsAndPlacement(string placement, bool always)
    {
        var state = await ComposedOutcomeTests.State(); state.OutcomeVersion = 3;
        var operation = state.Catalog!.Capabilities.Single(c => c.Id == "external"); operation.EffectKind = "execute";
        var response = JsonNode.Parse("""{"requirements":{"summary":"Requested cleanup","inputs":[],"outcomes":[{"id":"work","description":"Remove disposable workspace","operation":"external","placement":"PLACE","conditional":false,"coverage":"once"}]}}""".Replace("PLACE", placement, StringComparison.Ordinal))!;
        var normalized = PlanningOutcomeAnnotations.Normalize(state, response).Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        Assert.Equal("execute", normalized.Requirements!.Outcomes.Single().Execution);
        Assert.Equal(always, normalized.Requirements.Outcomes.Single().Always);
        Assert.Equal("external", normalized.Requirements.Outcomes.Single().Operation);
        Assert.Null(response["requirements"]!["outcomes"]![0]!["execution"]);
        state.Requirements = normalized.Requirements;
        if (always) { state.Plan!.Root.Always = state.Plan.Root.Tasks; state.Plan.Root.Tasks = []; }
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Operation = "invented" };
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_CONTRACT_INVALID");
    }

    [Fact]
    public async Task InputsSupportDataButCannotEstablishExecutionOrInventCallerParameters()
    {
        var state = await ComposedOutcomeTests.State(); state.OutcomeVersion = 3;
        state.Plan!.Inputs = [new() { Name = "callerReference" }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Requirements.Outcomes = [new("work", "Accept the supplied reference") { Execution = "data", Always = false, Conditional = false, Coverage = "once" }];
        state.OutcomeBindings = [new("work", [], []) { Inputs = ["callerReference"] }];
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.OutcomeBindings[0] = state.OutcomeBindings[0] with { Inputs = ["technicalParameter"] };
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_INPUT_INVALID");
        state.OutcomeBindings[0] = state.OutcomeBindings[0] with { Inputs = ["callerReference"] };
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Execution = "write", Operation = "external" };
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_OPERATION_REQUIRED");
    }

    [Theory]
    [InlineData("parcel", "payload", true)]
    [InlineData("segment", "records", true)]
    [InlineData("parcel", "payload", false)]
    public async Task NestedExportsIssueOnlyMissingPortsAndOneConsumerCorrection(string container, string port, bool existing)
    {
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var state = PlannerFixture.Session(); state.IntentVersion = 1; state.OutcomeVersion = 3;
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements = new() { Summary = "Return declared values", Inputs = [], Outcomes = [new("data", "Return the selected data") { Execution = "data", Always = false, Conditional = false, Coverage = "once" }] };
        var producer = new PlanTask { Id = "origin", Kind = "value", Objective = "Preserve data", Outputs = [new(port, PlanningCorpus.Business("input", "entries"))] };
        var inner = new PlanTask { Id = "inner", Kind = "sequence", Objective = "Nested work", Body = new() { Tasks = [producer] } };
        var outer = new PlanTask { Id = container, Kind = "conditional", Objective = "Choose branch", Condition = new() { Kind = "boolean", Boolean = true },
            Body = new() { Tasks = [inner] }, Otherwise = new() };
        if (existing)
        {
            inner.Body.Outputs = [new(port, PlanningCorpus.Business("output", "origin", port))];
            outer.Body.Outputs = [new(port, PlanningCorpus.Business("output", "inner", port))];
            outer.Otherwise.Outputs = [new(port, new() { Kind = "array", Items = [PlanningCorpus.String("fallback")] })];
        }
        state.Plan = new() { Inputs = [new() { Name = "entries", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }], Root = new() { Tasks = [outer], Outputs = [new("report", PlanningCorpus.Business("output", "origin", port))] } };
        state.Requirements.Inputs = state.Plan.Inputs;
        state.OutcomeBindings = [new("data", [], ["report"])];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics, minimalExports: true).ToList();
        var request = new PlanningPrompt(state).Request();
        Assert.Equal(6, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
        foreach (var code in new[] { "LLM_BUDGET_UNVERIFIABLE", "MODEL_DISPATCH_UNVERIFIABLE", "PLANNING_HOST_FAILURE" })
        {
            var interrupted = PlannerFixture.Clone(state); interrupted.Diagnostics.Add(new(code, "/", "Transport interruption"));
            PlanningRepairPatch.Verify(interrupted, request);
        }
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        var slots = RepairPatchTests.Slots(state);
        Assert.Equal(existing ? 1 : 4, slots.Count);
        Assert.DoesNotContain(slots, s => s.Kind == "exports");
        var revised = PlannerFixture.Clone(state).Plan!;
        revised.Root.Outputs[0].Value.Source = container;
        if (!existing)
        {
            revised.Root.Tasks[0].Body!.Tasks[0].Body!.Outputs.Add(new(port, PlanningCorpus.Business("output", "origin", port)));
            revised.Root.Tasks[0].Body!.Outputs.Add(new(port, PlanningCorpus.Business("output", "inner", port)));
            revised.Root.Tasks[0].Otherwise!.Outputs.Add(new(port, new() { Kind = "array", Items = [PlanningCorpus.String("fallback")] }));
        }
        var response = TestRuntime.PatchResponse(request, state, revised);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        var candidate = PlanningRepairPatch.Apply(state, response.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!.Patch!, request);
        Assert.Empty(new TaskPlanCompiler().Compile(candidate, state.Catalog).Diagnostics);
        Assert.Equal(existing ? 1 : 0, state.Plan.Root.Tasks[0].Body!.Outputs.Count);
        var wrong = response.DeepClone(); wrong["patch"]!["edits"]![0]!["value"] = JsonSerializer.SerializeToNode(PlanningCorpus.Business("output", "origin", port), PlanningJsonContext.Default.TaskValue);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(wrong, request.StructuredOutputSchema!));
    }
}
