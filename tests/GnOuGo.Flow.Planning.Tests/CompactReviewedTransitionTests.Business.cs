using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactReviewedTransitionTests
{
    [Fact]
    public async Task ResumedBusinessChildEstablishesParentBindingWithoutDiscardingEitherConsumer()
    {
        var control = await BusinessConsumers(9);
        var candidate = await BusinessConsumers();
        Assert.Equal(PlanningStatus.FinalReview, control.State.Status);
        Assert.Equal(PlanningStatus.FinalReview, candidate.State.Status);
        PlanningArtifactApproval.Verify(control.State); PlanningArtifactApproval.Verify(candidate.State);
        Assert.Equal(PlanningConstruction.Hash(control.State.Plan), PlanningConstruction.Hash(candidate.State.Plan));
        Assert.Equal(control.State.ModelCalls - 1, candidate.State.ModelCalls);
        Assert.Equal(control.Purposes.Count(p => p == PlanningCompactConstruction.SelectPurpose) - 1,
            candidate.Purposes.Count(p => p == PlanningCompactConstruction.SelectPurpose));
        Assert.Equal(control.Purposes.Count(IsJudge), candidate.Purposes.Count(IsJudge));
        Assert.Equal(2, control.State.ReplanAttempts); Assert.Equal(2, candidate.State.ReplanAttempts);
        foreach (var run in new[] { control, candidate })
        {
            var retained = run.State.Construction!.Compact!.SuspendedSteps!;
            Assert.Equal(2, retained.Count); Assert.All(retained, item => Assert.Equal("completed", item.Status));
            Assert.Equal(retained[0].Id, retained[1].ParentId);
            Assert.Equal(["seed", "subtotal", "total"], run.State.Plan!.Root.Tasks.Select(task => task.Id));
            using var result = JsonDocument.Parse((await ExecuteTransitionPlan(run.State))["number"]!.ToJsonString());
            Assert.Equal(16, result.RootElement.GetDouble());
        }
        var child = candidate.State.Construction!.Checkpoints.Single(checkpoint => checkpoint.TaskIds.Contains("subtotal"));
        Assert.Equal(candidate.State.Construction.Compact!.SuspendedSteps![0].Id, child.Step!.ContinuationBinding!.ContinuationId);
        Assert.Empty(child.Step.ContinuationBinding.DischargedContinuationIds ?? []);
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(candidate.State, child, PlanningConstruction.LocalPurpose));
        Assert.Single(child.Reviews, row => row["transition"]?.ToString() == "resume_reviewed_consumer");
        ReportBusinessComparison("two retained business consumers", control, candidate);
    }

    [Fact]
    public async Task ParentHandoffRecoveryKeepsIssuedReviewAndCompletesEachConsumerOnce()
    {
        var run = await BusinessConsumers(stopAfterChild: true);
        var accepted = run.State;
        var pending = PlannerFixture.Clone(run.Runtime.Checkpoints.Last(state => state.PendingCall is not null &&
            state.Construction!.Candidate?.TaskIds.Contains("subtotal") == true));
        var request = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(pending)));
        var physical = run.Responses.Count;
        var recovered = await Advance(pending, run.Runtime);
        Assert.Equal(request, JsonSerializer.Serialize(run.Runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(physical, run.Responses.Count); Assert.Equal(accepted.ModelCalls, recovered.ModelCalls);
        Assert.Equal(PlanningConstruction.Hash(accepted.Plan), PlanningConstruction.Hash(recovered.Plan));
        Assert.Equal(2, recovered.Construction!.Checkpoints.Count);
        var retained = recovered.Construction.Compact!.SuspendedSteps!;
        Assert.Equal("waiting", retained[0].Status); Assert.Equal("completed", retained[1].Status);
        recovered = await Advance(PlannerFixture.Clone(recovered), run.Runtime);
        Assert.Equal(accepted.ModelCalls, recovered.ModelCalls);
        Assert.Equal("resuming", recovered.Construction!.Compact!.SuspendedSteps![0].Status);
        Assert.Equal("completed", recovered.Construction.Compact.SuspendedSteps[1].Status);
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(recovered)));
        Assert.Single(recovered.Construction.Checkpoints[1].Reviews, row => row["transition"]?.ToString() == "resume_reviewed_consumer");
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("childObjective")]
    [InlineData("childStatus")]
    [InlineData("childDependencies")]
    [InlineData("binding")]
    public async Task ChangedBusinessHandoffCannotResumeParent(string mutation)
    {
        var run = await BusinessConsumers(stopAfterChild: true);
        var state = PlannerFixture.Clone(run.State); var compact = state.Construction!.Compact!;
        switch (mutation)
        {
            case "parent": compact.SuspendedSteps![1].ParentId = null; break;
            case "childObjective": compact.SuspendedSteps![1].Consumer.Objective += " Changed required result."; break;
            case "childStatus": compact.SuspendedSteps![1].Status = "abandoned"; break;
            case "childDependencies": compact.SuspendedSteps![1].DependencyHashes["seed"] = new string('a', 64); break;
            case "binding": state.Construction.Checkpoints[1].Step!.ContinuationBinding!.Binding = Output("seed"); break;
        }
        var calls = state.ModelCalls;
        var transition = PlanningCompactConstruction.PrepareAcceptedTransition(state);
        Assert.True(transition is null || transition.Construction!.Compact!.CurrentStep is null);
        Assert.Equal(calls, state.ModelCalls);
        Assert.DoesNotContain(state.Construction.Checkpoints.SelectMany(checkpoint => checkpoint.Reviews),
            row => row["transition"]?.ToString() == "resume_reviewed_consumer" && row["checkpoint"]?.GetValue<int>() == 2);
    }

    private sealed record BusinessRun(PlanningSession State, TestRuntime Runtime, List<string> Purposes,
        Dictionary<string, LLMResponse> Responses);

    private static async Task<BusinessRun> BusinessConsumers(int? historical = null, bool stopAfterChild = false)
    {
        const string subtotal = "Compute the separately required subtotal by adding one to a separately produced seed of seven.";
        const string total = "Then compute twice that subtotal and return the public numeric result named number.";
        const string parentGoal = "Compute the required total from the exact subtotal.";
        const string childGoal = "Compute the separately required subtotal from the exact seed.";
        const string seedGoal = "Produce the required seed of seven.";
        var runtime = new TestRuntime(); var purposes = new List<string>();
        var responses = new Dictionary<string, LLMResponse>(StringComparer.Ordinal); Exception? modelFailure = null;
        var selected = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (responses.TryGetValue(request.ClientRequestId!, out var retained)) return retained;
            try
            {
                purposes.Add(purpose); var context = Context(request); JsonObject response;
                var current = runtime.Checkpoints.LastOrDefault();
                if (purpose == PlanningCompactConstruction.IntentPurpose)
                    response = request.StructuredOutputSchema!["properties"]!["clauses"] is null ? ResolveNumber(context) : new()
                    {
                        ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(), ["clauses"] = new JsonArray(
                            new JsonObject { ["text"] = subtotal + " ", ["kind"] = "requirement", ["interfaces"] = "none" },
                            new JsonObject { ["text"] = total, ["kind"] = "requirement", ["interfaces"] = "output" })
                    };
                else if (purpose == PlanningCompactConstruction.SelectPurpose)
                {
                    response = Selection(request, "/root", "value", false, false);
                    response["step"]!["requirementIds"] = new JsonArray("req_0", "req_1");
                    if (selected < 3)
                    {
                        response["step"]!["objective"] = new[] { parentGoal, childGoal, seedGoal }[selected++];
                        if (selected == 1) response["step"]!["outputNames"] = new JsonArray("number");
                    }
                    else if (current!.Construction!.Compact!.SuspendedSteps!.Any(item => item.Status == "waiting"))
                    {
                        Assert.Equal(9, historical); response["step"] = null;
                        response["resumeStep"] = new JsonObject
                        {
                            ["continuationId"] = current!.Construction!.Compact!.SuspendedSteps![0].Id,
                            ["producerCheckpoint"] = 2,
                            ["binding"] = PlanningJsonTransport.TaskPlanPart(PlanningConstruction.Node(Output("subtotal"), PlanningJsonContext.Default.TaskValue))
                        };
                    }
                    else
                    {
                        response["step"]!["kind"] = "close";
                        response["step"]!["closeScopes"] = new JsonArray("/root");
                        response["step"]!["closeRequirementIds"] = new JsonArray("req_0", "req_1");
                    }
                }
                else if (purpose == PlanningConstruction.DraftPurpose)
                {
                    var step = current!.Construction!.Compact!.CurrentStep!;
                    if (step.Objective != seedGoal && step.Inputs.Count == 0)
                        response = Suspend(context, step.Objective == parentGoal ? "The exact required subtotal." : "The exact required seed.", "req_0");
                    else
                    {
                        var chain = current.Construction.Compact.SuspendedSteps!;
                        var extension = new PlanningConstructionExtension { RequirementIds = ["req_0", "req_1"] };
                        if (step.Objective == seedGoal)
                        {
                            extension.Tasks = [Number("seed")];
                            extension.ContinuationBinding = new() { ContinuationId = chain[1].Id, Binding = Output("seed") };
                        }
                        else if (step.Objective == childGoal)
                        {
                            extension.Tasks = [Computed("subtotal", "add", Output("seed"), 1)];
                            if (historical is null)
                                extension.ContinuationBinding = new() { ContinuationId = chain[0].Id, Binding = Output("subtotal") };
                        }
                        else
                        {
                            Assert.Equal(parentGoal, step.Objective);
                            extension.Tasks = [Computed("total", "multiply", Output("subtotal"), 2)];
                            extension.Outputs = [new("number", Output("total"))];
                        }
                        response = Draft(context, extension);
                    }
                }
                else { Assert.True(IsJudge(purpose), purpose); response = CleanReview(context); }
                var result = Wire(request, response); responses.Add(request.ClientRequestId!, result); return result;
            }
            catch (Exception error) { modelFailure = error; throw; }
        };
        var state = NewTransitionSession(subtotal + " " + total, historical);
        for (var tick = 0; tick < 100 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); tick++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.True(modelFailure is null, modelFailure?.ToString());
            Assert.True(state.Diagnostics.All(diagnostic => !diagnostic.Required), string.Join(";", state.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
            if (stopAfterChild && state.Construction!.Checkpoints.Count == 2 && state.Construction.Candidate is null) break;
        }
        return new(state, runtime, purposes, responses);
    }

    private static PlanningSession NewTransitionSession(string prompt, int? historical)
    {
        var state = new PlanningSession { Request = new() { TenantId = "business-transitions", Prompt = prompt,
            MaxModelCalls = 64, MaxReplanAttempts = 4, Generation = new("medium", 32_000, 8_192),
            Options = new() { ["automatic_correction"] = true } } };
        if (historical is { } version) state = PlannerFixture.CapturedPresentation(state, version);
        else { PlanningSessionCreation.Capture(state); Assert.Equal(11, state.Construction!.Compact!.PresentationVersion); }
        // Matched initial allowances in both arms; no request is issued before capture.
        state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens = 12_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["navigation"]!["maxInputTokens"] = 12_000;
        state.Construction.Limits.PhasePolicy.Draft.MaxInputTokens = 18_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"] = 18_000;
        state.Construction.Limits.PhasePolicy.Review.MaxInputTokens = 12_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["review"]!["maxInputTokens"] = 12_000;
        return state;
    }

    private static JsonObject Suspend(JsonObject context, string need, string requirement) => new()
    {
        ["baseHash"] = context["baseHash"]!.DeepClone(), ["reselectStep"] = new JsonObject
        {
            ["reason"] = "The immediate business consumer needs its exact prerequisite.", ["disposition"] = "suspend",
            ["prerequisite"] = new JsonObject { ["need"] = need,
                ["expectedType"] = PlanningConstruction.Node(new TaskType { Kind = "number" }, PlanningJsonContext.Default.TaskType),
                ["evidenceAddresses"] = new JsonArray("requirement:" + requirement) }
        }
    };
    private static JsonObject Draft(JsonObject context, PlanningConstructionExtension extension) => new()
    { ["baseHash"] = context["baseHash"]!.DeepClone(), ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
    private static PlanTask Computed(string id, string operation, TaskValue input, double literal) => new()
    {
        Id = id, Kind = "value", Objective = "Compute the exact required " + id + ".",
        Outputs = [new("number", new() { Kind = "arithmetic", Text = operation,
            Items = [input, new() { Kind = "number", Number = literal }] })]
    };
    private static bool IsJudge(string purpose) => purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose;
    private void ReportBusinessComparison(string name, BusinessRun control, BusinessRun candidate)
    {
        Assert.Equal(control.State.ModelCalls, control.Responses.Count);
        Assert.Equal(candidate.State.ModelCalls, candidate.Responses.Count);
        Assert.Equal(control.State.ModelCalls, control.Purposes.Count);
        Assert.Equal(candidate.State.ModelCalls, candidate.Purposes.Count);
        output.WriteLine($"{name}: historical9={control.State.ModelCalls}, fresh10={candidate.State.ModelCalls}; initial authority calls included; equal executable result.");
        output.WriteLine("v9: " + string.Join(" -> ", control.Purposes));
        output.WriteLine("v10: " + string.Join(" -> ", candidate.Purposes));
    }
    private static async Task<JsonObject> ExecuteTransitionPlan(PlanningSession state)
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        return result.Outputs!.AsObject();
    }
}
