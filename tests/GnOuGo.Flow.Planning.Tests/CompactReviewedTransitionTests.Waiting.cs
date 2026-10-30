using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactReviewedTransitionTests
{
    [Fact]
    public async Task IndependentClosureAndExportAdvanceWhileBusinessConsumerWaits()
    {
        var run = await WaitingBoundary();
        Assert.Equal(PlanningStatus.FinalReview, run.State.Status);
        PlanningArtifactApproval.Verify(run.State);
        var transitions = run.State.Construction!.Checkpoints.Where(checkpoint => checkpoint.Reviews.Any(
            review => review["transition"]?.ToString() == "reviewed_boundary")).ToArray();
        Assert.Equal(["/tasks/outer/body", "/root"], transitions.Select(checkpoint => checkpoint.Step!.Scope));
        foreach (var transition in transitions)
        {
            var saved = run.Runtime.Checkpoints.First(state => state.Construction!.Checkpoints.Count == transition.Sequence && state.Construction.Candidate is null);
            var waiting = Assert.Single(saved.Construction!.Compact!.SuspendedSteps!);
            Assert.Equal("waiting", waiting.Status); Assert.Equal("/root", waiting.Consumer.Scope);
            Assert.Equal(["req_1"], waiting.Consumer.RequirementIds);
            Assert.Equal("in_progress", saved.Construction.Requirements.Single(requirement => requirement.Id == "req_1").Status);
            Assert.Single(transition.Reviews, review => review["transition"]?.ToString() == "reviewed_boundary");
            Assert.DoesNotContain(transition.Reviews, review => review["purpose"]?.ToString() == PlanningConstruction.LocalPurpose);
        }
        Assert.Equal("completed", Assert.Single(run.State.Construction.Compact!.SuspendedSteps!).Status);
        var result = await ExecuteTransitionPlan(run.State);
        Assert.Equal("7", result["number"]!.ToJsonString()); Assert.Equal("8", result["laterNumber"]!.ToJsonString());
    }

    [Fact]
    public async Task ReviewedBoundaryCannotCloseTheWaitingConsumersScope()
    {
        var run = await WaitingBoundary(pendingInsideBoundary: true, stopAfterProducer: true);
        var state = run.State; var before = PlanningConstruction.Hash(state.Plan);
        Assert.Equal("/tasks/outer/body", Assert.Single(state.Construction!.Compact!.SuspendedSteps!).Consumer.Scope);
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(state)));
        Assert.Equal(before, PlanningConstruction.Hash(state.Plan));
        Assert.Contains("/tasks/outer/body", state.Construction.OpenScopes);
        Assert.Equal("waiting", Assert.Single(state.Construction.Compact.SuspendedSteps!).Status);
    }

    [Fact]
    public async Task ReviewedExportCannotTakeTheWaitingConsumersDeclaredPublicOutput()
    {
        var run = await WaitingBoundary(conflictingOutput: true, stopAfterOuter: true);
        Assert.DoesNotContain(run.State.Plan!.Root.Outputs, output => output.Name == "laterNumber");
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(run.State)));
        Assert.Equal("waiting", Assert.Single(run.State.Construction!.Compact!.SuspendedSteps!).Status);
    }

    [Fact]
    public async Task IndependentBoundaryRecoveryDoesNotLoseWaitingConsumerOrRepeatExport()
    {
        var run = await WaitingBoundary(stopAfterOuter: true);
        var accepted = PlannerFixture.Clone(run.State);
        var calls = accepted.ModelCalls;
        var next = PlanningCompactConstruction.PrepareAcceptedTransition(accepted);
        Assert.NotNull(next);
        Assert.Equal(calls, next.ModelCalls);
        Assert.Equal("/root", next.Construction!.Candidate!.Extension.Scope);
        Assert.Single(next.Construction.Candidate.Extension.Outputs);
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(next)));
        Assert.Equal("waiting", Assert.Single(next.Construction.Compact!.SuspendedSteps!).Status);
    }

    private static async Task<BusinessRun> WaitingBoundary(bool pendingInsideBoundary = false,
        bool conflictingOutput = false, bool stopAfterProducer = false, bool stopAfterOuter = false)
    {
        const string local = "Compute seven inside the inner local boundary, preserve the outer boundary's exact local result, and return public numeric number.";
        const string later = "Separately produce a seed of three, increment that seed, then double the incremented value and return public numeric laterNumber.";
        const string consumerGoal = "Double the exact incremented late value.";
        const string seedGoal = "Produce the separately required late seed.";
        const string lateGoal = "Increment the exact separately required late seed.";
        var waitingScope = pendingInsideBoundary ? "/tasks/outer/body" : "/root";
        var runtime = new TestRuntime(); var purposes = new List<string>();
        var responses = new Dictionary<string, LLMResponse>(StringComparer.Ordinal); Exception? modelFailure = null;
        var acceptedAfterScopeNavigation = false;
        runtime.Respond = (request, purpose) =>
        {
            if (responses.TryGetValue(request.ClientRequestId!, out var retained)) return retained;
            try
            {
                purposes.Add(purpose); var context = Context(request); JsonObject response;
                var current = runtime.Checkpoints.LastOrDefault();
                if (purpose == PlanningCompactConstruction.IntentPurpose)
                {
                    response = new() { ["clauses"] = new JsonArray(
                        new JsonObject { ["text"] = local + " ", ["kind"] = "requirement", ["inputs"] = new JsonArray(),
                            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "number", ["required"] = true, ["type"] = new JsonObject { ["kind"] = "number", ["nullable"] = false } }) },
                        new JsonObject { ["text"] = later, ["kind"] = "requirement", ["inputs"] = new JsonArray(),
                            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "laterNumber", ["required"] = true, ["type"] = new JsonObject { ["kind"] = "number", ["nullable"] = false } }) }) };
                }
                else if (purpose == PlanningCompactConstruction.SelectPurpose)
                {
                    var symbols = new TaskPlanSymbols(current!.Plan ?? new());
                    string scope, kind, objective; string[] requirements = ["req_0"];
                    bool close = false; string[] publicOutputs = []; TaskValue[] imports = [];
                    if (current.Construction!.Compact!.SuspendedSteps is null && (!pendingInsideBoundary || symbols.Tasks.ContainsKey("outer")))
                    { (scope, kind, objective) = (waitingScope, "value", consumerGoal); requirements = ["req_1"]; publicOutputs = ["laterNumber"]; }
                    else if (current.Construction.Compact.SuspendedSteps is not null && !symbols.Tasks.ContainsKey("lateSeed"))
                    { (scope, kind, objective) = (waitingScope, "value", seedGoal); requirements = ["req_1"]; }
                    else if (!symbols.Tasks.ContainsKey("outer")) (scope, kind, objective) = ("/root", "sequence", "Produce seven inside the required nested local boundary.");
                    else if (current.Construction.OpenScopes.Contains("/tasks/inner/body"))
                    { (scope, kind, objective) = ("/tasks/inner/body", "close", "Export and close the exact inner seven."); close = true; imports = [Output("producer")]; }
                    else if (current.Construction!.OpenScopes.Contains("/tasks/outer/body"))
                    { (scope, kind, objective) = ("/tasks/outer/body", "close", "Export and close the reviewed outer result."); close = true; imports = [Output("inner")]; }
                    else if (!current.Plan!.Root.Outputs.Any(output => output.Name == "number"))
                    { (scope, kind, objective) = ("/root", "close", "Export the reviewed public result."); publicOutputs = ["number"]; imports = [Output("outer")]; }
                    else if (!symbols.Tasks.ContainsKey("lateInput"))
                    { (scope, kind, objective) = ("/root", "value", lateGoal); requirements = ["req_1"]; imports = [Output("lateSeed")]; }
                    else
                    { (scope, kind, objective) = ("/root", "close", "Close the two completed business results."); requirements = ["req_0", "req_1"]; close = true; }
                    response = Selection(request, scope, kind, kind == "close", !close);
                    response["step"]!["objective"] = objective;
                    response["step"]!["requirementIds"] = new JsonArray(requirements.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
                    response["step"]!["outputNames"] = new JsonArray(publicOutputs.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
                    response["step"]!["inputs"] = new JsonArray(imports.Select(value => PlanningJsonTransport.TaskPlanPart(
                        PlanningConstruction.Node(value, PlanningJsonContext.Default.TaskValue))).ToArray());
                    if (kind == "close" && scope != "/root") response["step"]!["expectedOutputs"] = new JsonArray("number");
                    if (symbols.Tasks.ContainsKey("lateConsumer")) response["step"]!["closeRequirementIds"] = new JsonArray("req_0", "req_1");
                    if (!(context["scopes"] as JsonArray ?? []).Any(row => row?["path"]?.ToString() == scope))
                    {
                        // Optional scope pages must be inspected through their
                        // exact issued link before the scripted semantic choice.
                        var next = context["nextScopes"]?.ToString() ?? throw new InvalidOperationException(
                            "The desired existing scope is outside this page without an offered continuation: " + scope);
                        response["step"] = null;
                        response["evidenceRequests"] = new JsonArray(next);
                    }
                }
                else if (purpose == PlanningConstruction.DraftPurpose)
                {
                    var step = current!.Construction!.Compact!.CurrentStep!;
                    if (step.Objective == consumerGoal && step.Inputs.Count == 0) response = Suspend(context, "The incremented late seed, not the initial seed.", "req_1");
                    else
                    {
                        var extension = new PlanningConstructionExtension { Scope = step.Scope, RequirementIds = step.RequirementIds.ToList() };
                        if (step.Kind == "sequence")
                        {
                            if (!new TaskPlanSymbols(current.Plan ?? new()).Tasks.ContainsKey("outer"))
                            {
                                var outer = Container("outer");
                                var inner = Container("inner");
                                inner.Body!.Tasks = [new()
                                {
                                    Id = "localSeed", Kind = "value", Objective = "Supply three for the required local calculation of seven.",
                                    Outputs = [new("number", new() { Kind = "number", Number = 3 })]
                                }];
                                outer.Body!.Tasks = [inner];
                                extension.Tasks = [outer];
                                extension.ContinueInScope = "/tasks/inner/body";
                            }
                            else extension.Tasks = [Computed("producer", "add", Output("localSeed"), 4)];
                            // Finish the calculation across two productive fragments;
                            // the independent consumer and reviewed boundary exports
                            // are selected separately below.
                        }
                        else if (step.Objective == seedGoal) extension.Tasks = [new()
                        {
                            Id = "lateSeed", Kind = "value", Objective = "Produce exactly three for the independent later calculation.",
                            Outputs = [new("number", new() { Kind = "number", Number = 3 })]
                        }];
                        else if (step.Scope == "/tasks/inner/body")
                        {
                            extension.Outputs = [new("number", Output("producer"))];
                            extension.CloseScopes = [step.Scope];
                            extension.BoundaryTransitions =
                            [new() { Scope = "/tasks/outer/body", RequirementIds = ["req_0"], CloseScope = true, Outputs = [new("number", Output("inner"))] },
                             new() { Scope = "/root", RequirementIds = ["req_0"], Outputs = [new(conflictingOutput ? "laterNumber" : "number", Output("outer"))] }];
                        }
                        else if (step.Kind == "close")
                        {
                            extension.Outputs = [new("number", Output(step.Scope == "/root" ? "outer" : "inner"))];
                            extension.CloseScopes = step.CloseScopes!.ToList();
                        }
                        else if (step.Objective == lateGoal)
                        {
                            extension.Tasks = [Computed("lateInput", "add", Output("lateSeed"), 1)];
                            extension.ContinuationBinding = new() { ContinuationId = current.Construction.Compact.SuspendedSteps![0].Id, Binding = Output("lateInput") };
                        }
                        else
                        {
                            Assert.Equal(consumerGoal, step.Objective);
                            extension.Tasks = [Computed("lateConsumer", "multiply", Output("lateInput"), 2)];
                            extension.Outputs = [new("laterNumber", Output("lateConsumer"))];
                        }
                        response = Draft(context, extension);
                    }
                }
                else { Assert.True(IsJudge(purpose), purpose); response = CleanReview(context); }
                var result = Wire(request, response, runtime); responses.Add(request.ClientRequestId!, result); return result;
            }
            catch (Exception error) { modelFailure = error; throw; }
        };
        var state = NewTransitionSession(local + " " + later);
        for (var tick = 0; tick < 160 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); tick++)
        {
            var priorCursor = state.Construction!.ScopeCursor;
            var checkpoints = state.Construction.Checkpoints.Count;
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.True(modelFailure is null, modelFailure?.ToString());
            Assert.True(state.Diagnostics.All(diagnostic => !diagnostic.Required), string.Join(";", state.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)) +
                " Calls: " + string.Join(", ", purposes));
            if (priorCursor > 0 && state.Construction!.Checkpoints.Count > checkpoints)
            {
                acceptedAfterScopeNavigation = true;
                Assert.Equal(0, state.Construction.ScopeCursor);
                Assert.Null(state.Construction.Evidence);
            }
            if (state.Construction!.Candidate is null && (stopAfterProducer && state.Construction.Checkpoints.Any(
                    checkpoint => checkpoint.ClosedScopes.Contains("/tasks/inner/body", StringComparer.Ordinal)) ||
                stopAfterOuter && state.Construction.Checkpoints.Any(
                    checkpoint => checkpoint.ClosedScopes.Contains("/tasks/outer/body", StringComparer.Ordinal)))) break;
        }
        if (pendingInsideBoundary)
            Assert.True(acceptedAfterScopeNavigation, "The nested-consumer trace must accept a checkpoint after inspecting a later scope page.");
        return new(state, runtime, purposes, responses);
    }
}
