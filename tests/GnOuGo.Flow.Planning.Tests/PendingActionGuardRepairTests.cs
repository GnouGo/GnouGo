using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
using static GnOuGo.Flow.Planning.Tests.BusinessReviewProtocolTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PendingActionGuardRepairTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task LocalFindingCanGuardAPendingConsumerWhileKeepingItsReviewedProducerFixed(bool existingGuard, bool usesProducer)
    {
        var (state, runtime) = await Ready();
        state.Catalog!.Capabilities.Add(new()
        {
            Id = "record", StepType = "mcp.call", Kind = "tool", Server = "observations", Method = "record",
            EffectKind = "write", InputSchema = JsonNode.Parse("""{"type":"object","required":["amount"],"properties":{"amount":{"type":"number"}}}""")!.AsObject(),
            OutputSchema = new() { ["type"] = "object" }
        });
        runtime.Respond = (_, _) => new() { Json = Report("ok") };
        state = await Advance(state, runtime);
        var con = state.Construction!;
        var prior = Assert.Single(con.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, prior));
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = "Record positive computed values.", ["requirementIds"] = new JsonArray("later"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("A positive amount recorded."),
            ["successCriteria"] = new JsonArray("Zero or negative amounts are rejected before recording."),
            ["dependencies"] = new JsonArray()
        });
        var value = new TaskValue { Kind = "output", Source = "compute", Port = "result" };
        con.Compact!.CurrentStep = new() { BlockId = con.Compact.CurrentBlockId, Id = "recording", Kind = "operation",
            Objective = "Record the computed amount.", RequirementIds = ["later"], InputTaskIds = ["compute"], OperationIds = ["record"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        con.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"],
            CompleteBlock = false, ContinueInScope = "/root", Tasks = [new()
            {
                Id = "save", Kind = "operation", Operation = "record", Objective = "Record the amount.",
                Inputs = [new("amount", value)], Requires = existingGuard ? new() { Kind = "present", Source = "compute" } : null
            }] });
        state = await Advance(state, runtime);
        con = state.Construction!;
        Assert.True(con.Checkpoints.Last().Fragment["businessReviewPending"]!.GetValue<bool>());
        con.Compact!.CurrentStep = new() { BlockId = con.Compact.CurrentBlockId, Id = "receipt", Kind = "value",
            Objective = "Return the observed record.", RequirementIds = ["later"], InputTaskIds = ["save"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        con.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"],
            CompleteBlock = true, Tasks = [new() { Id = "receipt", Kind = "value", Objective = "Return the observation.",
                Outputs = [new("observed", new() { Kind = "present", Source = "save" })] }] });
        var acceptedHash = PlanningConstruction.Hash(state.Plan);
        var reviewed = PlanningConstruction.TaskNode(state.Plan!.Root.Tasks.Single(t => t.Id == "compute")).ToJsonString();
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = new JsonObject { ["decision"] = new JsonObject
            {
                ["verdict"] = "needs_change", ["reason"] = "The computed number can be negative when the recording is invoked.",
                ["obligations"] = new JsonArray("later"), ["targets"] = new JsonArray("compute", "save")
            } } };
        };
        state = await Advance(state, runtime);
        var paths = Assert.IsType<List<string>>(state.Construction!.Candidate!.AuthorizedPaths);
        var finding = state.Construction.Candidate.Reviews.Single(row => row["outcome"]?.ToString() == "needs_change");
        Assert.Equal("compute", Assert.Single(finding["readOnlyTargets"]!.AsArray())!.ToString());
        Assert.Equal(2, finding["defectWitness"]!["findings"]!.AsArray().Count);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(existingGuard ? ["/tasks/save/inputs"] : new[] { "/tasks/save/inputs", "/tasks/save/requires" }, paths.Order());
        if (existingGuard) return;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
            var slot = slots.Single(s => s!["location"]!.ToString() == "/tasks/save/requires")!;
            var edit = new JsonObject { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace",
                ["value"] = usesProducer
                    ? JsonNode.Parse("""{"kind":"predicate","predicate":"greater","items":[{"kind":"output","source":"compute","port":"result"},{"kind":"number","number":0}]}""")
                    : new JsonObject { ["kind"] = "boolean", ["boolean"] = true } };
            return new() { Json = PlanningCorpus.Transport(new JsonObject { ["patch"] = new JsonObject { ["edits"] = new JsonArray(edit) } },
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        };
        state = await Advance(Clone(state), runtime);
        var repaired = state.Construction!.Candidate!;
        if (!usesProducer)
        {
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_DEFECT_UNCHANGED");
            Assert.False(repaired.BusinessReviewed);
            return;
        }
        Assert.Empty(state.Diagnostics);
        Assert.Equal("greater", repaired.Plan.Root.Tasks.Single(t => t.Id == "save").Requires!.Predicate);
        Assert.Equal(reviewed, PlanningConstruction.TaskNode(repaired.Plan.Root.Tasks.Single(t => t.Id == "compute")).ToJsonString());
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        Assert.False(repaired.BusinessReviewed);
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = Report("ok") };
        };
        state = await Advance(state, runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal("greater", state.Plan!.Root.Tasks.Single(t => t.Id == "save").Requires!.Predicate);
    }
}
