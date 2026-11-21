using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SameBlockTechnicalResumeTests
{
    [Fact]
    public async Task ExactAcceptedAssociationResumesInsideBlockWithoutBusinessReviewOrDischarge()
    {
        var (state, runtime) = await AcceptedPrerequisite();
        var block = JsonSerializer.Serialize(PlanningCompactConstruction.CurrentBlock(state), PlanningJsonContext.Default.PlanningConstructionBlock);
        var plan = PlanningConstruction.Hash(state.Plan);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Empty(runtime.Calls);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(block, JsonSerializer.Serialize(PlanningCompactConstruction.CurrentBlock(state), PlanningJsonContext.Default.PlanningConstructionBlock));
        Assert.Equal("resuming", Assert.Single(state.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.Equal("seed", Assert.Single(state.Construction.Compact.CurrentStep!.Inputs, input => input.Kind == "output").Source);
        Assert.Equal(state.Construction.Compact.CurrentBlockId, state.Construction.Compact.CurrentStep.BlockId);
        Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        Assert.True(state.Construction.Checkpoints[0].Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, state.Construction.Checkpoints[0]));
        Assert.Null(PlanningCompactConstruction.ReviewedContinuationProvenance(state));
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(PlannerFixture.Clone(state)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("parent")]
    [InlineData("childObjective")]
    [InlineData("childStatus")]
    [InlineData("removedSnapshot")]
    public async Task ChildImplementationHandsOffToParentInsideOneUnreviewedBusinessBlock(string? corruption)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var compact = state.Construction.Compact!;
        Suspend("The final consumer needs its intermediate result");
        var parent = compact.SuspendedSteps![0].Id;
        compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        compact.CurrentStep = new() { Id = "child", BlockId = compact.CurrentBlockId, Kind = "value", Scope = "/root",
            Objective = compact.Blocks![0].Objective, RequirementIds = ["work"], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        Suspend("The intermediate computation needs the initial numeric result");
        var child = compact.SuspendedSteps[1].Id;
        compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        compact.CurrentStep = new() { Id = "seed_cursor", BlockId = compact.CurrentBlockId, Kind = "value", Scope = "/root",
            RequirementIds = ["work"], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, Extension("seed", new() { Kind = "number", Number = 7 }, child));
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state.Diagnostics.Clear();
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Equal(child, state.Construction!.Compact!.ResumingStepId);
        state.Construction.Candidate = PlanningConstruction.Apply(state, Extension("intermediate", new() { Kind = "output", Source = "seed", Port = "number" }, parent));
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Equal("implemented", state.Construction!.Compact!.SuspendedSteps![1].Status);
        Assert.Equal("waiting", state.Construction.Compact.SuspendedSteps[0].Status);
        if (corruption is not null)
        {
            var acceptedChild = state.Construction.Compact.SuspendedSteps[1];
            if (corruption == "parent") acceptedChild.ParentId = "different";
            if (corruption == "childObjective") acceptedChild.Consumer.Objective += " Different behavior";
            if (corruption == "childStatus") acceptedChild.Status = "abandoned";
            if (corruption == "removedSnapshot") { state.Construction.Checkpoints[^1].Fragment["acceptedConsumers"] = new JsonArray(); acceptedChild.Status = "abandoned"; }
            var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
            Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(state));
            Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
            Assert.Empty(runtime.Calls);
            return;
        }
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(parent, state.Construction!.Compact!.ResumingStepId);
        Assert.Equal("resuming", state.Construction.Compact.SuspendedSteps![0].Status);
        Assert.Equal("implemented", state.Construction.Compact.SuspendedSteps[1].Status);
        Assert.Empty(runtime.Calls);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true,
            Tasks = [new() { Id = "final", Kind = "value", Objective = "Produce the final requested computation", Outputs =
                [new("result", new() { Kind = "output", Source = "intermediate", Port = "number" })] }] });
        runtime.Respond = (_, purpose) => { Assert.Equal(PlanningConstruction.LocalPurpose, purpose); return new() { Json = BusinessReviewProtocolTests.Report("ok") }; };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(runtime.Calls);
        Assert.All(state.Construction!.Compact!.SuspendedSteps!, consumer => Assert.Equal("completed", consumer.Status));

        void Suspend(string need)
        {
            state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", need)];
            PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = need, Disposition = "suspend",
                Prerequisite = new() { Need = need, ExpectedType = new() { Kind = "number" }, EvidenceAddresses = ["requirement:work"] } });
        }
        static PlanningConstructionExtension Extension(string id, TaskValue value, string consumer) => new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = id, Kind = "value", Objective = "Produce the exact requested intermediate", Outputs = [new("number", value)] }],
            ContinuationBinding = new() { ContinuationId = consumer, Binding = new() { Kind = "output", Source = id, Port = "number" } }
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementedConsumerRetainsPendingReviewAndInvalidatesOnlyAffectedWork(bool invalidate)
    {
        var (state, runtime) = await AcceptedPrerequisite();
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        state.Construction!.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "consume", Kind = "value", Objective = "Consume the exact prerequisite",
                Outputs = [new("result", new() { Kind = "output", Source = "seed", Port = "number" })] }]
        });
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var consumer = Assert.Single(state.Construction!.Compact!.SuspendedSteps!);
        Assert.Equal("implemented", consumer.Status);
        Assert.Null(state.Construction.Compact.ResumingStepId);
        Assert.Null(state.Construction.Compact.CurrentStep);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.Empty(runtime.Calls);
        if (invalidate)
        {
            var independent = JsonSerializer.Serialize(state.Plan!.Root.Tasks[1], PlanningJsonContext.Default.PlanTask);
            state.Plan.Root.Tasks[0].Outputs[0] = new("number", new() { Kind = "number", Number = 99 });
            PlanningCompactConstruction.ReconcileContinuations(state);
            Assert.Equal("invalidated", consumer.Status);
            Assert.Equal(independent, JsonSerializer.Serialize(state.Plan.Root.Tasks[1], PlanningJsonContext.Default.PlanTask));
            Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
            return;
        }
        state.Construction.Compact.CurrentStep = new() { BlockId = state.Construction.Compact.CurrentBlockId, Id = "finish", Kind = "value",
            Scope = "/root", RequirementIds = ["work"], CloseRequirementIds = ["work"], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true });
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("completed", Assert.Single(state.Construction!.Compact!.SuspendedSteps!).Status);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("other_block")]
    [InlineData("changed_producer")]
    [InlineData("changed_consumer")]
    [InlineData("invalidated")]
    [InlineData("discharge")]
    [InlineData("closed_scope")]
    [InlineData("binding")]
    [InlineData("consumer_parent")]
    [InlineData("accepted_status")]
    public async Task PendingAssociationCannotBroadenAuthority(string change)
    {
        var (state, _) = await AcceptedPrerequisite();
        var checkpoint = state.Construction!.Checkpoints[0];
        var compact = state.Construction.Compact!;
        switch (change)
        {
            case "other_block": checkpoint.Step!.BlockId = "different"; break;
            case "changed_producer": state.Plan!.Root.Tasks[0].Outputs[0] = new("number", new() { Kind = "number", Number = 9 }); break;
            case "changed_consumer": compact.SuspendedSteps![0].Consumer.Objective = "Different computation"; break;
            case "invalidated": checkpoint.InvalidatedTaskIds.Add("seed"); break;
            case "discharge": checkpoint.Step!.ContinuationBinding!.DischargedContinuationIds = ["other"]; break;
            case "closed_scope": state.Construction.OpenScopes.Clear(); break;
            case "binding": checkpoint.Step!.ContinuationBinding!.Binding = new() { Kind = "present", Source = "seed" }; break;
            case "consumer_parent": compact.SuspendedSteps![0].ParentId = "other"; break;
            case "accepted_status": compact.SuspendedSteps![0].Status = "abandoned"; break;
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.False(PlanningCompactConstruction.TryResumeAcceptedBlockConsumer(state));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<(PlanningSession, TestRuntime)> AcceptedPrerequisite()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var compact = state.Construction!.Compact!;
        state.Construction.Candidate = null;
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "The exact computation needs its numeric prerequisite.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection
        {
            Reason = "Obtain the exact numeric prerequisite first", Disposition = "suspend",
            Prerequisite = new() { Need = "The exact numeric producer", ExpectedType = new() { Kind = "number" }, EvidenceAddresses = ["requirement:work"] }
        });
        compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        compact.CurrentStep = new() { BlockId = compact.CurrentBlockId, Id = "producer", Kind = "value", Scope = "/root",
            BaseHash = PlanningConstruction.Hash(state.Plan), RequirementIds = ["work"], DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "seed", Kind = "value", Objective = "Produce the exact numeric prerequisite", Outputs = [new("number", new() { Kind = "number", Number = 7 })] }],
            ContinuationBinding = new() { ContinuationId = compact.SuspendedSteps![0].Id, Binding = new() { Kind = "output", Source = "seed", Port = "number" } }
        });
        try { PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate); }
        catch (PlanningResponseException error) { Assert.Fail(string.Join("; ", error.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))); }
        state.Diagnostics.Clear();
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Empty(runtime.Calls);
        return (state, runtime);
    }
}
