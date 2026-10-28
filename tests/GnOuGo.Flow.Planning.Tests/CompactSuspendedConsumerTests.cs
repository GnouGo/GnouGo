using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSuspendedConsumerTests
{
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
        foreach (var source in compact.Sources) { source.Indexed = true; source.InterfaceKind = "none"; }
        compact.InterfaceExtractions = [];
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
