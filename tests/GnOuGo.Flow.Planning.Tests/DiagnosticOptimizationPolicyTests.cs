using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiagnosticOptimizationPolicyTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    public async Task CompleteNestedContributionsSurviveSchemaRepairCompilationAndRestart(int count)
    {
        var state = DiagnosticSession();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements = new() { Outcomes = [new("result", "Compute all requested values and cleanup.")] };
        state.Construction!.Requirements = [new() { Id = "result" }];
        state.Construction.Compact!.Blocks = [new() { Id = "block", Objective = "Compute all requested values and cleanup.", RequirementIds = ["result"],
            ExpectedResults = ["All requested values"], SuccessCriteria = ["Every requested value is computed and cleanup remains present"] }];
        state.Construction.Compact.CurrentBlockId = "block";
        state.Construction.Compact.CurrentStep = new() { BlockId = "block", Id = "anchor", Kind = "sequence", Scope = "/root",
            RequirementIds = ["result"], BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Compute all requested values." };
        var body = new TaskScope { Tasks = Enumerable.Range(1, count - 2).Select(Value).ToList(), Always = [Value(count - 1)] };
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["result"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [new() { Id = "sequence", Kind = "sequence", Objective = "Compute values with cleanup.", Body = body }] };
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var response = PlanningCorpus.Transport(new JsonObject { ["extension"] = JsonSerializer.SerializeToNode(extension,
            PlanningJsonContext.Default.PlanningConstructionExtension) }, schema, schema)!;
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(response, schema));
        state.Construction.Candidate = PlanningConstruction.Apply(state, response["extension"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionExtension)!);
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        PlanningConstruction.RefreshCandidate(state);
        state = PlannerFixture.Clone(state);
        var candidate = state.Construction!.Candidate!;
        Assert.Equal(count, candidate.TaskIds.Count);
        var compilation = TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request, candidate.OpenScopes.ToHashSet(StringComparer.Ordinal));
        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "recursive_tasks" && warning.Current == count && warning.Threshold == 8);
        Assert.Empty(candidate.Diagnostics);
        candidate.Plan.Root.Tasks[0].Body!.Tasks[0].Outputs[0] = new("number", new() { Kind = "output", Source = "fabricated", Port = "number" });
        Assert.NotEmpty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request, candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalAndProductionTaskBoundsRemainEnforced(bool tokenOnly)
    {
        var state = PlannerFixture.Session();
        if (tokenOnly) state.Request.Options[PlanningTokenThresholds.OptionName] = "advisory";
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var error = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state,
            new() { Scope = "/root", Tasks = Enumerable.Range(0, 10).Select(Value).ToList() }));
        Assert.Contains(error.Diagnostics, finding => finding.Code == "CONSTRUCTION_DRAFT_LIMIT");
        Assert.Null(state.ThresholdWarnings);
    }

    [Fact]
    public async Task CallsCorrectionsAndNonproductiveTargetsWarnDurablyWithoutGrantingRepairAuthority()
    {
        var state = DiagnosticSession(); state.Request.MaxModelCalls = 1; state.Request.MaxReplanAttempts = 0;
        state.Construction!.Limits.MaxCorrectionsPerDraft = 0;
        state.Construction.Requirements = [new() { Id = "result" }];
        PlanningProgress.Initialize(state);
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["value"] = "received" } } };
        for (var index = 0; index < 8; index++)
        {
            using (var correction = new PlanningCorrectionAttempt(state, correcting: true, draft: true))
                await PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.DraftPurpose, "Read the next fact " + index,
                    PlanningSchemas.Object(("value", PlanningSchemas.String())), PlannerFixture.Ct, correction: correction);
            PlanningProgress.Record(state);
            state = PlannerFixture.Clone(state);
        }
        Assert.Equal(8, runtime.Calls.Count); Assert.Equal(8, state.ModelCalls); Assert.Equal(8, state.ReplanAttempts);
        Assert.Equal(8, state.Construction!.DraftCorrections);
        Assert.Empty(state.Diagnostics);
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "model_calls");
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "session_corrections");
        var progress = state.ThresholdWarnings!.Last(warning => warning.Kind == "nonproductive_calls");
        Assert.Contains("result", progress.Details!["requirements"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.All(state.ThresholdWarnings!, warning => Assert.Equal(state.Request.SessionId, warning.SessionId));
        state.Request.Options["automatic_correction"] = "false";
        using var forbidden = new PlanningCorrectionAttempt(state, correcting: true);
        Assert.Throws<WorkflowRuntimeException>(forbidden.Reserve);
    }

    [Fact]
    public async Task GraphTargetsDoNotDisableIdentityOrBindingValidation()
    {
        var state = DiagnosticSession();
        var catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        catalog.Policy.MaxStepsTotal = 1; catalog.Policy.MaxStepsPerWorkflow = 1;
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps.Add(new() { Key = "second", Type = "set", Input = PlanningCorpus.Obj(("number", new() { Kind = "number", Number = 2 })) });
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), diagnostic => diagnostic.Code == "STEP_LIMIT");
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog, advisoryOptimization: true));
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "diagnostic", false, false, false, false, false, false,
            false, false, false, false, false, false, false, advisoryOptimization: true);
        Assert.Contains("second", graph.Workflows[0].Steps.Select(step => step.Key));
        Assert.NotEmpty(yaml);
        graph.Workflows[0].Steps[1].Key = graph.Workflows[0].Steps[0].Key;
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog, advisoryOptimization: true), diagnostic => diagnostic.Code == "NODE_IDENTITIES_INVALID");
    }

    [Fact]
    public async Task ReconsiderationRetainsEveryRelevantEvidenceAddressBeyondTheNavigationTarget()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var candidate = state.Construction!.Candidate!;
        for (var index = 0; index < 9; index++)
        {
            var task = Value(index);
            candidate.Plan.Root.Tasks.Add(task);
            candidate.TaskIds.Add(task.Id);
        }
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var taskIds = candidate.TaskIds.ToArray();
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["decision"] = new JsonObject
        {
            ["verdict"] = "needs_change", ["reason"] = "The values do not all double the original supplied number.",
            ["obligations"] = new JsonArray("work"),
            ["targets"] = new JsonArray(taskIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        } } };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        state = PlannerFixture.Clone(state);
        var addresses = HybridWorkflowPlanner.BusinessReviewReconsiderationEvidence(state);
        Assert.All(taskIds, id => Assert.Contains("task:" + id, addresses));
        Assert.True(addresses.Length > 8);
        Assert.True(HybridWorkflowPlanner.TryPrepareBusinessReviewReconsideration(state, addresses));
        Assert.Empty(state.Diagnostics);
        Assert.False(state.Construction!.Candidate!.BusinessReviewed);
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "context_requests" && warning.Current == addresses.Length);
    }

    private static PlanningSession DiagnosticSession() => PlannerFixture.Capture(new() { Request = new()
    {
        TenantId = "diagnostic-test", Prompt = "Compute all requested values and cleanup.", MaxModelCalls = 64,
        MaxReplanAttempts = 4, Generation = new("medium", 32000, 8192),
        Options = new() { [PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1" }
    } });
    private static PlanTask Value(int index) => new() { Id = "value_" + index, Kind = "value", Objective = "Preserve requested number " + index,
        Outputs = [new("number", new() { Kind = "number", Number = index })] };
}
