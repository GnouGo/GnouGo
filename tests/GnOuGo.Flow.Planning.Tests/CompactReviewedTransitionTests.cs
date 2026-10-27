using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactReviewedTransitionTests(ITestOutputHelper output)
{
    private const string Local = "Compute seven inside the inner local boundary and preserve the outer boundary's exact local result. ";
    private const string Later = "An unrelated later original obligation remains open.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedClaimSetChainsOuterClosureAndRootExportWithoutSelectionOrDraft(bool inference)
    {
        var control = await BoundarySession(8, export: true, chained: true, inference: inference);
        var candidate = await BoundarySession(9, export: true, chained: true, inference: inference);
        Assert.Equal(PlanningConstruction.Hash(control.State.Plan), PlanningConstruction.Hash(candidate.State.Plan));
        Assert.Equal(control.State.Construction!.OpenScopes, candidate.State.Construction!.OpenScopes);
        Assert.Equal(control.State.ModelCalls - 4, candidate.State.ModelCalls);
        Assert.Equal(5, candidate.State.Construction.Checkpoints.Count);
        Assert.Equal(["/root"], candidate.State.Construction.OpenScopes);
        Assert.All(candidate.State.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        Assert.Equal("outer", Assert.Single(candidate.State.Plan!.Root.Outputs).Value.Source);
        var producer = candidate.State.Construction.Checkpoints[2];
        Assert.Equal(["/root", "/tasks/outer/body"], producer.Step!.BoundaryTransitions!.Select(claim => claim.Scope));
        Assert.Equal(["/tasks/outer/body", "/root"], candidate.State.Construction.Checkpoints.Skip(3).Select(checkpoint => checkpoint.Step!.Scope));
        Assert.All(candidate.State.Construction.Checkpoints.Skip(3), checkpoint =>
        {
            Assert.True(checkpoint.NeedsIntermediateReview);
            Assert.True(PlanningConstructionReviewCoverage.Reviewed(candidate.State, checkpoint, PlanningConstruction.IntermediatePurpose));
            Assert.Single(checkpoint.Reviews, row => row["transition"]?.ToString() == "reviewed_boundary");
        });
        var finalDraft = candidate.Purposes.FindLastIndex(purpose => purpose == PlanningConstruction.DraftPurpose);
        Assert.All(candidate.Purposes.Skip(finalDraft + 1), purpose => Assert.Contains(purpose,
            new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose }));
        Assert.Null(candidate.State.ApprovedHash); Assert.False(candidate.State.Construction.Complete);
        ReportComparison("chain; inference=" + inference, control.State, control.Purposes, candidate.State, candidate.Purposes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedAncestorDischargesOnlyItsExplicitPrerequisiteDescendants(bool inference)
    {
        var run = await AncestorSession(inference);
        var state = run.State;
        var chain = state.Construction!.Compact!.SuspendedSteps!;
        Assert.Equal(2, chain.Count); Assert.All(chain, item => Assert.Equal("waiting", item.Status));
        Assert.Equal(chain[0].Id, chain[1].ParentId);
        var calls = state.ModelCalls; var plan = PlanningConstruction.Hash(state.Plan);
        var checkpoint = Assert.Single(state.Construction.Checkpoints);
        Assert.Equal(chain[0].Id, checkpoint.Step!.ContinuationBinding!.ContinuationId);
        Assert.Equal([chain[1].Id], checkpoint.Step.ContinuationBinding.DischargedContinuationIds);
        var changed = PlannerFixture.Clone(state);
        changed.Construction!.Compact!.SuspendedSteps![1].ParentId = null;
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(changed));
        state = await Advance(PlannerFixture.Clone(state), run.Runtime);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal("resuming", state.Construction!.Compact!.SuspendedSteps![0].Status);
        Assert.Equal("prerequisite_discharged", state.Construction.Compact.SuspendedSteps[1].Status);
        Assert.Equal(chain[0].Id, state.Construction.Compact.SuspendedSteps[1].ParentId);
        Assert.Equal("Consume the exact original seven.", state.Construction.Compact.CurrentStep!.Objective);
        Assert.Equal("producer", Assert.Single(state.Construction.Compact.CurrentStep.Inputs).Source);
        Assert.Equal(2, state.ReplanAttempts); Assert.Single(state.Plan!.Root.Tasks);
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(state,
            Assert.Single(state.Construction.Checkpoints), PlanningConstruction.LocalPurpose));
        var changedClaim = PlannerFixture.Clone(state);
        changedClaim.Construction!.Compact!.SuspendedSteps![0].Consumer.Objective += " Change the consumer's business meaning.";
        Assert.False(PlanningConstructionReviewCoverage.Reviewed(changedClaim,
            Assert.Single(changedClaim.Construction.Checkpoints), PlanningConstruction.LocalPurpose));
        Assert.All(state.Construction.Requirements, item => Assert.NotEqual("closed", item.Status));
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(state)));
        Assert.Null(state.ApprovedHash);
        output.WriteLine($"ancestor; inference={inference}; initial proof-producing calls={calls}; after resume={state.ModelCalls}; corrections={state.ReplanAttempts}; preserved checkpoints=1; runtime unverified");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedAncestorAvoidsTheHistoricalAbandonAndSemanticSelectionSegment(bool inference)
    {
        var control = await AncestorSession(inference, presentation: 8);
        var candidate = await AncestorSession(inference, presentation: 9);
        Assert.Equal(control.State.ModelCalls, candidate.State.ModelCalls);
        Assert.Equal(PlanningConstruction.Hash(control.State.Plan), PlanningConstruction.Hash(candidate.State.Plan));
        var resumedControl = await ResumeOriginal(control.State, control.Runtime, control.ModelFailure);
        var resumedCandidate = await ResumeOriginal(candidate.State, candidate.Runtime, candidate.ModelFailure);
        var before = resumedControl.Construction!.Compact!; var after = resumedCandidate.Construction!.Compact!;
        Assert.Equal(PlanningConstruction.Hash(resumedControl.Plan), PlanningConstruction.Hash(resumedCandidate.Plan));
        Assert.Equal(before.CurrentStep!.Objective, after.CurrentStep!.Objective);
        Assert.Equal(before.CurrentStep.Scope, after.CurrentStep.Scope);
        Assert.Equal(before.CurrentStep.RequirementIds, after.CurrentStep.RequirementIds);
        Assert.Equal(PlanningConstruction.Node(before.CurrentStep.Inputs, PlanningJsonContext.Default.ListTaskValue)!.ToJsonString(),
            PlanningConstruction.Node(after.CurrentStep.Inputs, PlanningJsonContext.Default.ListTaskValue)!.ToJsonString());
        Assert.Equal("abandoned", before.SuspendedSteps![1].Status);
        Assert.Equal("prerequisite_discharged", after.SuspendedSteps![1].Status);
        Assert.Equal("waiting", before.SuspendedSteps[0].Status);
        Assert.Equal("resuming", after.SuspendedSteps[0].Status);
        Assert.Equal(before.SuspendedSteps[0].Id, before.SuspendedSteps[1].ParentId);
        Assert.Equal(after.SuspendedSteps[0].Id, after.SuspendedSteps[1].ParentId);
        Assert.Equal(resumedControl.ModelCalls - 2, resumedCandidate.ModelCalls);
        Assert.Equal(3, resumedControl.ReplanAttempts); Assert.Equal(2, resumedCandidate.ReplanAttempts);
        Assert.Single(resumedControl.Construction.Checkpoints); Assert.Single(resumedCandidate.Construction.Checkpoints);
        Assert.All(resumedControl.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        Assert.All(resumedCandidate.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        ReportComparison("ancestor; inference=" + inference, resumedControl, control.Purposes, resumedCandidate, candidate.Purposes);
        output.WriteLine("v8 explicitly abandons the resumed prerequisite-only route, then selects the original consumer with the exact accepted input; its original navigation record stays waiting. v9 restores that record and retains its descendant as prerequisite_discharged. Corrections: 3 → 2. No runtime/business completion claimed.");

        static async Task<PlanningSession> ResumeOriginal(PlanningSession state, TestRuntime runtime, Func<Exception?> modelFailure)
        {
            for (var tick = 0; tick < 8; tick++)
            {
                state = await Advance(PlannerFixture.Clone(state), runtime);
                Assert.Null(modelFailure());
                Assert.Empty(state.Diagnostics);
                if (state.Construction!.Compact!.CurrentStep?.Objective == "Consume the exact original seven.")
                    return state;
            }
            throw new InvalidOperationException("The normal coordinator did not restore the original consumer.");
        }
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime, List<string> Purposes, Func<Exception?> ModelFailure)> AncestorSession(bool inference, int presentation = 9)
    {
        const string original = "Produce seven and consume that exact number; prerequisite-only navigation is not a separate business result.";
        var selection = -1; var runtime = new TestRuntime(); Exception? modelFailure = null; var purposes = new List<string>();
        runtime.Respond = (request, purpose) =>
        {
            try
            {
            purposes.Add(purpose);
            var context = Context(request); JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                response = new() { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(), ["clauses"] = new JsonArray(
                    new JsonObject { ["text"] = original, ["kind"] = "requirement", ["interfaces"] = "none" }) };
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                selection++;
                Assert.InRange(selection, 0, presentation == 8 ? 3 : 2);
                if (selection == 3)
                {
                    var retained = runtime.Checkpoints[^1].Construction!.Compact!.SuspendedSteps!;
                    Assert.Equal("abandoned", retained[1].Status);
                    // Historical abandonment keeps replacement authority, which
                    // prohibits ResumeStep. Its ordinary selection remains legal.
                    response = Selection(request, "/root", "value", false, false);
                    response["step"]!["objective"] = "Consume the exact original seven.";
                    response["step"]!["inputs"] = new JsonArray(PlanningJsonTransport.TaskPlanPart(
                        PlanningConstruction.Node(Output("producer"), PlanningJsonContext.Default.TaskValue)));
                    return Wire(request, response);
                }
                response = Selection(request, "/root", inference && selection == 2 ? "transform" : "value", false, false);
                response["step"]!["objective"] = selection switch
                { 0 => "Consume the exact original seven.", 1 => "Resolve a prerequisite-only route to the exact number.", _ => "Produce the exact original seven." };
            }
            else if (purpose == PlanningConstruction.DraftPurpose && selection < 2)
                response = new() { ["baseHash"] = context["baseHash"]!.DeepClone(), ["reselectStep"] = new JsonObject
                {
                    ["reason"] = "This tentative route requires the exact original numeric producer.", ["disposition"] = "suspend",
                    ["prerequisite"] = new JsonObject { ["need"] = selection == 0 ? "The original seven." : "The original numeric producer for the prerequisite-only route.",
                        ["expectedType"] = PlanningConstruction.Node(new TaskType { Kind = "number" }, PlanningJsonContext.Default.TaskType),
                        ["evidenceAddresses"] = new JsonArray("requirement:req_0") }
                } };
            else if (purpose == PlanningConstruction.DraftPurpose)
            {
                var compact = runtime.Checkpoints[^1].Construction!.Compact!; var chain = compact.SuspendedSteps!;
                if (presentation == 8 && compact.ResumingStepId == chain[1].Id)
                    response = new() { ["baseHash"] = context["baseHash"]!.DeepClone(), ["reselectStep"] = new JsonObject
                    {
                        ["reason"] = "The reviewed producer now supplies the original consumer directly; this prerequisite-only route adds no required work.",
                        ["disposition"] = "abandon", ["prerequisite"] = null
                    } };
                else
                    response = new() { ["baseHash"] = context["baseHash"]!.DeepClone(), ["extension"] = PlanningConstruction.Node(new PlanningConstructionExtension
                    {
                        RequirementIds = ["req_0"], Tasks = [inference ? Inference("producer") : Number("producer")], ContinuationBinding = new()
                        { ContinuationId = chain[presentation == 9 ? 0 : 1].Id, Binding = Output("producer"),
                            DischargedContinuationIds = presentation == 9 ? [chain[1].Id] : null }
                    }, PlanningJsonContext.Default.PlanningConstructionExtension) };
            }
            else { Assert.Contains(purpose, new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose }); response = CleanReview(context); }
            return Wire(request, response);
            }
            catch (Exception failure) { modelFailure = failure; throw; }
        };
        var state = PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "ancestor-transition", Prompt = original,
            MaxModelCalls = 64, MaxReplanAttempts = 4, Generation = new("medium", 32_000, 8_192), Options = new() { ["automatic_correction"] = true } } }, presentation);
        for (var tick = 0; tick < 40; tick++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Null(modelFailure);
            Assert.Empty(state.Diagnostics);
            if (state.Construction!.Checkpoints.Count == 1 && state.Construction.Candidate is null) return (state, runtime, purposes, () => modelFailure);
        }
        throw new InvalidOperationException("The normal producer review did not finish.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedExistingBoundaryAvoidsOnlyItsSemanticNavigation(bool export)
    {
        var control = await BoundarySession(8, export);
        var candidate = await BoundarySession(9, export);
        Assert.Equal(PlanningConstruction.Hash(control.State.Plan), PlanningConstruction.Hash(candidate.State.Plan));
        Assert.Equal(control.State.Construction!.OpenScopes, candidate.State.Construction!.OpenScopes);
        Assert.Equal(control.State.Construction.Requirements.Select(item => item.Status), candidate.State.Construction.Requirements.Select(item => item.Status));
        Assert.All(candidate.State.Construction.Requirements, item => Assert.NotEqual("closed", item.Status));
        Assert.Equal(4, candidate.State.Construction.Checkpoints.Count);
        Assert.Equal(control.Purposes.Count(purpose => purpose == PlanningCompactConstruction.SelectPurpose) - 1,
            candidate.Purposes.Count(purpose => purpose == PlanningCompactConstruction.SelectPurpose));
        Assert.Equal(control.Purposes.Count(purpose => purpose == PlanningConstruction.DraftPurpose) - (export ? 1 : 0),
            candidate.Purposes.Count(purpose => purpose == PlanningConstruction.DraftPurpose));
        Assert.Equal(control.State.ModelCalls - (export ? 2 : 1), candidate.State.ModelCalls);
        var checkpoint = candidate.State.Construction.Checkpoints[^1];
        Assert.True(checkpoint.NeedsIntermediateReview);
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(candidate.State, checkpoint, PlanningConstruction.IntermediatePurpose));
        Assert.Single(checkpoint.Reviews, row => row["transition"]?.ToString() == "reviewed_boundary");
        Assert.Null(candidate.State.ApprovedHash); Assert.False(candidate.State.Construction.Complete);
        Assert.Equal(0, candidate.State.ReplanAttempts);
        ReportComparison("boundary; export=" + export, control.State, control.Purposes, candidate.State, candidate.Purposes);
    }

    [Fact]
    public async Task ExactReviewedTransitionDoesNotInferUnknownBehaviorConservation()
    {
        var run = await BoundarySession(9, export: true, stopAfterProducer: true, inference: true);
        var state = run.State;
        Assert.Null(PlanningConstructionDependencies.Capture(state, state.Plan!,
            new JsonObject { ["kind"] = "task", ["id"] = "producer" }));
        Assert.NotNull(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(state)));
        var changed = PlannerFixture.Clone(state);
        new TaskPlanSymbols(changed.Plan!).Tasks["producer"].Task.Objective += " Then reinterpret the supplied value.";
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(changed));
        Assert.Equal(3, changed.Construction!.Checkpoints.Count);
        Assert.Null(changed.ApprovedHash);
    }

    [Fact]
    public async Task BoundaryTransitionRecoveryPreservesIssuedReviewAndDoesNotApplyTwice()
    {
        var run = await BoundarySession(9, export: true, stopBeforeTransitionReview: true);
        var state = run.State; var calls = state.ModelCalls;
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Empty(state.Construction.Candidate!.TaskIds);
        Assert.False(state.Construction.Candidate.LocalReviewed);
        Assert.Equal(3, state.Construction.Checkpoints.Count);
        var original = PlanningConstruction.Hash(state.Plan);
        var physicalCalls = 0; var receipts = new Dictionary<string, LLMResponse>(StringComparer.Ordinal);
        var respond = run.Runtime.Respond!;
        run.Runtime.Respond = (request, purpose) =>
        {
            if (receipts.TryGetValue(request.ClientRequestId!, out var saved)) return saved;
            physicalCalls++;
            var response = respond(request, purpose); receipts.Add(request.ClientRequestId!, response); return response;
        };
        var accepted = await Advance(PlannerFixture.Clone(state), run.Runtime);
        Assert.Equal(calls + 1, accepted.ModelCalls);
        var issuedState = PlannerFixture.Clone(run.Runtime.Checkpoints.Last(saved => saved.PendingCall is not null &&
            saved.Construction!.Candidate?.Reviews.Any(row => row["transition"]?.ToString() == "reviewed_boundary") == true));
        var issued = JsonSerializer.Serialize(issuedState.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var frozenContext = issuedState.Construction!.PendingReviewContext!.ToJsonString();
        var recovered = await Advance(issuedState, run.Runtime);
        Assert.Equal(issued, JsonSerializer.Serialize(run.Runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(frozenContext, Context(run.Runtime.Calls[^1]).ToJsonString());
        Assert.Equal(1, physicalCalls); Assert.Equal(accepted.ModelCalls, recovered.ModelCalls);
        Assert.Equal(PlanningConstruction.Hash(accepted.Plan), PlanningConstruction.Hash(recovered.Plan));
        Assert.Equal(4, recovered.Construction!.Checkpoints.Count);
        Assert.NotEqual(original, PlanningConstruction.Hash(recovered.Plan));
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(PlannerFixture.Clone(recovered)));
        Assert.Single(new TaskPlanSymbols(recovered.Plan!).Scopes.Single(scope => scope.Path == "/tasks/outer/body").Source.Outputs);
        Assert.Equal(0, recovered.ReplanAttempts); Assert.Null(recovered.PendingCall); Assert.Null(recovered.ApprovedHash);
        output.WriteLine($"prepared transition recovery: total logical calls={recovered.ModelCalls}; physical review responses={physicalCalls}; exact issued request and context reused; accepted checkpoints=4");
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("source")]
    [InlineData("policy")]
    [InlineData("plan")]
    [InlineData("review")]
    public async Task StaleBoundaryAuthorityRetainsSemanticSelection(string mutation)
    {
        var run = await BoundarySession(9, export: false, stopAfterProducer: true);
        var state = PlannerFixture.Clone(run.State);
        switch (mutation)
        {
            case "claim": state.Construction!.Checkpoints[^1].Step!.BoundaryTransitions![0].CloseScope = false; break;
            case "source": state.Construction!.Compact!.Sources[0].Text += " changed"; break;
            case "policy": state.Request.Policy.DeniedCapabilityIds = ["changed"]; break;
            case "plan": state.Plan!.Root.Tasks[0].Objective += " changed"; break;
            case "review": state.Construction!.Checkpoints[^1].Reviews.RemoveAll(row => row["outcome"]?.ToString() == "unverified"); break;
        }
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(state));
        Assert.Equal(3, state.Construction!.Checkpoints.Count);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime, List<string> Purposes)> BoundarySession(int presentation, bool export,
        bool stopBeforeTransitionReview = false, bool stopAfterProducer = false, bool chained = false, bool inference = false)
    {
        var local = chained ? Local + "Return the public numeric result named number. " : Local;
        var purposes = new List<string>(); var selection = -1; Exception? modelFailure = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
            purposes.Add(purpose);
            var context = Context(request);
            JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose && request.StructuredOutputSchema!["properties"]!["clauses"] is null)
                response = ResolveNumber(context);
            else if (purpose == PlanningCompactConstruction.IntentPurpose)
                response = new() { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(), ["clauses"] = new JsonArray(
                    new JsonObject { ["text"] = local, ["kind"] = "requirement", ["interfaces"] = chained ? "output" : "none" },
                    new JsonObject { ["text"] = Later, ["kind"] = "requirement", ["interfaces"] = "none" }) };
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                selection++;
                Assert.InRange(selection, 0, chained ? 4 : 3);
                response = Selection(request, selection switch { 0 or 4 => "/root", 1 or 3 => "/tasks/outer/body", _ => "/tasks/inner/body" },
                    selection < 2 ? "sequence" : selection == 2 ? inference ? "transform" : "value" : "close", selection >= 3,
                    selection >= 3 && export);
                if (chained && selection == 3) response["step"]!["closeScopes"] = new JsonArray("/tasks/outer/body");
                if (selection == 4) response["step"]!["outputNames"] = new JsonArray("number");
            }
            else if (purpose == PlanningConstruction.DraftPurpose)
            {
                var extension = selection switch
                {
                    0 => new PlanningConstructionExtension { Tasks = [Container("outer")], RequirementIds = ["req_0"] },
                    1 => new PlanningConstructionExtension { Scope = "/tasks/outer/body", Tasks = [Container("inner")], RequirementIds = ["req_0"] },
                    2 => new PlanningConstructionExtension { Scope = "/tasks/inner/body", Tasks = [inference ? Inference("producer") : Number("producer")], RequirementIds = ["req_0"],
                        Outputs = [new("number", Output("producer"))], CloseScopes = ["/tasks/inner/body"],
                        BoundaryTransitions = presentation != 9 ? null : chained ?
                        [new() { Scope = "/root", RequirementIds = ["req_0"], Outputs = [new("number", Output("outer"))] },
                         new() { Scope = "/tasks/outer/body", RequirementIds = ["req_0"], CloseScope = true, Outputs = [new("number", Output("inner"))] }] :
                        [new() { Scope = "/tasks/outer/body", RequirementIds = ["req_0"], CloseScope = !export, Outputs = export ? [new("number", Output("inner"))] : [] }] },
                    3 => new PlanningConstructionExtension { Scope = "/tasks/outer/body", RequirementIds = ["req_0"], Outputs = [new("number", Output("inner"))],
                        CloseScopes = chained ? ["/tasks/outer/body"] : [] },
                    _ => new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["req_0"], Outputs = [new("number", Output("outer"))] }
                };
                response = new() { ["baseHash"] = context["baseHash"]!.DeepClone(),
                    ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
            }
            else
            {
                Assert.Contains(purpose, new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose });
                response = CleanReview(context);
            }
            return Wire(request, response);
            }
            catch (Exception failure) { modelFailure = failure; throw; }
        } };
        var state = PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "reviewed-transition", Prompt = local + Later,
            MaxModelCalls = 64, Generation = new("medium", 32_000, 8_192), Options = new() { ["automatic_correction"] = true } } }, presentation);
        for (var tick = 0; tick < 60; tick++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Null(modelFailure);
            Assert.Empty(state.Diagnostics);
            if (stopAfterProducer && state.Construction!.Checkpoints.Count == 3 && state.Construction.Candidate is null ||
                stopBeforeTransitionReview && state.Construction!.Checkpoints.Count == 3 && state.Construction.Candidate?.Reviews
                    .Any(row => row["transition"]?.ToString() == "reviewed_boundary") == true || state.Construction!.Checkpoints.Count == (chained ? 5 : 4))
                return (state, runtime, purposes);
        }
        throw new InvalidOperationException("The real coordinator did not reach its reviewed boundary.");
    }

    private static JsonObject ResolveNumber(JsonObject context) => new()
    {
        ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(), ["interfaces"] = new JsonObject
        {
            ["inputs"] = new JsonArray(), ["outputs"] = PlanningConstruction.Node(new List<TaskInput>
                { new() { Name = "number", Required = true, Type = new() { Kind = "number" } } }, PlanningJsonContext.Default.ListTaskInput),
            ["dispositions"] = new JsonArray(context["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
            {
                ["sourceId"] = source!["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(), ["kind"] = "interfaces",
                ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray("number"), ["reason"] = "The original explicitly declares the public numeric result.",
                ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() })
            }).ToArray())
        }, ["evidenceRequests"] = null, ["clarifications"] = null
    };

    private static JsonObject Selection(LLMRequest request, string scope, string kind, bool close, bool export)
    {
        var response = new JsonObject
        {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(),
        ["step"] = new JsonObject { ["scope"] = scope, ["kind"] = kind, ["objective"] = "Preserve the exact local result and required boundary.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["operationIds"] = new JsonArray(),
            ["closeScopes"] = close ? export ? new JsonArray() : new JsonArray(scope) : null,
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = export ? new JsonArray("Export the reviewed inner number.") : new JsonArray(),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["groupId"] = null, ["prerequisites"] = null },
        ["evidenceRequests"] = null, ["clarifications"] = null
        };
        if (request.StructuredOutputSchema["properties"]!["resumeStep"] is not null) response["resumeStep"] = null;
        return response;
    }

    private static JsonObject CleanReview(JsonObject context)
    {
        var action = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() };
        if (context["reviewPropositions"] is JsonArray propositions)
            action["acknowledgments"] = new JsonArray(propositions.Select(item => (JsonNode)new JsonObject
                { ["id"] = item!["id"]!.DeepClone(), ["applicability"] = "broad" }).ToArray());
        return new() { ["action"] = action, ["uncertainty"] = "Runtime remains unverified; unrelated original work is still open." };
    }

    private void ReportComparison(string name, PlanningSession control, List<string> controlPurposes, PlanningSession candidate, List<string> candidatePurposes)
    {
        output.WriteLine($"{name}; simulated normal-session dispatches including initial proof: v8={control.ModelCalls}, v9={candidate.ModelCalls}; same plan={PlanningConstruction.Hash(candidate.Plan)}; no live/runtime acceptance");
        output.WriteLine("v8 trace: " + string.Join(" → ", controlPurposes));
        output.WriteLine("v9 trace: " + string.Join(" → ", candidatePurposes));
        foreach (var purpose in controlPurposes.Concat(candidatePurposes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            output.WriteLine($"{purpose}: {controlPurposes.Count(item => item == purpose)} → {candidatePurposes.Count(item => item == purpose)}");
    }

    private static PlanTask Container(string id) => new() { Id = id, Kind = "sequence", Objective = "Preserve the required local boundary.", Body = new() };
    private static PlanTask Inference(string id) => new() { Id = id, Kind = "transform", Mode = "interpret",
        Objective = "Return the exact supplied seven as the numeric number field.", Inputs = [new("source", new() { Kind = "number", Number = 7 })],
        ResultType = new() { Kind = "object", Fields = [new() { Name = "number", Type = new() { Kind = "number" } }] } };
    private static PlanTask Number(string id) => new() { Id = id, Kind = "value", Objective = "Produce exactly seven.", Outputs = [new("number", new() { Kind = "number", Number = 7 })] };
    private static TaskValue Output(string id) => new() { Kind = "output", Source = id, Port = "number" };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response)
    {
        // Selection already supplies its complete issued shape. The historical
        // corpus adapter cannot discriminate a factored nullable kind branch.
        var schema = request.StructuredOutputSchema ?? throw new InvalidOperationException("The scripted response requires its exact issued schema.");
        var value = response.ContainsKey("step") ? response.DeepClone() :
            PlanningCorpus.Transport(response, schema.AsObject(), schema.AsObject())!;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(value, schema));
        return new() { Json = value, Text = value.ToJsonString() };
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
