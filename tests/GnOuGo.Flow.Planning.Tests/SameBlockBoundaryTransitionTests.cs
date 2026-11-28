using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SameBlockBoundaryTransitionTests
{
    [Fact]
    public void ContinuingDraftCannotDeclareDeferredExportsOrBindAWaitingConsumer()
    {
        var extension = PlanningSchemas.Object(("continueInScope", PlanningSchemas.Nullable(PlanningSchemas.String())),
            ("boundaryTransitions", PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.String()))),
            ("continuationBinding", PlanningSchemas.Nullable(PlanningSchemas.String())));
        var schema = PlanningSchemas.Object(("extension", PlanningSchemas.Nullable(extension)));
        PlanningCompactConstruction.ProjectIssuedBlockContinuation(schema);

        var cursor = new JsonObject { ["continueInScope"] = "/root" };
        var response = new JsonObject { ["extension"] = cursor };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        cursor["boundaryTransitions"] = new JsonArray();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        cursor.Remove("boundaryTransitions");
        cursor["continuationBinding"] = "producer";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, schema));

        cursor["continueInScope"] = null;
        cursor["boundaryTransitions"] = new JsonArray("result");
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("foreign_producer")]
    [InlineData("undeclared_output")]
    public async Task StandaloneExportUsesAnAcceptedResultAndKeepsTheBusinessBlockOpen(string variant)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var construction = state.Construction!;
        var compact = construction.Compact!;
        var step = compact.CurrentStep!;
        var initial = construction.Candidate!.Extension;
        var producer = Assert.Single(initial.Tasks);
        var port = Assert.Single(producer.Outputs).Name;
        state.Requirements!.Outputs = [new() { Name = "doubled", Type = new() { Kind = "number" } }];
        step.CloseRequirementIds.Clear();
        initial.CompleteBlock = false;
        construction.Candidate = PlanningConstruction.Apply(state, initial);
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        construction = state.Construction!;
        compact = construction.Compact!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        compact.CurrentStep = step;
        var source = variant == "foreign_producer" ? "outside" : producer.Id;
        var name = variant == "undeclared_output" ? "other" : "doubled";
        var export = new PlanningConstructionExtension { CompleteBlock = false, RequirementIds = ["work"],
            BoundaryTransitions = [new() { Scope = "/root", RequirementIds = ["work"], Outputs =
                [new(name, new() { Kind = "output", Source = source, Port = port })] }] };
        if (variant == "foreign_producer")
        {
            Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, export));
            return;
        }
        construction.Candidate = PlanningConstruction.Apply(state, export);
        if (variant == "undeclared_output")
        {
            Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, construction.Candidate));
            return;
        }
        PlanningCompactConstruction.ValidateExtension(state, construction.Candidate);
        for (var i = 0; i < 3; i++) state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var result = Assert.Single(state.Plan!.Root.Outputs);
        Assert.Equal(("doubled", producer.Id, port), (result.Name, result.Value.Source, result.Value.Port));
        Assert.Equal(step.BlockId, state.Construction!.Compact!.CurrentBlockId);
        Assert.DoesNotContain(state.Construction.Requirements, requirement => requirement.Status == "closed");
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task ExactAcceptedExportsCloseNestedBoundariesWithoutCompletingBusinessBlock()
    {
        var (state, runtime) = await AcceptedExports();
        var block = state.Construction!.Compact!.CurrentBlockId;
        for (var index = 0; index < 4; index++)
        {
            state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
        }
        Assert.Equal(["/root"], state.Construction!.OpenScopes);
        Assert.Equal(block, state.Construction.Compact!.CurrentBlockId);
        Assert.Empty(runtime.Calls);
        Assert.Equal(3, state.Construction.Checkpoints.Count);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        var consumer = new PlanningConstructionStep { Scope = "/root", Inputs = [new() { Kind = "output", Source = "outer", Port = "result" }] };
        Assert.Single(PlanningCompactConstruction.ImportedInterfaces(state, consumer));
    }

    [Theory]
    [InlineData("different_block")]
    [InlineData("changed_baseline")]
    [InlineData("invalidated")]
    [InlineData("waiting_consumer")]
    [InlineData("changed_claim")]
    public async Task UnreviewedBoundaryClaimCannotEscapeItsExactAuthority(string change)
    {
        var (state, _) = await AcceptedExports(waiting: change == "waiting_consumer");
        switch (change)
        {
            case "different_block": state.Construction!.Checkpoints[0].Step!.BlockId = "other"; break;
            case "changed_baseline": state.Plan!.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Unrelated accepted contribution",
                Outputs = [new("number", new() { Kind = "number", Number = 1 })] }); break;
            case "invalidated": state.Construction!.Checkpoints[0].InvalidatedTaskIds.Add("number"); break;
            case "changed_claim": state.Construction!.Checkpoints[0].Step!.BoundaryTransitions![0].CloseScope = false; break;
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(state));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<(PlanningSession, TestRuntime)> AcceptedExports(bool waiting = false)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Root.Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Retain the requested ordered work", Body = new()
            { Tasks = [new() { Id = "inner", Kind = "sequence", Objective = "Retain the requested nested computation", Body = new() }] } }];
        state.Construction.OpenScopes = ["/root", "/tasks/outer/body", "/tasks/inner/body"];
        var compact = state.Construction.Compact!;
        compact.CurrentStep!.Scope = "/tasks/inner/body"; compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        if (waiting)
        {
            state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "The waiting consumer still needs its exact input")];
            PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = "Obtain the exact input", Disposition = "suspend",
                Prerequisite = new() { Need = "The exact numeric input", ExpectedType = new() { Kind = "number" }, EvidenceAddresses = ["requirement:work"] } });
            compact.ReplacementScope = null; compact.ReplacementRequirements = null;
            compact.CurrentStep = new() { Id = "independent", BlockId = compact.CurrentBlockId, Kind = "value", Scope = "/tasks/inner/body",
                RequirementIds = ["work"], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        }
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/tasks/inner/body", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "number", Kind = "value", Objective = "Compute the exact original numeric result", Outputs = [new("result", new() { Kind = "number", Number = 7 })] }],
            BoundaryTransitions = [new() { Scope = "/tasks/inner/body", RequirementIds = ["work"], CloseScope = true,
                Outputs = [new("result", new() { Kind = "output", Source = "number", Port = "result" })] },
                new() { Scope = "/tasks/outer/body", RequirementIds = ["work"], CloseScope = true,
                Outputs = [new("result", new() { Kind = "output", Source = "inner", Port = "result" })] }] });
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state.Diagnostics.Clear();
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Empty(runtime.Calls);
        return (state, runtime);
    }
}
