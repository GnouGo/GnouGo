using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSuspendedConsumerTests
{
    [Fact]
    public async Task PrerequisiteDiscoveryRetainsItsOwnRejectionReasonAndExactIssuedRecovery()
    {
        var (state, runtime) = await TentativeProducer();
        var construction = state.Construction!;
        var compact = construction.Compact!;
        construction.Candidate = null;
        compact.CurrentStep!.Kind = null;
        compact.CurrentStep.DiscoveryComplete = false;
        var reason = compact.RejectedSteps[0]["reason"]!.ToString();
        // Another retained rejection must not replace this consumer's evidence.
        compact.RejectedSteps.Add(new() { ["reason"] = "An unrelated action failed for a different reason." });
        var plan = PlanningConstruction.Hash(state.Plan);
        var corrections = state.ReplanAttempts;
        LLMResponse? response = null;
        string? issued = null;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.Equal(reason, context["continuation"]!["rejectionReason"]?.ToString());
            if (issued is not null)
            {
                Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                return response!;
            }
            issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            var decision = new JsonObject { ["binding"] = new JsonObject { ["kind"] = "value", ["operationIds"] = new JsonArray(), ["groupId"] = null, ["closeScopes"] = null } };
            var wire = PlanningCorpus.Transport(decision, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, request.StructuredOutputSchema));
            return response = new() { Json = wire, Text = wire.ToJsonString() };
        };
        var next = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(next.Diagnostics);
        Assert.Equal(plan, PlanningConstruction.Hash(next.Plan));
        Assert.Equal(corrections, next.ReplanAttempts);
        var retained = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.PendingCall?.Purpose == PlanningCompactConstruction.DiscoveryPurpose));
        retained.Construction!.Compact!.RejectedSteps[0]["reason"] = "Newer navigation must not rebuild the issued request.";
        var recovered = await Advance(retained, runtime);
        Assert.Empty(recovered.Diagnostics);
        Assert.Equal(next.ModelCalls, recovered.ModelCalls);
        Assert.Equal(corrections, recovered.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(recovered.Plan));
    }

    [Fact]
    public async Task ExplicitResumeComposesRetainedPendingAcceptedWorkBeforeMandatoryBusinessReviews()
    {
        var run = await PendingPrerequisite();
        var state = run.State;
        var producer = state.Construction!.Checkpoints.Single(checkpoint => checkpoint.TaskIds.Contains("seed"));
        Assert.True(producer.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Empty(producer.Reviews);
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, producer));
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(PlannerFixture.Clone(state)));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));

        var calls = state.ModelCalls;
        state = await Advance(PlannerFixture.Clone(state), run.Runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal("resuming", Assert.Single(state.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.Equal("seed", Assert.Single(state.Construction.Compact.CurrentStep!.Inputs).Source);
        Assert.Empty(producer.Reviews);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));

        // Recover the exact issued semantic decision, without selecting another
        // producer or applying its transition twice.
        var issued = PlannerFixture.Clone(run.Runtime.Checkpoints.Last(saved => saved.PendingCall?.Purpose == PlanningCompactConstruction.ImplementationPurpose));
        var exactRequest = JsonSerializer.Serialize(issued.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var recovered = await Advance(issued, run.Runtime);
        Assert.Equal(exactRequest, JsonSerializer.Serialize(run.Runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(state.ModelCalls, recovered.ModelCalls);
        Assert.Equal(state.ReplanAttempts, recovered.ReplanAttempts);
        Assert.Equal("resuming", Assert.Single(recovered.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(PlannerFixture.Clone(recovered)));

        for (var tick = 0; tick < 20 && !PlanningStatus.IsWaiting(recovered.Status) && !PlanningStatus.IsTerminal(recovered.Status); tick++)
        {
            recovered = await Advance(PlannerFixture.Clone(recovered), run.Runtime);
            Assert.Empty(recovered.Diagnostics);
        }
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status);
        Assert.Equal(1, run.LocalReviews()); Assert.Equal(1, run.GlobalReviews());
        Assert.Equal("completed", Assert.Single(recovered.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.Equal(["factor", "seed", "double"], recovered.Plan!.Root.Tasks[0].Body!.Tasks.Select(task => task.Id));
        Assert.Equal("seed", recovered.Plan.Root.Tasks[0].Body!.Tasks[2].Outputs[0].Value.Items[0].Source);
        PlanningArtifactApproval.Verify(recovered);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(recovered.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(14, result.Outputs!["number"]!.GetValue<double>());
    }

    [Fact]
    public async Task CompletedTechnicalPrerequisiteStaysPendingUntilBusinessCompositionReview()
    {
        var run = await PendingPrerequisite(retainedPending: false);
        var state = run.State;
        var producer = state.Construction!.Checkpoints.Single(checkpoint => checkpoint.TaskIds.Contains("seed"));
        Assert.True(producer.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, producer));
        Assert.Equal(0, run.LocalReviews());
        // Technical acceptance did not claim the unselected association with this consumer.
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(PlannerFixture.Clone(state)));
        for (var tick = 0; tick < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); tick++)
        {
            state = await Advance(PlannerFixture.Clone(state), run.Runtime);
            Assert.Empty(state.Diagnostics);
        }
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(1, run.LocalReviews()); Assert.Equal(1, run.GlobalReviews());
        Assert.Equal("completed", Assert.Single(state.Construction!.Compact!.SuspendedSteps!).Status);
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData("ownership")]
    [InlineData("producer")]
    [InlineData("source")]
    [InlineData("nullable")]
    public async Task PendingReviewDoesNotRelaxExplicitResumeGuards(string invalid)
    {
        var run = await PendingPrerequisite(nullable: invalid == "nullable");
        var state = run.State;
        var checkpoint = state.Construction!.Checkpoints.Single(item => item.TaskIds.Contains("seed"));
        Assert.Empty(checkpoint.Reviews);
        if (invalid == "producer") state.Plan!.Root.Tasks[0].Body!.Tasks.Single(task => task.Id == "seed").Objective += " Changed executable objective.";
        if (invalid == "source")
        {
            var source = Assert.Single(state.Construction.Compact!.SuspendedSteps!).SourceVersions.Keys.First();
            state.Construction.Compact.Sources.Single(item => item.Id == source).Text += " Changed original obligation.";
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ResumeStep(state,
            Assert.Single(state.Construction.Compact!.SuspendedSteps!).Id, invalid == "ownership" ? 1 : checkpoint.Sequence, Output("seed")));
        Assert.Contains(error.Diagnostics, finding => finding.Code == "CONSTRUCTION_CONTINUATION_INVALID");
        if (invalid == "nullable") Assert.Contains(error.Diagnostics, finding => finding.Location == "/resumeStep/binding");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("number")]
    [InlineData("null")]
    [InlineData("nested")]
    public async Task ResumeChoicesExcludeIncompatibleRootsAndKeepExplicitNestedBindings(string shape)
    {
        var run = await PendingPrerequisite(nullable: shape == "null", nested: shape == "nested");
        var state = run.State;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var checkpoint = state.Construction!.Checkpoints.Single(item => item.TaskIds.Contains("seed"));
        var page = PlanningCompactConstruction.ImplementationContext(state);
        var schema = PlanningCompactConstruction.ImplementationSchema(state, page);
        var resumeSchema = schema["properties"]!["resumeStep"]!.DeepClone().AsObject();
        resumeSchema["$defs"] = schema["$defs"]?.DeepClone();
        var response = new JsonObject { ["continuationId"] = state.Construction.Compact!.SuspendedSteps![0].Id,
            ["producerCheckpoint"] = checkpoint.Sequence, ["binding"] = new JsonObject { ["kind"] = "output", ["source"] = "seed", ["port"] = "number" } };
        Assert.Equal(shape == "number", PlanningContractValidation.ValidateInstance(response, resumeSchema).Count == 0);
        // Field navigation is a proposed path, not a claim that every member fits.
        // The displayed checkpoints must match the remaining response domains.
        var offered = Nodes(resumeSchema).OfType<JsonObject>().Where(node => node["properties"]?["producerCheckpoint"] is not null)
            .Select(node => node["properties"]!["producerCheckpoint"]!["enum"]![0]!.GetValue<int>()).Distinct().Order().ToArray();
        Assert.Equal(offered, page["continuation"]!["acceptedCheckpoints"]!.AsArray().Select(row => row!["sequence"]!.GetValue<int>()).Order());
        Assert.Equal(offered.Length > 0, page["continuation"]!["canAttemptResume"]!.GetValue<bool>());
        if (shape == "nested")
        {
            response["binding"] = new JsonObject { ["kind"] = "field", ["port"] = "value", ["items"] = new JsonArray(response["binding"]!.DeepClone()) };
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, resumeSchema));
            var clone = PlannerFixture.Clone(state);
            PlanningCompactConstruction.ResumeStep(clone, response["continuationId"]!.ToString(), checkpoint.Sequence,
                response["binding"]!.Deserialize(PlanningJsonContext.Default.TaskValue)!);
            Assert.Equal("field", Assert.Single(clone.Construction!.Compact!.CurrentStep!.Inputs).Kind);
        }
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        static IEnumerable<JsonNode> Nodes(JsonNode node)
        {
            yield return node;
            foreach (var child in node is JsonObject obj ? obj.Select(pair => pair.Value).OfType<JsonNode>() : node is JsonArray array ? array.OfType<JsonNode>() : [])
                foreach (var descendant in Nodes(child)) yield return descendant;
        }
    }

    [Fact]
    public async Task ExactAcceptedAssociationResumesOnceWithoutSemanticSelection()
    {
        var state = await AssociatedProducer();
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        var calls = state.ModelCalls;
        Assert.True(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(state));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal("resuming", Assert.Single(state.Construction.Compact!.SuspendedSteps!).Status);
        var binding = Assert.Single(state.Construction.Compact.CurrentStep!.Inputs);
        Assert.Equal("number_source", binding.Source); Assert.Equal("number", binding.Port);
        Assert.Single(state.Construction.Checkpoints[0].Reviews, row => row["transition"]?.ToString() == "resume_accepted_block_consumer");
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(PlannerFixture.Clone(state)));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("producer")]
    [InlineData("upstream")]
    [InlineData("invalidated")]
    [InlineData("binding")]
    [InlineData("claim")]
    public async Task ChangedAuthorityCannotAutomaticallyResumeAConsumer(string change)
    {
        var state = await AssociatedProducer();
        var checkpoint = state.Construction!.Checkpoints[0];
        switch (change)
        {
            case "source": state.Construction.Compact!.Sources[0].Text += " Changed business obligation."; break;
            case "producer": state.Plan!.Root.Tasks[1].Objective += " Different executable contribution."; break;
            case "upstream": state.Plan!.Root.Tasks[0].Outputs[0].Value.Number = 9; break;
            case "invalidated": checkpoint.InvalidatedTaskIds.Add("number_source"); break;
            case "binding": checkpoint.Step!.ContinuationBinding!.Binding = Output("seed"); break;
            case "claim": checkpoint.Fragment["acceptedClaimsHash"] = "changed"; break;
        }
        var calls = state.ModelCalls;
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(state));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Equal("waiting", Assert.Single(state.Construction.Compact.SuspendedSteps!).Status);
    }

    [Fact]
    public async Task ChangingTheAssociationInvalidatesMechanicalAuthorityWithoutChangingExecutableBytes()
    {
        var (state, runtime) = await TentativeProducer();
        var candidate = state.Construction!.Candidate!;
        var original = PlanningConstruction.Hash(candidate.Plan);
        state = await Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        checkpoint.Step!.ContinuationBinding!.Binding = Output("seed");
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(state));
    }

    [Fact]
    public async Task ReceivedWholeBlockReviewRecoveryPreservesExactRequestAndAccounting()
    {
        var (state, runtime) = await TentativeProducer();
        state = await Advance(state, runtime);
        state = await Advance(state, runtime);
        Assert.Equal("resuming", state.Construction!.Compact!.SuspendedSteps![0].Status);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["req"], CompleteBlock = true,
            Tasks = [new() { Id = "consume", Kind = "value", Objective = "Consume the exact accepted prerequisite",
                Outputs = [new("number", Output("number_source"))] }]
        });
        var responses = new Dictionary<string, LLMResponse>(StringComparer.Ordinal);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var response = Ok(); responses.Add(request.ClientRequestId!, response); return response;
        };
        state = await Advance(state, runtime);
        var retained = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.PendingCall is not null));
        var issued = JsonSerializer.Serialize(retained.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return responses[request.ClientRequestId!];
        };
        var recovered = await Advance(retained, runtime);
        Assert.Equal(calls, recovered.ModelCalls);
        Assert.Equal(state.ReplanAttempts, recovered.ReplanAttempts);
        Assert.Equal(2, recovered.Construction!.Checkpoints.Count);
        Assert.Equal("completed", Assert.Single(recovered.Construction.Compact!.SuspendedSteps!).Status);
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(PlannerFixture.Clone(recovered)));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("checkpoint")]
    [InlineData("future")]
    public async Task InvalidExplicitBindingLeavesAcceptedStateUntouched(string invalid)
    {
        var state = await AssociatedProducer();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var binding = invalid == "type" ? new TaskValue { Kind = "string", Text = "seven" } : Output("number_source");
        var checkpoint = invalid == "checkpoint" ? 0 : invalid == "future" ? 2 : 1;
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ResumeStep(state, "continuation_0", checkpoint, binding));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task FourTypedPrerequisitesBoundTheRetainedChain()
    {
        var (state, _) = await TentativeProducer();
        state.Construction!.Candidate = null;
        for (var index = 1; index < 4; index++)
        {
            state.Construction.Compact!.CurrentStep = Consumer(state, "consumer_" + index);
            state.Construction.Compact.ReplacementScope = null;
            state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "This exact consumer needs its typed prerequisite.")];
            PlanningCompactConstruction.Reselect(state, Need("Need " + index));
        }
        Assert.Equal(4, state.Construction.Compact!.SuspendedSteps!.Count);
        state.Construction.Compact.CurrentStep = Consumer(state, "consumer_4");
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "This exact consumer needs its typed prerequisite.")];
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.Reselect(state, Need("Need fifth")));
        Assert.Equal(4, state.Construction.Compact.SuspendedSteps.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticFifthConsumerKeepsExactPendingChildToParentAuthority(bool corruptParent)
    {
        var (state, runtime) = await TentativeProducer(diagnostic: true);
        state.Construction!.Candidate = null;
        for (var index = 1; index < 5; index++)
        {
            state.Construction.Compact!.CurrentStep = Consumer(state, "consumer_" + index);
            state.Construction.Compact.ReplacementScope = null;
            state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "This consumer needs its exact prerequisite.")];
            if (index == 4)
            {
                var schema = PlanningCompactConstruction.DraftSchema(state);
                var suspension = Assert.Single(schema["properties"]!["reselectStep"]!["anyOf"]!.AsArray(),
                    alternative => alternative?["properties"]?["disposition"]?["enum"]?[0]?.ToString() == "suspend")!;
                Assert.Null(suspension["properties"]!["prerequisite"]!["properties"]!["evidenceAddresses"]!["maxItems"]);
            }
            PlanningCompactConstruction.Reselect(state, Need("Need " + index));
        }
        var compact = state.Construction.Compact!;
        Assert.Equal(5, compact.SuspendedSteps!.Count);
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "suspended_consumers" && warning.Current == 5 && warning.Threshold == 4);
        compact.CurrentStep = Consumer(state, "seed_cursor"); compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        state.Diagnostics.Clear();
        var offered = PlanningCompactConstruction.DraftSchema(state)["properties"]!["extension"]!["anyOf"]![0]!["properties"]!["continuationBinding"]!["anyOf"]![0]!;
        Assert.Equal(5, offered["properties"]!["continuationId"]!["enum"]!.AsArray().Count);
        Assert.Null(offered["properties"]!["dischargedContinuationIds"]!["anyOf"]![0]!["maxItems"]);
        var child = compact.SuspendedSteps[^1].Id; var parent = compact.SuspendedSteps[^2].Id;
        state.Construction.Candidate = PlanningConstruction.Apply(state, Prefix("chain_seed", new() { Kind = "number", Number = 7 }, child));
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state = await Advance(state, runtime);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(child, state.Construction!.Compact!.ResumingStepId);
        Assert.Empty(runtime.Calls);
        Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        if (corruptParent) state.Construction.Compact.SuspendedSteps![^1].ParentId = "unrelated";
        var next = PlanningConstruction.Apply(state, Prefix("child_result", Output("chain_seed"), parent));
        if (corruptParent)
        {
            var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, next));
            Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_CONTINUATION_INVALID");
        }
        else
        {
            PlanningCompactConstruction.ValidateExtension(state, next);
            state.Construction.Candidate = next;
            state = await Advance(state, runtime);
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Equal(parent, state.Construction!.Compact!.ResumingStepId);
            Assert.Equal("implemented", state.Construction.Compact.SuspendedSteps![^1].Status);
            Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
            Assert.Empty(runtime.Calls);
        }

        static PlanningConstructionExtension Prefix(string id, TaskValue value, string consumer) => new()
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["req"],
            Tasks = [new() { Id = id, Kind = "value", Objective = "Produce the exact retained prerequisite", Outputs = [new("number", value)] }],
            ContinuationBinding = new() { ContinuationId = consumer, Binding = Output(id) }
        };
    }

    private static async Task<PlanningSession> AssociatedProducer()
    {
        var (state, runtime) = await TentativeProducer();
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Null(state.Construction!.Candidate);
        Assert.Empty(runtime.Calls);
        return state;
    }

    private sealed record PendingRun(PlanningSession State, TestRuntime Runtime, Func<int> LocalReviews, Func<int> GlobalReviews);

    private static async Task<PendingRun> PendingPrerequisite(bool nullable = false, bool nested = false, bool retainedPending = true)
    {
        const string work = "Inside a sequential scope, retain multiplier two, produce seven and double that exact prerequisite using the retained multiplier.";
        const string finish = "Return the required numeric public result named number from the closed scope.";
        const string consumer = "Double the exact prerequisite and complete this sequential body.";
        var selections = 0; var drafts = 0; var localReviews = 0; var globalReviews = 0;
        var runtime = new TestRuntime { Checkpointed = checkpoint => Assert.Equal("pending-consumer", checkpoint.Request.TenantId) };
        var responses = new Dictionary<string, LLMResponse>(StringComparer.Ordinal);
        runtime.Respond = (request, purpose) =>
        {
            if (responses.TryGetValue(request.ClientRequestId!, out var saved)) return saved;
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
            var current = runtime.Checkpoints.Last();
            JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
            {
                response = new() { ["clauses"] = new JsonArray(
                    new JsonObject { ["text"] = work + " ", ["kind"] = "requirement", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() },
                    new JsonObject { ["text"] = finish, ["kind"] = "requirement", ["inputs"] = new JsonArray(),
                        ["outputs"] = new JsonArray(new JsonObject { ["name"] = "number", ["required"] = true,
                            ["type"] = new JsonObject { ["kind"] = "number", ["nullable"] = false } }) }) };
            }
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var final = current.Construction!.Requirements.Single(requirement => requirement.Id == "req_0").Status == "closed";
                response = new() { ["block"] = new JsonObject
                {
                    ["objective"] = final ? finish : work, ["requirementIds"] = new JsonArray(final ? "req_1" : "req_0"),
                    ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray(final ? "The required public number" : "The exact doubled numeric result"),
                    ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray(final ? "Return the declared public result" : "Retain two and double the exact seven within the sequential scope")
                } };
            }
            else if (purpose == PlanningCompactConstruction.ImplementationPurpose)
            {
                var index = selections++;
                response = new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone() };
                if (index == 2)
                {
                    var checkpoint = current.Construction!.Checkpoints.Single(item => item.TaskIds.Contains("seed"));
                    response["resumeStep"] = new JsonObject { ["continuationId"] = Assert.Single(current.Construction.Compact!.SuspendedSteps!).Id,
                        ["producerCheckpoint"] = checkpoint.Sequence, ["binding"] = PlanningConstruction.Node(Output("seed"), PlanningJsonContext.Default.TaskValue) };
                }
                else
                {
                    Assert.InRange(index, 0, 3);
                    response["step"] = new JsonObject {
                        ["scope"] = index == 1 ? "/tasks/work/body" : "/root", ["kind"] = index == 0 ? "sequence" : "value",
                        ["objective"] = index switch { 0 => "Build the required sequential computation.", 1 => "Produce the exact prerequisite of seven.", _ => "Return the completed scope result." },
                        ["requirementIds"] = new JsonArray(index == 3 ? "req_1" : "req_0"), ["operationIds"] = new JsonArray(), ["inputs"] = index == 3
                            ? new JsonArray(PlanningConstruction.Node(Output("work"), PlanningJsonContext.Default.TaskValue)) : new JsonArray(),
                        ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(),
                        ["closeRequirementIds"] = index is 0 or 3 ? new JsonArray(index == 0 ? "req_0" : "req_1") : new JsonArray(),
                        ["outputNames"] = index == 3 ? new JsonArray("number") : new JsonArray() };
                }
            }
            else if (purpose == PlanningConstruction.DraftPurpose)
            {
                var index = drafts++;
                response = new() { ["baseHash"] = context["baseHash"]!.DeepClone() };
                if (index == 1)
                    response["reselectStep"] = new JsonObject { ["reason"] = "The consumer needs the exact prerequisite before it can double it.", ["disposition"] = "suspend",
                        ["prerequisite"] = new JsonObject { ["need"] = "The accepted numeric prerequisite.", ["expectedType"] = new JsonObject { ["kind"] = "number", ["nullable"] = false },
                            ["evidenceAddresses"] = new JsonArray(runtime.EvidenceId(request, "requirement:req_0")) } };
                else
                {
                    var extension = index switch
                    {
                        0 => new PlanningConstructionExtension { CompleteBlock = false, RequirementIds = ["req_0"], ContinueInScope = "/tasks/work/body", Tasks = [new()
                        { Id = "work", Kind = "sequence", Objective = "Run the requested sequential computation.", Body = new()
                            { Tasks = [new() { Id = "factor", Kind = "value", Objective = "Retain the requested multiplier.",
                                Outputs = [new("number", new() { Kind = "number", Number = 2 })] }] } }] },
                        2 => new() { Scope = "/tasks/work/body", RequirementIds = ["req_0"], CompleteBlock = false, Tasks = [new() { Id = "seed", Kind = "value", Objective = "Produce the exact prerequisite of seven.",
                            Outputs = [new("number", nullable ? new() { Kind = "null" } : nested
                                ? new() { Kind = "object", Members = [new("value", new() { Kind = "number", Number = 7 })] }
                                : new() { Kind = "number", Number = 7 })] }] },
                        3 => new() { CompleteBlock = true, Scope = "/tasks/work/body", RequirementIds = ["req_0"], Tasks = [new() { Id = "double", Kind = "value", Objective = consumer,
                            Outputs = [new("number", new() { Kind = "arithmetic", Text = "multiply", Items = [Output("seed"), Output("factor")] })] }],
                            Outputs = [new("number", Output("double"))], CloseScopes = ["/tasks/work/body"] },
                        _ => new() { CompleteBlock = true, RequirementIds = ["req_1"], Outputs = [new("number", Output("work"))], CloseScopes = ["/root"], Complete = true }
                    };
                    response["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
                }
            }
            else
            {
                Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(current));
                Assert.Contains(runtime.EvidenceId(request, "task:seed"), request.Prompt, StringComparison.Ordinal);
                var composition = current.Construction!.Candidate!.Plan.Root.Tasks[0].Body!.Tasks.Any(task => task.Id == "double");
                if (composition) Assert.Contains(runtime.EvidenceId(request, "task:double"), request.Prompt, StringComparison.Ordinal);
                if (purpose == PlanningConstruction.LocalPurpose)
                {
                    localReviews++;
                    if (retainedPending)
                        Assert.True(current.Construction.Checkpoints.Single(item => item.TaskIds.Contains("seed")).Fragment["businessReviewPending"]!.GetValue<bool>());
                    else if (!composition)
                        Assert.Equal("seed", Assert.Single(current.Construction.Candidate.TaskIds));
                }
                else { Assert.Equal(PlanningConstruction.GlobalPurpose, purpose); globalReviews++; Assert.Equal(1, localReviews); }
                response = new() { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The exact seven is doubled, exported by the sequential scope and returned as the numeric result." } };
            }
            var wire = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, request.StructuredOutputSchema));
            var result = new LLMResponse { Json = wire, Text = wire.ToJsonString() };
            responses.Add(request.ClientRequestId!, result); return result;
        };
        var state = PlannerFixture.Capture(new() { Request = new() { TenantId = "pending-consumer", Prompt = work + " " + finish,
            MaxModelCalls = 64, MaxReplanAttempts = 4, Options = new() { ["automatic_correction"] = true } } });
        for (var tick = 0; tick < 30 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); tick++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            if (state.Construction!.Candidate is null && state.Construction.Checkpoints.Any(checkpoint => checkpoint.TaskIds.Contains("seed")))
            {
                return new(state, runtime, () => localReviews, () => globalReviews);
            }
        }
        Assert.Fail("Ordinary planning did not accept the technical prerequisite checkpoint.");
        throw new InvalidOperationException();
    }

    private static async Task<(PlanningSession, TestRuntime)> TentativeProducer(bool diagnostic = false)
    {
        const string original = "Produce the authorized numeric prerequisite and consume it to return the requested result.";
        var state = new PlanningSession { Request = new() { TenantId = "continuations", Prompt = original,
            MaxModelCalls = 64, MaxReplanAttempts = 4, Options = new() { ["automatic_correction"] = true } } };
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        PlanningSessionCreation.Capture(state);
        state.Revision = 1; state.Status = PlanningStatus.Generating;
        state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("req", original)] };
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct); state.Plan = new();
        state.Construction!.Requirements = [new() { Id = "req", Status = "in_progress" }];
        PlanningCompactConstruction.InitializeSources(state);
        var compact = state.Construction.Compact!;
        foreach (var source in compact.Sources) { source.Indexed = true; source.Inputs = []; source.Outputs = []; }
        var originalSource = compact.Sources.First(); compact.RequirementSources["req"] = [originalSource.Id];
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = original, ["requirementIds"] = new JsonArray("req"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The requested consumer result"),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("The exact accepted prerequisite is consumed")
        });
        compact.CurrentStep = Consumer(state, "consumer");
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "This exact consumer needs its typed prerequisite.")];
        PlanningCompactConstruction.Reselect(state, Need("The exact numeric prerequisite."));
        compact.CurrentStep = Consumer(state, "producer"); compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            CompleteBlock = false, RequirementIds = ["req"], Tasks =
            [new() { Id = "seed", Kind = "value", Objective = "Produce seven.", Outputs = [new("number", new() { Kind = "number", Number = 7 })] },
             new() { Id = "number_source", Kind = "value", Objective = "Use the exact accepted seed.", Outputs = [new("number", Output("seed"))] }],
            ContinuationBinding = new() { ContinuationId = compact.SuspendedSteps![0].Id, Binding = Output("number_source") }
        });
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state.Diagnostics.Clear();
        return (state, new TestRuntime { Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose); return Ok();
        } });
    }

    private static LLMResponse Ok()
    {
        var response = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The exact prerequisite and consumer association are supported." } };
        return new() { Json = response, Text = response.ToJsonString() };
    }
    private static PlanningConstructionStep Consumer(PlanningSession state, string id) => new()
    {
        BlockId = state.Construction!.Compact!.CurrentBlockId, Id = id, BaseHash = PlanningConstruction.Hash(state.Plan), Kind = "value", Scope = "/root",
        Objective = "Produce the required " + id + ".", RequirementIds = ["req"], DiscoveryComplete = true
    };
    private static PlanningConstructionReselection Need(string text) => new()
    {
        Reason = "The selected consumer needs an exact accepted numeric producer.", Disposition = "suspend",
        Prerequisite = new() { Need = text, ExpectedType = new() { Kind = "number" }, EvidenceAddresses = ["requirement:req"] }
    };
    private static TaskValue Output(string source) => new() { Kind = "output", Source = source, Port = "number" };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
