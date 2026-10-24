using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionDefectProgressTests
{
    [Fact]
    public void UnrelatedInputUseAndOperationPurposeDoNotResolveTheAffectedConsumer()
    {
        var state = Ready(); Reject(state);
        var before = state.Construction!.Candidate!.Hash;
        state.Construction.Candidate.Plan.Root.Tasks[0].Objective = "Use caller text in this operation";
        state.Construction.Candidate.Plan.Root.Tasks.Add(new() { Id = "other", Kind = "value", Objective = "Read elsewhere",
            Outputs = [new("text", new() { Kind = "input", Source = "text" })] });
        Refresh(state);
        Assert.NotEqual(before, state.Construction.Candidate.Hash);
        var progress = Single(state);
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.Equal(progress["before"]!["hash"]!.ToString(), progress["current"]!["hash"]!.ToString());
        Assert.False(PlanningConstruction.HasNewDefectEvidence(state, Context(state), progress, "/requirements/0", "/draft/tasks/0"));
        Assert.Null(progress["requiredSource"]);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void ChangingTheConsumerBindingIsRelevantButDoesNotDeclareSemanticResolution()
    {
        var state = Ready(); Reject(state);
        state.Construction!.Candidate!.Plan.Root.Tasks[0].Inputs[0] = new("text", new() { Kind = "input", Source = "text" });
        Refresh(state);
        var progress = Single(state);
        Assert.Equal("changed", progress["status"]!.ToString());
        Assert.Null(progress["resolved"]);
        Assert.Equal("input", progress["current"]!["value"]!["inputs"]![0]!["value"]!["kind"]!.ToString());
        Assert.False(state.Construction.Candidate.LocalReviewed);
    }

    [Theory]
    [InlineData("interpret")]
    [InlineData("extract")]
    public void TransformInstructionsRemainExecutableEvidence(string mode)
    {
        var state = Ready();
        var task = state.Construction!.Candidate!.Plan.Root.Tasks[0];
        task.Kind = "transform"; task.Mode = mode; task.Operation = null;
        Refresh(state); Reject(state);
        task.Objective = "Return every supplied record including duplicates";
        Refresh(state);
        Assert.Equal("changed", Single(state)["status"]!.ToString());
    }

    [Fact]
    public void LiteralInstructionsAndNamedBindingsRemainExact()
    {
        var state = Ready(); Reject(state, "/draft/tasks/0/inputs/0/value");
        var task = state.Construction!.Candidate!.Plan.Root.Tasks[0];
        task.Inputs.Insert(0, new("unrelated", new() { Kind = "number", Number = 1 }));
        Refresh(state);
        var unchanged = Single(state);
        Assert.Equal("unchanged", unchanged["status"]!.ToString());
        Assert.Equal("/draft/tasks/0/inputs/1/value", unchanged["currentImplementationPointer"]!.ToString());
        task.Inputs[1] = new("text", new() { Kind = "string", Text = "Different literal instruction" });
        Refresh(state);
        Assert.Equal("changed", Single(state)["status"]!.ToString());
    }

    [Fact]
    public void ReorderedTasksBindByIdentityButRenamedOrAmbiguousTasksStayUnverified()
    {
        var state = Ready(); Reject(state);
        var tasks = state.Construction!.Candidate!.Plan.Root.Tasks;
        tasks.Insert(0, new() { Id = "another", Kind = "value", Objective = "Other work" });
        Refresh(state);
        var progress = Single(state);
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.Equal("/draft/tasks/1", progress["currentImplementationPointer"]!.ToString());
        tasks[1].Id = "renamed";
        Refresh(state);
        Assert.Equal("unverified", Single(state)["status"]!.ToString());
        tasks[1].Id = "consumer"; tasks[0].Id = "consumer";
        Refresh(state);
        Assert.Equal("unverified", Single(state)["status"]!.ToString());
    }

    [Fact]
    public void WithdrawalOfAClosureClaimIsNotAnUnchangedFailure()
    {
        var state = Ready();
        state.Construction!.Compact!.CurrentStep!.CloseRequirementIds = ["req_0"];
        Reject(state);
        var planHash = state.Construction.Candidate!.Hash;
        state.Construction.Compact.CurrentStep.CloseRequirementIds.Clear();
        Assert.Equal(planHash, state.Construction.Candidate.Hash);
        Assert.Equal("claims_changed", Single(state)["status"]!.ToString());
        Assert.Empty(state.Construction.Candidate.AuthorizedPaths ?? []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AChangedOriginalOrAcceptedBaselineInvalidatesTheComparison(bool original)
    {
        var state = Ready(); Reject(state);
        if (original) state.Construction!.Compact!.Sources[0].Version = "new-version";
        else state.Plan!.Inputs.Add(new() { Name = "another", Type = new() { Kind = "number" } });
        Assert.Equal("basis_changed", Single(state)["status"]!.ToString());
    }

    [Fact]
    public void RemappedOriginalSourcesInvalidateTheComparisonWithoutChangingTheirText()
    {
        var state = Ready();
        state.Construction!.Compact!.RequirementSources["req_0"] = ["source_0"];
        Reject(state);
        state.Construction.Compact.RequirementSources["req_0"].Clear();
        state.Construction.Compact.GlobalSourceIds.Add("source_0");
        Assert.Equal("basis_changed", Single(state)["status"]!.ToString());
    }

    [Theory]
    [InlineData("version", "basis_changed")]
    [InlineData("contract", "basis_changed")]
    [InlineData("unrelated", "unchanged")]
    public void OnlySelectedOriginalContractChangesInvalidateTheComparison(string change, string expected)
    {
        var state = CoordinatorReady(); Reject(state);
        if (change == "version") state.Discovery.Resolved[0].Version = "v2";
        else if (change == "contract") state.Discovery.Resolved[0].Description = "Different original constraints at the same retained version.";
        else state.Discovery.Resolved.Add(new() { Id = "unrelated", Version = "v1", Kind = "tool", Description = "Unrelated discovery." });
        Assert.Equal(expected, Single(state)["status"]!.ToString());
    }

    [Fact]
    public void AdditionalEvidencePermitsAssessmentButIsNotAutomaticallyAResolvedDefect()
    {
        var state = Ready(); Reject(state);
        state.Construction!.Evidence = new() { ["contract:exact"] = new JsonObject { ["description"] = "An inspected original" } };
        var progress = Single(state);
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.True(progress["evidenceChanged"]!.GetValue<bool>());
        Assert.Null(progress["resolved"]);
    }

    [Fact]
    public void RemovingEvidenceOrAddingAnUnrelatedOriginalDoesNotImproveAffectedEvidence()
    {
        var state = Ready();
        state.Construction!.Evidence = new() { ["task:consumer"] = new JsonObject { ["id"] = "consumer", ["kind"] = "operation", ["objective"] = "Original" } };
        Reject(state);
        state.Construction.Evidence = null;
        var removed = Single(state);
        Assert.False(removed["evidenceChanged"]!.GetValue<bool>());
        Assert.False(removed["evidenceImproved"]!.GetValue<bool>());
        state.Construction.Evidence = new() { ["task:unrelated"] = new JsonObject { ["id"] = "unrelated", ["kind"] = "value" } };
        var unrelated = Single(state);
        Assert.True(unrelated["evidenceChanged"]!.GetValue<bool>());
        Assert.False(unrelated["evidenceImproved"]!.GetValue<bool>());
        state.Construction.Evidence["task:consumer"] = new JsonObject { ["id"] = "consumer", ["kind"] = "operation", ["inputs"] = new JsonArray() };
        Assert.True(Single(state)["evidenceImproved"]!.GetValue<bool>());
    }

    [Fact]
    public void AContestMustCiteNewExactSupportRatherThanMerelyHaveUnrelatedNewEvidence()
    {
        var state = Ready(); Reject(state);
        state.Construction!.Evidence = new() { ["task:accepted"] = new JsonObject { ["id"] = "accepted", ["kind"] = "value" } };
        var context = Context(state); var progress = Single(state);
        Assert.False(PlanningConstruction.HasNewDefectEvidence(state, context, progress, "/requirements/0", "/draft/tasks/0"));
        Assert.True(PlanningConstruction.HasNewDefectEvidence(state, context, progress, "/requirements/0", "/evidence/task:accepted"));
        state.Construction.Evidence.Clear();
        Assert.False(PlanningConstruction.HasNewDefectEvidence(state, Context(state), progress, "/requirements/0", "/evidence/task:accepted"));
    }

    [Fact]
    public async Task ReplacedDraftKeepsExactCitedAcceptedTaskReferenceableWithoutNewProgressOrAuthority()
    {
        var state = AcceptedTaskWitness();
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        var progress = Assert.Single(context["defectProgress"]!.AsArray())!;
        Assert.Equal("/evidence/task:accepted", progress["currentImplementationPointer"]?.ToString());
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.False(progress["evidenceChanged"]!.GetValue<bool>());
        Assert.False(progress["evidenceImproved"]!.GetValue<bool>());
        Assert.False(PlanningConstruction.HasNewDefectEvidence(state, context, progress.AsObject(), "/requirements/0", "/evidence/task:accepted"));
        Assert.Null(context["evidence"]!["task:unrelated"]);
        var schema = request.StructuredOutputSchema!.AsObject();
        var complete = schema["properties"]!["action"]!["anyOf"]!.AsArray().Single(action => action?["properties"]?["kind"]?["enum"]?[0]?.ToString() == "findings");
        Assert.Contains(complete!["properties"]!["resolutions"]!["items"]!["properties"]!["implementation"]!["enum"]!.AsArray(),
            pointer => pointer?.ToString() == "/evidence/task:accepted");
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var recovered = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)!;
        var recoveredContext = HybridWorkflowPlanner.CreateConstructionReviewRequest(recovered, intermediate: false).Context;
        Assert.Equal(progress.ToJsonString(), Assert.Single(recoveredContext["defectProgress"]!.AsArray())!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(context["evidence"], recoveredContext["evidence"]));

        // A request issued before this presentation change had no restored evidence.
        // Recovery must use those exact saved bytes, not rebuild the improved context.
        var retainedContext = context.DeepClone().AsObject(); retainedContext["evidence"] = null;
        var retainedProgress = retainedContext["defectProgress"]![0]!;
        retainedProgress["status"] = "unverified"; retainedProgress["current"] = null;
        retainedProgress["currentImplementationPointer"] = null;
        var oldPrompt = request.Prompt.Replace(context.ToJsonString(), retainedContext.ToJsonString(), StringComparison.Ordinal);
        Assert.NotEqual(request.Prompt, oldPrompt);
        var issued = PlanningModelCalls.PrepareRequest(recovered, PlanningConstruction.LocalPurpose, oldPrompt,
            HybridWorkflowPlanner.ConvergentConstructionReviewSchema(retainedContext), recovered.Construction!.Limits.ReviewOutputTokens);
        recovered.Construction.PendingReviewContext = retainedContext;
        recovered.Construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            recovered.Construction.Candidate!.Hash + "\n" + retainedContext.ToJsonString());
        recovered.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued };
        recovered.ModelCalls++;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = recovered.ModelCalls;
        var runtime = new TestRuntime { Respond = (pending, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(pending, PlanningJsonContext.Default.LLMRequest));
            Assert.Null(JsonNode.Parse(pending.Prompt[(pending.Prompt.LastIndexOf('\n') + 1)..])!["evidence"]);
            throw new OperationCanceledException();
        } };
        recovered = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(recovered),
            new() { ExpectedRevision = recovered.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(calls, recovered.ModelCalls);
        Assert.Equal(0, recovered.ReplanAttempts); Assert.Empty(recovered.Construction!.Checkpoints);
        Assert.Equal(exact, JsonSerializer.Serialize(recovered.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest));
    }

    [Theory]
    [InlineData("candidate_only")]
    [InlineData("candidate_changed")]
    [InlineData("candidate_moved")]
    [InlineData("accepted_changed")]
    [InlineData("invalidated")]
    [InlineData("source_changed")]
    [InlineData("stale_supplied_evidence")]
    [InlineData("oversized")]
    public void UnverifiedOrOversizedAcceptedTaskEvidenceRemainsAnExplicitRetrieval(string change)
    {
        var state = AcceptedTaskWitness(change);
        if (change == "candidate_changed") state.Construction!.Candidate!.Plan.Root.Tasks.Single(task => task.Id == "accepted").Objective = "Different actual task";
        if (change == "candidate_moved")
        {
            var scope = state.Construction!.Candidate!.Plan.Root; var task = scope.Tasks.Single(task => task.Id == "accepted");
            scope.Tasks.Remove(task); scope.Always.Add(task);
        }
        if (change == "accepted_changed") state.Plan!.Root.Tasks.Single(task => task.Id == "accepted").Objective = "Changed accepted original";
        if (change == "invalidated") state.Plan!.Root.Tasks.RemoveAll(task => task.Id == "accepted");
        if (change == "source_changed") state.Construction!.Compact!.Sources[0].Version = "changed";
        var context = Context(state);
        Assert.NotNull(PlanningConstruction.DefectProgressContext(state, context));
        Assert.Null(context["evidence"]);
        Assert.Null(state.Construction!.Evidence);
    }

    private static PlanningSession AcceptedTaskWitness(string change = "unchanged")
    {
        var state = CoordinatorReady();
        var consumer = state.Construction!.Candidate!.Plan.Root.Tasks.Single();
        var accepted = new PlanTask { Id = "accepted", Kind = "value", Objective = change == "oversized" ? new string('x', 2048) : "Accepted value",
            Outputs = [new("number", new() { Kind = "number", Number = 2 })] };
        if (change != "candidate_only") state.Plan!.Root.Tasks.Add(accepted);
        state.Plan!.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Unrelated accepted value",
            Outputs = [new("number", new() { Kind = "number", Number = 3 })] });
        var candidate = state.Construction.Candidate;
        candidate.Plan = PlanningConstruction.Clone(state.Plan);
        if (change == "candidate_only") candidate.Plan.Root.Tasks.Add(accepted);
        candidate.Plan.Root.Tasks.Add(consumer);
        candidate.BaseHash = PlanningConstruction.Hash(state.Plan);
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        candidate.Extension = new() { Scope = "/root", Tasks = [consumer], RequirementIds = ["req_0"] };
        candidate.TaskIds = [consumer.Id];
        PlanningConstruction.LoadEvidence(state, ["task:accepted", "task:unrelated"]);
        if (change == "stale_supplied_evidence") state.Construction.Evidence!["task:accepted"]!["objective"] = "Stale supplied original";
        var finding = Finding("/draft"); finding["evidence"] = new JsonArray("/requirements/0", "/evidence/task:accepted");
        var context = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Context;
        Attach(state, PlanningConstruction.DefectWitness(state, context, new JsonArray(finding))!);
        state.Construction.Evidence = null; state.Construction.EvidenceRequests.Clear();
        return state;
    }

    [Theory]
    [InlineData("literal", "disconnected")]
    [InlineData("direct", "established")]
    [InlineData("assembled", "established")]
    [InlineData("producer", "unknown")]
    [InlineData("other_operation", "unknown")]
    public void StructuredRequiredSourceIsCheckedOnlyAtItsExactConsumerArgument(string replacement, string expected)
    {
        var state = Ready();
        var context = Context(state);
        context["inputDeclarations"] = new JsonObject { ["inputs"] = new JsonArray(new JsonObject { ["name"] = "text",
            ["declaration"] = JsonSerializer.SerializeToNode(state.Plan!.Inputs[0], PlanningJsonContext.Default.TaskInput) }) };
        var finding = Finding("/draft/tasks/0");
        finding["requiredBinding"] = new JsonObject { ["source"] = "/inputDeclarations/inputs/0", ["consumer"] = "/draft/tasks/0/inputs/0" };
        Attach(state, PlanningConstruction.DefectWitness(state, context, new JsonArray(finding))!);
        var task = state.Construction!.Candidate!.Plan.Root.Tasks[0];
        var source = new TaskValue { Kind = "input", Source = "text" };
        task.Inputs[0] = new("text", replacement switch
        {
            "direct" => source,
            "assembled" => new() { Kind = "json", Items = [new() { Kind = "object", Members = [new("text", source)] }] },
            "producer" => new() { Kind = "output", Source = "producer", Port = "text" },
            _ => new() { Kind = "string", Text = "The text is mentioned but not read" }
        });
        if (replacement == "other_operation") task.Operation = "different@exact";
        state.Construction.Candidate.Plan.Root.Tasks.Add(new() { Id = "other", Kind = "value", Outputs = [new("read", source)] });
        Refresh(state);
        var progress = Single(state);
        Assert.Equal(expected, progress["requiredBinding"]!["dependency"]!.ToString());
        Assert.Equal("/draft/tasks/0/inputs/0", progress["requiredBinding"]!["consumerPointer"]!.ToString());
        Assert.Null(progress["resolved"]);
    }

    [Fact]
    public void UnresolvedWitnessesSurviveLaterReviewsAndValidatedResolutionRemovesOnlyItsExactId()
    {
        var state = Ready(); Reject(state);
        var firstId = Single(state)["id"]!.ToString();
        var finding = Finding("/draft/tasks/0/inputs/0/value");
        finding["explanation"] = "A second independently grounded observation.";
        Attach(state, PlanningConstruction.DefectWitness(state, Context(state), new JsonArray(finding))!);
        var progress = PlanningConstruction.DefectProgressContext(state, Context(state))!;
        Assert.Equal(2, progress.Count);
        Assert.Contains(progress, item => item!["id"]!.ToString() == firstId);
        state.Construction!.Candidate!.Reviews.Add(new JsonObject { ["response"] = new JsonObject { ["resolutions"] = new JsonArray(new JsonObject
            { ["id"] = firstId, ["status"] = "invalidated_by_evidence" }) } });
        var retainedId = Single(state)["id"]!.ToString();
        Assert.NotEqual(firstId, retainedId);
        var construction = state.Construction; var previous = construction.Candidate;
        var step = construction.Compact!.CurrentStep!;
        state.Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/draft/tasks/0", "The other finding remains advisory.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Disposition = "abandon", Reason = "Choose a different applicable strategy." });
        construction.Compact.CurrentStep = step;
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Disposition = "abandon", Reason = "Withdraw this selection before drafting." });
        construction.Compact.CurrentStep = step;
        var next = new PlanningConstructionCandidate { BaseHash = previous.BaseHash, Hash = previous.Hash, Plan = previous.Plan,
            Extension = previous.Extension, OpenScopes = previous.OpenScopes.ToList() };
        PlanningConstruction.CarryDefectWitnesses(state, null, next); construction.Candidate = next;
        Assert.Equal(3, next.Reviews.Count); // Both observations and the exact validated resolution survive.
        Assert.Equal(retainedId, Single(state)["id"]!.ToString());
    }

    [Fact]
    public void AnUnresolvedOverflowStopsRatherThanSilentlyDroppingOlderFindings()
    {
        var state = Ready();
        for (var i = 0; i < 4; i++)
        {
            var finding = Finding("/draft/tasks/0"); finding["explanation"] = "Distinct finding " + i;
            Attach(state, PlanningConstruction.DefectWitness(state, Context(state), new JsonArray(finding))!);
        }
        var error = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.DefectProgressContext(state, Context(state)));
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(error.Diagnostics).Code);
        Assert.Equal(4, state.Construction!.Candidate!.Reviews.Count);
    }

    [Fact]
    public void AFalsePriorFindingRemainsContestableAndItsObservedFactIsNotAnOutcomeRequirement()
    {
        var state = Ready();
        var context = Context(state);
        context["constructionStage"] = new JsonObject { ["preflight"] = new JsonObject { ["scopeOpen"] = true } };
        var finding = Finding("/draft/tasks/0");
        finding["explanation"] = "The remaining open ancestor supposedly makes this operation unavailable.";
        finding["fact"] = new JsonObject { ["pointer"] = "/constructionStage/preflight/scopeOpen", ["equals"] = true };
        Attach(state, PlanningConstruction.DefectWitness(state, context, new JsonArray(finding))!);
        var progress = Single(state);
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.True(progress["factInPriorContext"]!["equals"]!.GetValue<bool>());
        Assert.Null(progress["requiredFact"]);
        Assert.Empty(state.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementAndReselectionCarryTheExactWitnessThroughSavedState(bool reselected)
    {
        var state = Ready();
        state.Construction!.Candidate!.AuthorizedPaths = reselected ? ["/tasks/consumer/inputs/text"] : null;
        Reject(state);
        var previous = state.Construction!.Candidate!;
        var witness = previous.Reviews[0]["defectWitness"]!.ToJsonString();
        var authority = previous.Reviews[0]["defectWitness"]!["editAuthority"]!.DeepClone();
        Assert.Equal(reselected ? "explicit_revision" : "tentative_fragment", authority["value"]!["basis"]!.ToString());
        Assert.Equal("/root", authority["value"]!["scope"]!.ToString());
        Assert.Equal("consumer", Assert.Single(authority["value"]!["taskIds"]!.AsArray())!.ToString());
        if (reselected) Assert.Equal("/tasks/consumer/inputs/text", Assert.Single(authority["value"]!["authorizedPaths"]!.AsArray())!.ToString());
        else Assert.Null(authority["value"]!["authorizedPaths"]);
        Assert.False(authority["omitted"]!.GetValue<bool>());
        if (reselected)
        {
            var compact = state.Construction.Compact!;
            compact.ReplacementScope = compact.CurrentStep!.Scope;
            compact.ReplacementRequirements = compact.CurrentStep.RequirementIds.ToList();
            compact.RejectedSteps.Add(new JsonObject
            {
                ["step"] = JsonSerializer.SerializeToNode(compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep),
                ["reviews"] = new JsonArray(previous.Reviews.Select(review => review.DeepClone()).ToArray())
            });
            state.Construction.Candidate = null;
        }
        var next = new PlanningConstructionCandidate { BaseHash = previous.BaseHash, Hash = previous.Hash, Plan = previous.Plan,
            Extension = previous.Extension, OpenScopes = previous.OpenScopes.ToList() };
        PlanningConstruction.CarryDefectWitnesses(state, reselected ? null : previous, next);
        PlanningConstruction.CarryDefectWitnesses(state, reselected ? null : previous, next);
        state.Construction.Candidate = next;
        state = PlannerFixture.Clone(state);
        Assert.Equal(witness, Assert.Single(state.Construction!.Candidate!.Reviews)["defectWitness"]!.ToJsonString());
        var progress = Single(state);
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.True(JsonNode.DeepEquals(authority, progress["priorEditAuthority"]));
        Assert.Null(state.Construction.Candidate.AuthorizedPaths); // Historical evidence grants no new permission.
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("same", "unchanged")]
    [InlineData("source", "basis_changed")]
    [InlineData("contract", "basis_changed")]
    [InlineData("baseline", "basis_changed")]
    [InlineData("claims", "claims_changed")]
    [InlineData("identity", "unverified")]
    public void CandidateFreeSuspensionPreservesOnlyItsReplacementChainInTheActualReview(string change, string expected)
    {
        var state = CoordinatorReady(); var construction = state.Construction!;
        var originalStep = JsonSerializer.Serialize(construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var extension = construction.Candidate!.Extension;
        var context = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Context;
        Attach(state, PlanningConstruction.DefectWitness(state, context, new JsonArray(Finding("/draft/tasks/0")))!);
        var originalWitness = construction.Candidate.Reviews.Single()["defectWitness"]!.ToJsonString();
        state.Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/draft/tasks/0", "A grounded but advisory finding.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = "Choose another applicable strategy.", Disposition = "abandon" });
        construction.Compact.CurrentStep = JsonSerializer.Deserialize(originalStep, PlanningJsonContext.Default.PlanningConstructionStep);
        construction.Compact.CurrentStep!.Id = "replacement";
        Assert.Null(construction.Candidate);
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection
        {
            Reason = "Suspend for the exact caller-text prerequisite.", Disposition = "suspend",
            Prerequisite = new() { Need = "The text needed by the consumer.", ExpectedType = new() { Kind = "string" },
                EvidenceAddresses = ["requirement:req_0", "source:text-clause"] }
        });
        Assert.Equal(originalWitness, Assert.Single(construction.Compact.RejectedSteps[1]["reviews"]!.AsArray())!["defectWitness"]!.ToJsonString());
        state = PlannerFixture.Clone(state); construction = state.Construction!;
        construction.Compact!.CurrentStep = JsonSerializer.Deserialize(originalStep, PlanningJsonContext.Default.PlanningConstructionStep);
        if (change == "source") construction.Compact.Sources.Last().Version = "changed-source";
        if (change == "contract") state.Discovery.Resolved[0].Description += " Changed exact contract.";
        if (change == "baseline") state.Plan!.Root.Tasks.Add(new() { Id = "accepted", Kind = "value", Objective = "An accepted producer.",
            Outputs = [new("number", new() { Kind = "number", Number = 1 })] });
        if (change == "claims") construction.Compact.CurrentStep!.CloseRequirementIds = ["req_0"];
        if (change == "identity") extension.Tasks[0].Id = "another-consumer";
        construction.Compact.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var next = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.StageCommitments(state, null);
        PlanningConstruction.CarryDefectWitnesses(state, null, next); construction.Candidate = next;
        state.Diagnostics.Clear();
        var (current, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        var prior = Assert.Single(current["defectProgress"]!.AsArray())!;
        Assert.Equal(expected, prior["status"]!.ToString());
        Assert.Equal(originalWitness, next.Reviews.Single()["defectWitness"]!.ToJsonString());
        Assert.Contains("\"resolutions\"", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(next.AuthorizedPaths); Assert.Empty(state.Diagnostics); Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(construction.Checkpoints);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("scope")]
    [InlineData("requirements")]
    public void EndedOrUnrelatedReplacementChainsDoNotLeakOldFindings(string boundary)
    {
        var state = Ready(); Reject(state);
        var construction = state.Construction!; var previous = construction.Candidate!;
        var step = construction.Compact!.CurrentStep!;
        state.Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/draft/tasks/0", "Prior advisory finding.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = "Abandon this draft.", Disposition = "abandon" });
        construction.Compact.CurrentStep = step;
        if (boundary == "accepted") PlanningCompactConstruction.Accept(state);
        construction.Compact.CurrentStep = step;
        if (boundary == "scope") step.Scope = "/groups/other/body";
        if (boundary == "requirements") step.RequirementIds = ["different-original"];
        var next = new PlanningConstructionCandidate { BaseHash = previous.BaseHash, Hash = previous.Hash, Plan = previous.Plan,
            Extension = previous.Extension, OpenScopes = previous.OpenScopes.ToList() };
        PlanningConstruction.CarryDefectWitnesses(state, null, next);
        Assert.Empty(next.Reviews);
        Assert.Single(construction.Compact.RejectedSteps[0]["reviews"]!.AsArray());
        Assert.Null(next.AuthorizedPaths);
    }

    [Fact]
    public void LargeExactValuesAreHashBoundInsteadOfTruncatedOrCopiedIntoTheNextPrompt()
    {
        var state = Ready();
        state.Construction!.Candidate!.Plan.Root.Tasks[0].Inputs[0] = new("text", new() { Kind = "string", Text = new string('x', 50_000) });
        Refresh(state); Reject(state);
        var progress = Single(state);
        Assert.True(progress["before"]!["omitted"]!.GetValue<bool>());
        Assert.Null(progress["before"]!["value"]);
        Assert.Equal("unchanged", progress["status"]!.ToString());
        Assert.True(progress.ToJsonString().Length < 4096);
        Assert.Equal(50_000, state.Construction.Candidate.Plan.Root.Tasks[0].Inputs[0].Value.Text!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HistoricalPresentationsAndTheirSavedRequestsRemainUntouched(int? version)
    {
        var state = AcceptedTaskWitness(); state.Construction!.Compact!.PresentationVersion = version;
        var context = Context(state); var before = context.ToJsonString();
        Assert.Null(PlanningConstruction.DefectWitness(state, context, new JsonArray(Finding("/draft/tasks/0"))));
        Assert.Null(PlanningConstruction.DefectProgressContext(state, context));
        PlanningConstruction.CarryDefectWitnesses(state, state.Construction.Candidate, state.Construction.Candidate!);
        Assert.Single(state.Construction.Candidate!.Reviews);
        Assert.Equal(before, context.ToJsonString());
    }

    [Fact]
    public void InvalidOrMissingConcreteSupportCreatesNoWitness()
    {
        var state = Ready(); var context = Context(state); var finding = Finding("/draft/tasks/0");
        finding["evidence"] = new JsonArray("/requirements/0", "/step");
        finding["location"] = "/requirements/0";
        Assert.Null(PlanningConstruction.DefectWitness(state, context, new JsonArray(finding)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorRequiresRelevantConsumerProgressBeforeAnotherJudge(bool repairBinding)
    {
        var state = CoordinatorReady(); var accepted = PlanningConstruction.Hash(state.Plan);
        var reviews = 0; var corrections = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
            JsonObject response;
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                reviews++;
                var findings = new JsonArray();
                var action = new JsonObject { ["kind"] = "findings", ["findings"] = findings };
                if (reviews == 1)
                {
                    var finding = Finding("/draft/tasks/0"); finding.Remove("evidence");
                    finding["obligation"] = "/requirements/0"; finding["implementation"] = "/draft/tasks/0";
                    finding["requiredBinding"] = null;
                    findings.Add((JsonNode)finding);
                }
                else
                {
                    Assert.True(repairBinding); Assert.Equal(2, corrections);
                    var prior = Assert.Single(context["defectProgress"]!.AsArray())!;
                    Assert.Equal("changed", prior["status"]!.ToString());
                    Assert.Equal("input", context["draft"]!["tasks"]![0]!["inputs"]![0]!["value"]!["kind"]!.ToString());
                    action["resolutions"] = new JsonArray(new JsonObject { ["id"] = prior["id"]!.DeepClone(), ["status"] = "resolved",
                        ["obligation"] = "/requirements/0", ["implementation"] = "/draft/tasks/0",
                        ["explanation"] = "The exact consumer now reads the original caller text." });
                }
                response = new() { ["action"] = action, ["uncertainty"] = "Synthetic planning review; no effects executed." };
            }
            else
            {
                Assert.Equal("replan", purpose); corrections++;
                var task = PlanningConstruction.Clone(state.Construction!.Candidate!.Plan).Root.Tasks.Single();
                task.Objective = "Rephrased purpose " + corrections;
                if (repairBinding && corrections == 2) task.Inputs[0] = new("text", new() { Kind = "input", Source = "text" });
                if (PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()))
                {
                    var location = repairBinding && corrections == 2 ? "/tasks/consumer/inputs/text" : "/tasks/consumer/objective";
                    var slot = context["repair"]!["slots"]!.AsArray().Single(item => item!["location"]!.ToString() == location)!;
                    response = new() { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                    {
                        ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace",
                        ["value"] = repairBinding && corrections == 2
                            ? JsonSerializer.SerializeToNode(task.Inputs[0].Value, PlanningJsonContext.Default.TaskValue) : JsonValue.Create(task.Objective)
                    }) } };
                }
                else response = new() { ["baseHash"] = context["baseHash"]!.DeepClone(),
                    ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension { Scope = "/root",
                        RequirementIds = ["req_0"], Tasks = [task] }, PlanningJsonContext.Default.PlanningConstructionExtension) };
            }
            var wire = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject());
            return new() { Json = wire, Text = wire!.ToJsonString() };
        } };
        state = await Advance(state);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        state = await Advance(PlannerFixture.Clone(state));
        Assert.Equal("CONSTRUCTION_DEFECT_UNCHANGED", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(1, reviews); Assert.Equal(1, state.ReplanAttempts);
        Assert.Empty(state.Construction!.Checkpoints); Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        state = await Advance(PlannerFixture.Clone(state));
        Assert.Equal(2, state.ReplanAttempts);
        if (repairBinding)
        {
            Assert.Empty(state.Diagnostics); Assert.Equal(1, reviews); Assert.Empty(state.Construction!.Checkpoints);
            state = await Advance(PlannerFixture.Clone(state));
            Assert.Equal(2, reviews); Assert.Single(state.Construction!.Checkpoints);
            Assert.Equal("input", Assert.Single(state.Plan!.Root.Tasks).Inputs[0].Value.Kind);
            Assert.Null(state.ApprovedHash);
        }
        else
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
            Assert.Equal(1, reviews); Assert.Empty(state.Construction!.Checkpoints);
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        }

        Task<PlanningSession> Advance(PlanningSession current) => new HybridWorkflowPlanner().AdvanceAsync(current,
            new() { ExpectedRevision = current.Revision }, runtime, PlannerFixture.Ct);

    }

    private static PlanningSession CoordinatorReady()
    {
        var state = Ready();
        var captured = new PlanningSession { Request = new() { TenantId = "defect-progress", Prompt = "Use the original caller string text in the operation.",
            Options = new() { ["automatic_correction"] = true } } };
        PlanningSessionCreation.Capture(captured);
        state.Request = captured.Request; state.Revision = 1; state.IntentVersion = 3; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!; var compact = construction.Compact!;
        construction.Limits = captured.Construction!.Limits; construction.OpenScopes = ["/root"];
        construction.Requirements = [new() { Id = "req_0" }];
        compact.Sources.Clear(); PlanningCompactConstruction.InitializeSources(state);
        foreach (var page in compact.Sources) page.Indexed = true;
        var source = new PlanningConstructionSource { Id = "text-clause", Kind = "clause", Text = state.Request.Prompt,
            Version = PlanningGraphCompiler.Fingerprint(state.Request.Prompt), Indexed = true, InterfaceKind = "input" };
        compact.Sources.Add(source); compact.RequirementSources["req_0"] = [source.Id];
        compact.InterfaceExtractions = [new() { Id = "extract", SourceIds = [source.Id], Inputs = true, Complete = true,
            Dispositions = [new() { SourceId = source.Id, Version = source.Version, Kind = "interfaces", Inputs = ["text"],
                Reason = "Explicit caller input.", Evidence = new() { [source.Id] = source.Version } }] }];
        var operation = new PlanningCapability { Id = "operation@exact", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Server = "synthetic", Method = "consume", EffectKind = "read", Description = "Read the supplied text.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("text"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = [operation] }; state.Discovery.Resolved = [operation];
        compact.CurrentStep!.OperationIds = [operation.Id]; compact.CurrentStep.DiscoveryComplete = true;
        compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan); compact.CurrentStep.Objective = state.Request.Prompt;
        construction.Candidate = PlanningConstruction.Apply(state, construction.Candidate!.Extension);
        PlanningCompactConstruction.StageCommitments(state, null);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(construction.Candidate.Plan, state.Catalog, state.Request,
            construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        return state;
    }

    private static PlanningSession Ready()
    {
        var state = PlannerFixture.Session();
        state.Plan = new() { Inputs = [new() { Name = "text", Type = new() { Kind = "string" } }] };
        state.Requirements = new() { Inputs = state.Plan.Inputs, Outputs = [], Outcomes = [new("req_0", "Use the original caller text")] };
        state.Construction = new() { Version = 2, CandidateFailureIdentityVersion = 1,
            Compact = new() { PresentationVersion = 5, CurrentStep = new() { Id = "consumer", Kind = "operation", Scope = "/root", RequirementIds = ["req_0"] },
                Sources = [new() { Id = "source_0", Version = "version_0", Text = "Use the original caller text" }] } };
        var plan = PlanningConstruction.Clone(state.Plan);
        plan.Root.Tasks.Add(new() { Id = "consumer", Kind = "operation", Operation = "operation@exact", Objective = "Consume caller text",
            Inputs = [new("text", new() { Kind = "string", Text = "Unrelated literal" })] });
        state.Construction.Candidate = new() { BaseHash = PlanningConstruction.Hash(state.Plan), Plan = plan, OpenScopes = ["/root"] };
        Refresh(state);
        return state;
    }

    private static void Refresh(PlanningSession state)
    {
        var candidate = state.Construction!.Candidate!;
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        candidate.Extension = new() { Tasks = candidate.Plan.Root.Tasks, RequirementIds = ["req_0"] };
        candidate.TaskIds = candidate.Plan.Root.Tasks.Select(task => task.Id).ToList();
    }

    private static JsonObject Context(PlanningSession state) => new()
    {
        ["requirements"] = new JsonArray(new JsonObject { ["id"] = "req_0", ["description"] = "Use the original caller text", ["sources"] = new JsonArray() }),
        ["globalConstraints"] = new JsonArray(), ["evidence"] = state.Construction!.Evidence?.DeepClone(),
        ["draft"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(state.Construction.Candidate!.Extension, PlanningJsonContext.Default.PlanningConstructionExtension))
    };

    private static JsonObject Finding(string location) => new()
    {
        ["kind"] = "omission", ["location"] = location, ["evidence"] = new JsonArray("/requirements/0", "/draft/tasks/0"),
        ["explanation"] = "The consumer binding omits the original caller text.", ["uncertainty"] = "No runtime observation.", ["fact"] = null
    };

    private static void Reject(PlanningSession state, string location = "/draft/tasks/0") =>
        Attach(state, PlanningConstruction.DefectWitness(state, Context(state), new JsonArray(Finding(location)))!);

    private static void Attach(PlanningSession state, JsonObject witness) => state.Construction!.Candidate!.Reviews.Add(new JsonObject
        { ["purpose"] = PlanningConstruction.LocalPurpose, ["outcome"] = "revision_required", ["defectWitness"] = witness });

    private static JsonObject Single(PlanningSession state) => Assert.Single(PlanningConstruction.DefectProgressContext(state, Context(state))!)!.AsObject();
}
