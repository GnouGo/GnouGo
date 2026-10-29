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
    public async Task ExplicitResumeComposesPendingAcceptedWorkBeforeMandatoryBusinessReviews()
    {
        var run = await PendingPrerequisite();
        var state = run.State;
        var producer = state.Construction!.Checkpoints.Single(checkpoint => checkpoint.TaskIds.Contains("seed"));
        Assert.True(producer.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Empty(producer.Reviews);
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, producer));
        Assert.False(PlanningCompactConstruction.TryResumeReviewedConsumer(PlannerFixture.Clone(state)));
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
        var issued = PlannerFixture.Clone(run.Runtime.Checkpoints.Last(saved => saved.PendingCall?.Purpose == PlanningCompactConstruction.SelectPurpose));
        var exactRequest = JsonSerializer.Serialize(issued.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var recovered = await Advance(issued, run.Runtime);
        Assert.Equal(exactRequest, JsonSerializer.Serialize(run.Runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(state.ModelCalls, recovered.ModelCalls);
        Assert.Equal(state.ReplanAttempts, recovered.ReplanAttempts);
        Assert.Equal("resuming", Assert.Single(recovered.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.False(PlanningCompactConstruction.TryResumeReviewedConsumer(PlannerFixture.Clone(recovered)));

        for (var tick = 0; tick < 20 && !PlanningStatus.IsWaiting(recovered.Status) && !PlanningStatus.IsTerminal(recovered.Status); tick++)
        {
            recovered = await Advance(PlannerFixture.Clone(recovered), run.Runtime);
            Assert.Empty(recovered.Diagnostics);
        }
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status);
        Assert.Equal(1, run.LocalReviews()); Assert.Equal(1, run.GlobalReviews());
        Assert.Equal("completed", Assert.Single(recovered.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.Equal(["seed", "double"], recovered.Plan!.Root.Tasks[0].Body!.Tasks.Select(task => task.Id));
        Assert.Equal("seed", recovered.Plan.Root.Tasks[0].Body!.Tasks[1].Outputs[0].Value.Items[0].Source);
        PlanningArtifactApproval.Verify(recovered);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(recovered.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(14, result.Outputs!["number"]!.GetValue<double>());
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
        if (invalid == "producer") state.Plan!.Root.Tasks[0].Body!.Tasks[0].Objective += " Changed executable objective.";
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

    [Fact]
    public async Task ExactReviewedAssociationResumesOnceWithoutSemanticSelection()
    {
        var state = await AssociatedProducer();
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        var calls = state.ModelCalls;
        Assert.True(PlanningCompactConstruction.TryResumeReviewedConsumer(state));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal("resuming", Assert.Single(state.Construction.Compact!.SuspendedSteps!).Status);
        var binding = Assert.Single(state.Construction.Compact.CurrentStep!.Inputs);
        Assert.Equal("number_source", binding.Source); Assert.Equal("number", binding.Port);
        Assert.Single(state.Construction.Checkpoints[0].Reviews, row => row["transition"]?.ToString() == "resume_reviewed_consumer");
        Assert.False(PlanningCompactConstruction.TryResumeReviewedConsumer(PlannerFixture.Clone(state)));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("producer")]
    [InlineData("upstream")]
    [InlineData("invalidated")]
    [InlineData("binding")]
    [InlineData("review")]
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
            case "review": checkpoint.Reviews.Clear(); break;
        }
        var calls = state.ModelCalls;
        Assert.False(PlanningCompactConstruction.TryResumeReviewedConsumer(state));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Equal("waiting", Assert.Single(state.Construction.Compact.SuspendedSteps!).Status);
    }

    [Fact]
    public async Task ChangingTheAssociationInvalidatesReviewWithoutChangingExecutableBytes()
    {
        var (state, runtime) = await TentativeProducer();
        var candidate = state.Construction!.Candidate!;
        var original = PlanningConstruction.Hash(candidate.Plan);
        state = await Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        checkpoint.Step!.ContinuationBinding!.Binding = Output("seed");
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
    }

    [Fact]
    public async Task ReceivedReviewRecoveryPreservesExactRequestAndAccounting()
    {
        var (state, runtime) = await TentativeProducer();
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
        Assert.Single(recovered.Construction!.Checkpoints);
        Assert.True(PlanningCompactConstruction.TryResumeReviewedConsumer(recovered));
        Assert.False(PlanningCompactConstruction.TryResumeReviewedConsumer(PlannerFixture.Clone(recovered)));
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

    private static async Task<PlanningSession> AssociatedProducer()
    {
        var (state, runtime) = await TentativeProducer();
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Null(state.Construction!.Candidate);
        Assert.Single(runtime.Calls);
        return state;
    }

    private sealed record PendingRun(PlanningSession State, TestRuntime Runtime, Func<int> LocalReviews, Func<int> GlobalReviews);

    private static async Task<PendingRun> PendingPrerequisite(bool nullable = false)
    {
        const string work = "Inside a sequential scope, produce seven and then double that exact prerequisite.";
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
                var index = selections++;
                response = new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone() };
                if (index == 3)
                {
                    var checkpoint = current.Construction!.Checkpoints.Single(item => item.TaskIds.Contains("seed"));
                    response["resumeStep"] = new JsonObject { ["continuationId"] = Assert.Single(current.Construction.Compact!.SuspendedSteps!).Id,
                        ["producerCheckpoint"] = checkpoint.Sequence, ["binding"] = PlanningConstruction.Node(Output("seed"), PlanningJsonContext.Default.TaskValue) };
                }
                else
                {
                    Assert.InRange(index, 0, 4);
                    response["step"] = new JsonObject {
                        ["scope"] = index is 1 or 2 ? "/tasks/work/body" : "/root", ["kind"] = index == 0 ? "sequence" : "value",
                        ["objective"] = index switch { 0 => "Open the required sequential computation.", 1 => consumer, 2 => "Produce the exact prerequisite of seven.", _ => "Return the completed scope result." },
                        ["requirementIds"] = new JsonArray(index == 4 ? "req_1" : "req_0"), ["operationIds"] = new JsonArray(), ["inputs"] = index == 4
                            ? new JsonArray(PlanningConstruction.Node(Output("work"), PlanningJsonContext.Default.TaskValue)) : new JsonArray(),
                        ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(),
                        ["closeRequirementIds"] = index is 1 or 4 ? new JsonArray(index == 1 ? "req_0" : "req_1") : new JsonArray(),
                        ["outputNames"] = index == 4 ? new JsonArray("number") : new JsonArray() };
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
                        0 => new PlanningConstructionExtension { RequirementIds = ["req_0"], Tasks = [new() { Id = "work", Kind = "sequence", Objective = "Run the requested sequential computation.", Body = new() }] },
                        2 => new() { Scope = "/tasks/work/body", RequirementIds = ["req_0"], Tasks = [new() { Id = "seed", Kind = "value", Objective = "Produce the exact prerequisite of seven.",
                            Outputs = [new("number", nullable ? new() { Kind = "null" } : new() { Kind = "number", Number = 7 })] }] },
                        3 => new() { Scope = "/tasks/work/body", RequirementIds = ["req_0"], Tasks = [new() { Id = "double", Kind = "value", Objective = consumer,
                            Outputs = [new("number", new() { Kind = "arithmetic", Text = "multiply", Items = [Output("seed"), new() { Kind = "number", Number = 2 }] })] }],
                            Outputs = [new("number", Output("double"))], CloseScopes = ["/tasks/work/body"] },
                        _ => new() { RequirementIds = ["req_1"], Outputs = [new("number", Output("work"))], CloseScopes = ["/root"], Complete = true }
                    };
                    response["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
                }
            }
            else
            {
                Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(current));
                Assert.Contains("seed", request.Prompt, StringComparison.Ordinal);
                Assert.Contains("double", request.Prompt, StringComparison.Ordinal);
                if (purpose == PlanningConstruction.LocalPurpose)
                {
                    localReviews++;
                    Assert.True(current.Construction!.Checkpoints.Single(item => item.TaskIds.Contains("seed")).Fragment["businessReviewPending"]!.GetValue<bool>());
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
                return new(state, runtime, () => localReviews, () => globalReviews);
        }
        Assert.Fail("Ordinary planning did not accept the technical prerequisite checkpoint.");
        throw new InvalidOperationException();
    }

    private static async Task<(PlanningSession, TestRuntime)> TentativeProducer()
    {
        const string original = "Produce the authorized numeric prerequisite and consume it to return the requested result.";
        var state = new PlanningSession { Request = new() { TenantId = "continuations", Prompt = original,
            MaxModelCalls = 64, MaxReplanAttempts = 4, Options = new() { ["automatic_correction"] = true } } };
        PlanningSessionCreation.Capture(state);
        state.Revision = 1; state.Status = PlanningStatus.Generating;
        state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("req", original)] };
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct); state.Plan = new();
        state.Construction!.Requirements = [new() { Id = "req", Status = "in_progress" }];
        PlanningCompactConstruction.InitializeSources(state);
        var compact = state.Construction.Compact!;
        foreach (var source in compact.Sources) { source.Indexed = true; source.Inputs = []; source.Outputs = []; }
        var originalSource = compact.Sources.First(); compact.RequirementSources["req"] = [originalSource.Id];
        compact.CurrentStep = Consumer(state, "consumer");
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "This exact consumer needs its typed prerequisite.")];
        PlanningCompactConstruction.Reselect(state, Need("The exact numeric prerequisite."));
        compact.CurrentStep = Consumer(state, "producer"); compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            RequirementIds = ["req"], Tasks =
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
        Id = id, BaseHash = PlanningConstruction.Hash(state.Plan), Kind = "value", Scope = "/root",
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
