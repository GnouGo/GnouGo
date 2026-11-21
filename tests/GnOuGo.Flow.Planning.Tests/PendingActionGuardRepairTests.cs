using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
using static GnOuGo.Flow.Planning.Tests.BusinessReviewProtocolTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PendingActionGuardRepairTests
{
    [Fact]
    public async Task FindingOnPendingLoopAndConsumerOffersItsExistingCollectionAndConsumerBindings()
    {
        var (state, runtime) = await Ready();
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        step.Kind = "foreach";
        step.CloseRequirementIds.Clear();
        construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [new()
            {
                Id = "measure_each", Kind = "foreach", Objective = "Measure each input.",
                Items = new() { Kind = "array", Items = [new() { Kind = "input", Source = "x" }] },
                Body = new()
                {
                    Tasks = [new()
                    {
                        Id = "measure", Kind = "transform", Mode = "interpret", Objective = "Measure the supplied number.",
                        Inputs = [new("number", new() { Kind = "item" })],
                        ResultType = new() { Kind = "object", Fields = [new() { Name = "amount", Type = new() { Kind = "number" } }] }
                    }],
                    Outputs = [new("amount", new() { Kind = "output", Source = "measure", Port = "amount" })]
                }
            }]
        });
        state = await Advance(state, runtime);
        construction = state.Construction!;
        var baseline = PlanningConstruction.Hash(state.Plan);
        Assert.True(Assert.Single(construction.Checkpoints).Fragment["businessReviewPending"]!.GetValue<bool>());
        construction.Compact!.CurrentStep = new() { Id = "claim", BlockId = construction.Compact.CurrentBlockId,
            Kind = "sequence", Scope = "/root", Objective = "Complete the measurements.", RequirementIds = ["work"],
            BaseHash = baseline, DiscoveryComplete = true };
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true });
        PlanningCompactConstruction.StageCommitments(state, null, construction.Candidate);
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["decision"] = new JsonObject {
            ["verdict"] = "needs_change", ["reason"] = "The iteration and measurement omit a required source value.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("measure_each", "measure") } } };
        state = await Advance(state, runtime);
        var paths = Assert.IsType<List<string>>(state.Construction!.Candidate!.AuthorizedPaths);
        Assert.Equal(new[] { "/tasks/measure_each/items", "/tasks/measure/inputs", "/tasks/measure/objective", "/tasks/measure/requires" }.Order(), paths.Order());
        Assert.True(HybridWorkflowPlanner.CanWithdrawRejectedClaim(state));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PendingTransformCanExposeValidationFactsBeforeItsResultIsConsumed(bool consumed, bool guarded)
    {
        var (state, runtime) = await Ready();
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        step.Kind = "transform";
        step.CloseRequirementIds.Clear();
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            CompleteBlock = false, ContinueInScope = "/root", Tasks = [new()
            {
                Id = "measure", Kind = "transform", Mode = "interpret", Objective = "Measure the supplied value.",
                Inputs = [new("number", new() { Kind = "input", Source = "x" })],
                Requires = guarded ? new() { Kind = "boolean", Boolean = true } : null,
                ResultType = new() { Kind = "object", Fields = [new() { Name = "amount", Type = new() { Kind = "number" } }] }
            }] });
        state = await Advance(state, runtime);
        construction = state.Construction!;
        Assert.True(Assert.Single(construction.Checkpoints).Fragment["businessReviewPending"]!.GetValue<bool>());
        var baseline = PlanningConstruction.Hash(state.Plan);
        construction.Compact!.CurrentStep = new() { Id = "finish", BlockId = construction.Compact.CurrentBlockId,
            Kind = "value", Scope = "/root", Objective = "Review the measured value.", RequirementIds = ["work"],
            InputTaskIds = ["measure"], BaseHash = baseline, DiscoveryComplete = true };
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            CompleteBlock = true, Tasks = [new() { Id = "receipt", Kind = "value", Objective = "Record completion.",
                Outputs = [new("result", consumed ? new() { Kind = "output", Source = "measure", Port = "amount" }
                    : new() { Kind = "present", Source = "measure" })] }] });
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["decision"] = new JsonObject {
            ["verdict"] = "needs_change", ["reason"] = "Expose the measured validation fact and check the source before accepting it.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("measure") } } };
        state = await Advance(state, runtime);
        var paths = Assert.IsType<List<string>>(state.Construction!.Candidate!.AuthorizedPaths);
        Assert.Equal(!consumed, paths.Contains("/tasks/measure/resultType"));
        Assert.Equal(!guarded, paths.Contains("/tasks/measure/requires"));
        Assert.All(paths, path => Assert.StartsWith("/tasks/measure/", path, StringComparison.Ordinal));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        if (consumed || guarded) return;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
            var edits = new JsonArray();
            Add("requires", JsonNode.Parse("""{"kind":"predicate","predicate":"greater","items":[{"kind":"input","source":"x"},{"kind":"number","number":0}]}""")!);
            Add("resultType", JsonNode.Parse("""{"kind":"object","fields":[{"name":"amount","type":{"kind":"number"}},{"name":"observed","type":{"kind":"boolean"}}]}""")!);
            return new() { Json = PlanningCorpus.Transport(new JsonObject { ["patch"] = new JsonObject { ["edits"] = edits } },
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };

            void Add(string field, JsonNode value)
            {
                var slot = Assert.Single(slots, row => row!["location"]!.ToString() == "/tasks/measure/" + field)!;
                edits.Add(new JsonObject { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = value });
            }
        };
        state = await Advance(Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.False(state.Construction!.Candidate!.BusinessReviewed);
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = Report("ok") };
        };
        state = await Advance(state, runtime);
        var measure = state.Plan!.Root.Tasks.Single(task => task.Id == "measure");
        Assert.Equal("greater", measure.Requires!.Predicate);
        Assert.Contains(measure.ResultType!.Fields, field => field.Name == "observed" && field.Type.Kind == "boolean");
        Assert.Null(state.Construction!.Candidate);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task LocalFindingCanGuardACitedConsumerWhileKeepingItsReviewedProducerFixed(bool existingGuard, bool usesProducer, bool checkpointed)
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
        if (checkpointed)
        {
            state = await Advance(state, runtime);
            con = state.Construction!;
            Assert.True(con.Checkpoints.Last().Fragment["businessReviewPending"]!.GetValue<bool>());
            con.Compact!.CurrentStep = new() { BlockId = con.Compact.CurrentBlockId, Id = "receipt", Kind = "value",
                Objective = "Return the observed record.", RequirementIds = ["later"], InputTaskIds = ["save"],
                BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
            con.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"],
                CompleteBlock = true, Tasks = [new() { Id = "receipt", Kind = "value", Objective = "Return the observation.",
                    Outputs = [new("observed", new() { Kind = "present", Source = "save" })] }] });
        }
        else
        {
            var delivery = con.Candidate.Extension;
            delivery.CompleteBlock = true;
            delivery.ContinueInScope = null;
            delivery.Tasks.Add(new() { Id = "receipt", Kind = "value", Objective = "Preserve the successful record.",
                Outputs = [new("observed", new() { Kind = "present", Source = "save" })] });
            con.Compact.CurrentStep.Kind = "sequence";
            con.Candidate = PlanningConstruction.Apply(state, delivery);
        }
        var acceptedHash = PlanningConstruction.Hash(state.Plan);
        var reviewed = PlanningConstruction.TaskNode(state.Plan!.Root.Tasks.Single(t => t.Id == "compute")).ToJsonString();
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = new JsonObject { ["decision"] = new JsonObject
            {
                ["verdict"] = "needs_change", ["reason"] = "The computed number can be negative when the recording is invoked.",
                ["obligations"] = new JsonArray("later"), ["targets"] = checkpointed ? new JsonArray("compute", "save") : new JsonArray("compute", "save", "receipt")
            } } };
        };
        state = await Advance(state, runtime);
        var paths = Assert.IsType<List<string>>(state.Construction!.Candidate!.AuthorizedPaths);
        var finding = state.Construction.Candidate.Reviews.Single(row => row["outcome"]?.ToString() == "needs_change");
        Assert.Equal("compute", Assert.Single(finding["readOnlyTargets"]!.AsArray())!.ToString());
        Assert.Equal(checkpointed ? 2 : 3, finding["defectWitness"]!["findings"]!.AsArray().Count);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        var expected = new List<string> { "/tasks/save/inputs" };
        if (!existingGuard) expected.Add("/tasks/save/requires");
        if (!checkpointed) expected.Add("/tasks/receipt/outputs/observed");
        Assert.Equal(expected.Order(), paths.Order());
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
