using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningOutcomeTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => PlannerFixture.Ct;
    internal static PlanningRequirement Outcome(string id, string execution = "write", bool always = false, bool conditional = false) =>
        new(id, "Perform the requested " + id) { Execution = execution, Always = always, Conditional = conditional };

    private static async Task<PlanningSession> State()
    {
        var state = PlannerFixture.Session(); state.IntentVersion = 1; state.OutcomeVersion = 1;
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, Ct);
        state.Catalog.Capabilities.Add(new() { Id = "issued", Version = "v1", Kind = "tool", Server = "arbitrary", Method = "arbitrary",
            StepType = "mcp.call", EffectKind = "write", InputSchema = Empty(), OutputSchema = Empty() });
        state.Requirements = new() { Summary = "Perform requested work", Inputs = [], Outcomes = [Outcome("work")] };
        state.Plan = new() { Root = new() { Tasks = [Operation("task")], Outputs = [new("result", PlanningCorpus.String("done"))] } };
        state.OutcomeBindings = [new("work", ["task"], [])];
        return state;
    }
    private static PlanTask Operation(string id) => new() { Id = id, Objective = "Perform requested work", Kind = "operation", Operation = "issued" };
    private static JsonObject Empty() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };

    [Fact]
    public async Task ComparableContractOverheadKeepsTheOneCallExecutionPath()
    {
        var samples = new List<object>();
        // Interleave compatibility and current contracts; the first pair warms both paths.
        // These are deterministic host timings and token estimates, never provider usage.
        for (var repetition = -1; repetition < 3; repetition++)
            foreach (var legacy in new[] { true, false })
            {
                var runtime = new TestRuntime(); var state = PlannerFixture.Session();
                if (legacy) { state.IntentVersion = 1; state.OutcomeVersion = 1; }
                var clock = System.Diagnostics.Stopwatch.StartNew(); state = await PlannerFixture.RunAsync(runtime, state);
                var planningMs = clock.Elapsed.TotalMilliseconds; PlanningArtifactApproval.Verify(state);
                var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
                clock.Restart(); var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], null, Ct);
                var executionMs = clock.Elapsed.TotalMilliseconds;
                Assert.True(result.Success); Assert.Equal("Hello", result.Outputs!["message"]!.ToString());
                Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
                if (repetition >= 0) samples.Add(new { contract = legacy ? "outcome-v1" : "outcome-v2", repetition,
                    planningCalls = state.ModelCalls, repairs = state.ReplanAttempts, discoveryReads = state.Discovery.Pages.Count,
                    inputTokenEstimate = PlanningJsonTransport.EstimateInputTokens(runtime.Calls[0].Prompt, runtime.Calls[0].StructuredOutputSchema!.AsObject()),
                    planningMs, executionMs, totalMs = planningMs + executionMs, executionSuccess = result.Success, oracleCorrect = true });
            }
        output.WriteLine("OUTCOME_METRICS " + JsonSerializer.Serialize(samples));
    }

    [Fact]
    public async Task DiscoveryBeforeRequirementsDoesNotConsumeRepairsAndCachedRecoveryPreservesIt()
    {
        var catalog = new Sources(); var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Calls.Count == 1
            ? new() { DiscoveryRequests = [new("one"), new("two")] } : runtime.Proposal);
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        Assert.Null(state.Requirements); Assert.Empty(state.Diagnostics); Assert.Equal(2, catalog.Reads);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, catalog.Reads); PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task ClearDataPlanRetainsOneCallAndExplicitReviewSupport()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(2, state.OutcomeVersion); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("data", Assert.Single(state.Requirements!.Outcomes).Execution);
        Assert.Equal("message", Assert.Single(Assert.Single(state.OutcomeBindings!).Outputs));
        Assert.Contains(state.ValidationResults, r => r.Id == "outcome:message" && r.Description.Contains("not been observed", StringComparison.Ordinal));
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData("value", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("transform", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("read", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("unknown", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("denied", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("unresolved", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("unused", "OUTCOME_TASK_UNREACHABLE")]
    [InlineData("duplicate", "OUTCOME_BINDINGS_INVALID")]
    [InlineData("missing", "OUTCOME_BINDINGS_INVALID")]
    [InlineData("extra", "OUTCOME_BINDINGS_INVALID")]
    [InlineData("output", "OUTCOME_OPERATION_REQUIRED")]
    [InlineData("placement", "OUTCOME_PLACEMENT_INVALID")]
    public async Task InvalidSupportCannotBecomeAnApprovableImplementation(string defect, string code)
    {
        var state = await State(); var task = state.Plan!.Root.Tasks[0];
        switch (defect)
        {
            case "value": case "transform": task.Kind = defect; break;
            case "read": case "unknown": state.Catalog!.Capabilities.Single(c => c.Id == "issued").EffectKind = defect; break;
            case "denied": state.Catalog!.Policy.DeniedCapabilityIds.Add("issued"); break;
            case "unresolved": task.Operation = "absent"; break;
            case "unused": state.Plan.Groups.Add(new() { Id = "unused", Body = state.Plan.Root }); state.Plan.Root = new(); break;
            case "duplicate": state.OutcomeBindings!.Add(state.OutcomeBindings[0]); break;
            case "missing": state.OutcomeBindings = null; break;
            case "extra": state.OutcomeBindings!.Add(new("unrequested", ["task"], [])); break;
            case "output": state.OutcomeBindings = [new("work", [], ["result"])]; break;
            case "placement": state.Requirements!.Outcomes[0] = Outcome("work", always: true); break;
        }
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == code);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalCounterpartsAndNestedGroupsKeepTheirCoverage(bool both)
    {
        var state = await State(); state.Plan!.Inputs = [new() { Name = "enabled", Type = new() { Kind = "boolean" } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Plan.Groups = [new() { Id = "group", Body = new() { Tasks = [Operation("first")] } }];
        state.Plan.Root.Tasks = [new() { Id = "branch", Kind = "conditional", Objective = "Choose implementation", Condition = PlanningCorpus.Business("input", "enabled"),
            Body = new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "group", Objective = "Execute group" }] },
            Otherwise = new() { Tasks = both ? [Operation("second")] : [] } }];
        state.OutcomeBindings = [new("work", both ? ["first", "second"] : ["first"], [])];
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics);
        Assert.Equal(both, PlanningOutcomeValidation.Findings(state).Count == 0);
        state.Requirements.Outcomes[0] = Outcome("work", conditional: true);
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.Plan.Root.Tasks[0].Condition = new() { Kind = "boolean", Boolean = false };
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_TASK_UNREACHABLE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PossiblyEmptyLoopsCannotSilentlyWeakenUnconditionalWork(bool parallel)
    {
        var state = await State(); var body = state.Plan!.Root;
        state.Plan.Inputs = [new() { Name = "items", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }];
        state.Plan.Root = new() { Tasks = [new() { Id = "loop", Kind = "foreach", Objective = "Process entries", Parallel = parallel, Items = PlanningCorpus.Business("input", "items"), Body = body }] };
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_PATH_INCOMPLETE");
        state.Requirements!.Outcomes[0] = Outcome("work", conditional: true);
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.Plan.Root.Tasks[0].Items = new() { Kind = "array", Items = [] };
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_TASK_UNREACHABLE");
    }

    [Fact]
    public async Task TheSameGroupCanSupplyNormalAndCleanupInvocations()
    {
        var state = await State(); var task = state.Plan!.Root.Tasks.Single();
        state.Plan.Groups = [new() { Id = "shared", Body = new() { Tasks = [task] } }];
        state.Plan.Root.Tasks = [new() { Id = "normal", Kind = "call", Group = "shared", Objective = "Initialize" }];
        state.Plan.Root.Always = [new() { Id = "final", Kind = "call", Group = "shared", Objective = "Clean up" }];
        state.Requirements!.Outcomes.Add(Outcome("cleanup", always: true));
        state.OutcomeBindings!.Add(new("cleanup", ["task"], []));
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
        state.Plan.Root.Always.Clear();
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_PLACEMENT_INVALID");
    }

    [Fact]
    public async Task ReusedGroupPathsAreCheckedWithoutExpandingTheInvocationTree()
    {
        var state = await State(); state.Plan!.Root.Tasks.Clear();
        for (var depth = 0; depth < 18; depth++)
        {
            var body = new TaskScope();
            if (depth == 0) body.Tasks.Add(new() { Id = "constant", Kind = "value", Objective = "Return supplied data" });
            else for (var call = 0; call < 4; call++) body.Tasks.Add(new() { Id = "call_" + depth + "_" + call, Kind = "call", Group = "group_" + (depth - 1), Objective = "Reuse work" });
            state.Plan.Groups.Add(new() { Id = "group_" + depth, Body = body });
        }
        state.Plan.Root.Tasks.Add(new() { Id = "entry", Kind = "call", Group = "group_17", Objective = "Reuse groups" });
        state.OutcomeBindings = [new("work", ["constant"], [])];
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_OPERATION_REQUIRED");
    }

    [Fact]
    public async Task AcceptedEffectsAndCoverageParticipateInApprovalAndRepairAuthority()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var hash = state.ComputeArtifactHash(); var authority = PlanningRepairPatch.Authority(state, 4);
        var restored = PlannerFixture.Clone(state); Assert.Equal(hash, restored.ComputeArtifactHash());
        restored.OutcomeBindings![0].Outputs.Clear();
        Assert.NotEqual(hash, restored.ComputeArtifactHash()); Assert.NotEqual(authority, PlanningRepairPatch.Authority(restored, 4));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(restored));
        var revised = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Change the requested work" }, runtime, Ct);
        Assert.Null(revised.OutcomeBindings); Assert.Null(revised.ApprovedHash); Assert.Null(revised.Yaml); Assert.NotNull(revised.Request.Baseline);
        Assert.Equal(state.ModelCalls, revised.ModelCalls);
    }

    [Fact]
    public async Task AcceptedExternalWorkCannotBeDowngradedByAnotherProposal()
    {
        var state = await State(); state.Plan = null; state.OutcomeBindings = null; state.Requirements!.Inputs = null;
        var runtime = new TestRuntime { Proposal = new() { Requirements = new() { Summary = state.Requirements!.Summary,
            Inputs = [], Outcomes = [Outcome("work", "data")] }, Plan = PlanningCorpus.Greeting(), OutcomeBindings = [new("work", [], ["message"])] } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Contains(result.Diagnostics, d => d.Code == "REQUIREMENTS_CHANGED");
        Assert.Equal("write", Assert.Single(result.Requirements!.Outcomes).Execution); Assert.Null(result.Plan); Assert.Null(result.Yaml);
    }

    [Fact]
    public async Task MissingSupportClearsStaleRepairAuthorityAndApprovalEvidence()
    {
        var state = await State(); state.RevisionScope = ["/tasks/task/operation"];
        state.Catalog!.Capabilities.Single(c => c.Id == "issued").EffectKind = "read";
        state.ValidationResults = [new("old", "passed", "Obsolete evidence", [])]; state.ApprovedHash = "obsolete";
        var runtime = new TestRuntime { Proposal = new() { Plan = state.Plan, OutcomeBindings = state.OutcomeBindings } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Empty(result.RevisionScope); Assert.Empty(result.ValidationResults);
        Assert.Null(result.ApprovedHash); Assert.Null(result.Yaml);
        Assert.Contains(result.Diagnostics, d => d.Code == "OUTCOME_OPERATION_REQUIRED");
        Assert.Single(state.RevisionScope); Assert.Equal("obsolete", state.ApprovedHash);
    }

    [Theory]
    [InlineData("agent.run")]
    [InlineData("mcp.call")]
    public async Task ResolvedExecutionContractsRemainValidForAdaptiveOrDeterministicWork(string executor)
    {
        var state = await State(); var capability = state.Catalog!.Capabilities.Single(c => c.Id == "issued");
        capability.StepType = executor; capability.EffectKind = "execute";
        if (!state.Catalog.AllowedStepTypes.Contains(executor)) state.Catalog.AllowedStepTypes.Add(executor);
        state.Requirements!.Outcomes[0] = Outcome("work", "execute");
        Assert.Empty(PlanningOutcomeValidation.Findings(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionFourRepairCanClarifyAndRejectsRebindingDuringRecovery(bool corrupt)
    {
        var state = RepairPatchTests.State(); state.OutcomeVersion = 1; state.IntentVersion = 1;
        state.Requirements!.Inputs = []; state.Requirements.Outcomes = [Outcome("work", "data")];
        state.OutcomeBindings = [new("work", ["producer"], [])];
        var request = new LLMRequest { Prompt = HybridWorkflowPlanner.Prompt(state), StructuredOutputSchema = PlanningSchemas.Proposal(state) };
        Assert.Equal(4, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
        state.PendingCall = new() { Purpose = "replan", Request = request, Id = "retained" }; state.ModelCalls = 2; state.ReplanAttempts = 1;
        var runtime = new TestRuntime { Proposal = new() { Clarifications = [PlanningClarificationTests.Question()] } };
        if (corrupt)
        {
            state.OutcomeBindings[0].TaskIds[0] = "unrelated";
            await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct));
            Assert.Empty(runtime.Calls); return;
        }
        var result = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Clarification, result.Status); Assert.Single(result.PendingQuestions!);
        Assert.Equal("producer", Assert.Single(Assert.Single(result.OutcomeBindings!).TaskIds));
        Assert.Equal(2, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
        Assert.Equal(request.StructuredOutputSchema.ToJsonString(), Assert.Single(runtime.Calls).StructuredOutputSchema!.ToJsonString());
    }

    [Fact]
    public async Task HistoricalApprovalsAndIssuedRequestsRetainTheirIdentityAndSchema()
    {
        var state = PlannerFixture.Session(); state.IntentVersion = 1; var runtime = new TestRuntime();
        state = await PlannerFixture.RunAsync(runtime, state);
        var hash = state.ComputeArtifactHash(); Assert.Null(state.OutcomeVersion); Assert.Null(state.OutcomeBindings);
        Assert.DoesNotContain("outcomeBindings", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        PlanningArtifactApproval.Verify(PlannerFixture.Clone(state)); Assert.Equal(hash, PlannerFixture.Clone(state).ComputeArtifactHash());
        var pending = runtime.Checkpoints.First(s => s.PendingCall is not null);
        var schema = pending.PendingCall!.Request.StructuredOutputSchema!.ToJsonString();
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(pending), new() { ExpectedRevision = pending.Revision }, runtime, Ct);
        Assert.Equal(schema, runtime.Calls.Last().StructuredOutputSchema!.ToJsonString()); Assert.Null(recovered.OutcomeVersion);
        Assert.Equal(hash, recovered.ComputeArtifactHash()); Assert.Equal(1, recovered.ModelCalls);
    }

    [Fact]
    public async Task SanitizedFailureCompilesButCannotSupportExternalOutcomesEvenWhenModelClaimsSuccess()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OutcomeCoverage", "retained-placeholder-plan.json"), Ct))!;
        var state = await State(); state.Plan = fixture["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Requirements.Outcomes = [Outcome("perform", "execute"), Outcome("publish", "write"), Outcome("cleanup", "lifecycle", always: true)];
        state.OutcomeBindings = [new("perform", ["execute_pr_review_workflow"], []), new("publish", ["execute_pr_review_workflow"], []), new("cleanup", ["cleanup_clone_directory"], [])];
        var compilation = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!); Assert.Empty(compilation.Diagnostics);
        Assert.Equal(3, PlanningOutcomeValidation.Findings(state).Count(d => d.Code == "OUTCOME_OPERATION_REQUIRED"));
        var model = new Claims(); var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "deterministic" } };
        // Execute only the unsafe historical artifact with an injected deterministic model:
        // a successful JSON response demonstrably provides no external execution evidence.
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, state.Catalog!);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject
            { ["pullRequestUrl"] = "https://example.invalid/project/pull/1", ["reviewInstructions"] = "Check correctness" }, Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(1, model.Calls);
        Assert.True(result.Outputs!["reviewResult"]!["githubReviewSubmitted"]!.GetValue<bool>());
        Assert.DoesNotContain(PlanningGraphCompiler.Enumerate(compilation.Graph!.Workflows.SelectMany(w => w.Steps.Concat(w.Finally))), n => n.Type is "mcp.call" or "agent.run");
        state.Yaml = yaml; state.Graph = compilation.Graph;
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    private sealed class Sources : ICapabilityCatalog
    {
        internal int Reads;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("one", "First source"), new("two", "Second source")]);
        public Task<CapabilityPage> ListAsync(string id, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { Reads++; return Task.FromResult(new CapabilityPage(id, cursor, [], null, Query: query)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class Claims : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""
                {"pullRequestUrl":"https://example.invalid/project/pull/1","decision":"accept","githubReviewSubmitted":true,
                 "summary":"Claimed success","diffComments":[],"checks":{"dependenciesInstalled":true,"lintersRun":true,"unitTestsRun":true,"integrationTestsRun":true,"blockingFindings":[]}}
                """) });
        }
    }
}
