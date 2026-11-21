using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TargetedRevisionTests
{
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static string Plan(TaskPlan? plan) => JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
    private static PlanningCommand Revise(PlanningSession state, params string[] paths) => new()
    {
        Kind = "revise", PreserveRequirements = true, ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
        Text = "Correct only the selected business bindings and guard; retain all other work.", EditablePaths = paths.ToList()
    };

    [Fact]
    public void ExtractionRevisionChangesOnlyAuthorizedDeclarations()
    {
        var original = new TaskPlan { Root = new() { Tasks = [new() { Id = "adapt", Kind = "transform", Mode = "interpret", Objective = "Expose each supplied record",
            Inputs = [new("records", new() { Kind = "array", Items = [new() { Kind = "object", Members = [new("text", Text("observed"))] }] })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "rows", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields = [new() { Name = "text", Type = new() }] } } }] } }],
            Outputs = [new("message", Text("retained"))] } };
        var corrected = PlanningConstruction.Clone(original); var task = corrected.Root.Tasks[0];
        task.Mode = "extract"; task.Each = new("records", "rows"); task.ResultType!.Fields[0].Type.MinItems = 1;
        string[] paths = ["/tasks/adapt/mode", "/tasks/adapt/each", "/tasks/adapt/resultType"];
        Assert.Empty(TaskPlanRevisions.Validate(original, corrected, paths, editablePaths: paths));
        corrected.Root.Outputs[0].Value.Text = "unrelated rewrite";
        Assert.Contains(TaskPlanRevisions.Validate(original, corrected, paths, editablePaths: paths), diagnostic => diagnostic.Code == "REVISION_SCOPE_CHANGED");
    }

    [Theory]
    [InlineData("0")][InlineData("length")]
    public void InvalidArrayAccessInImmutableRequirementStopsUnrelatedRepair(string field)
    {
        var state = PlannerFixture.Session(); state.Catalog = new(); state.Plan = PlanningCorpus.Greeting();
        var task = state.Plan.Root.Tasks[0]; task.Outputs.Add(new("rows", new() { Kind = "array", Items = [Text("observed")] }));
        state.Plan.Root.Tasks.Add(new() { Id = "consumer", Kind = "value", Objective = "Preserve the guarded business action",
            Requires = new() { Kind = "predicate", Predicate = "not_equal", Items = [new() { Kind = "field", Port = field, Items = [Output(task.Id, "rows")] }, new()] },
            Outputs = [new("unrelated", Output(task.Id, "invalid"))] });
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(state), diagnostic => diagnostic.Code == "REVISION_REQUIRED" && diagnostic.Location == "/tasks/consumer/requires");
    }

    [Theory]
    [InlineData("first_", "record")]
    [InlineData("second_", "résultat")]
    public void RetainedDroppedWriterIsRejectedWhileTheTargetedEquivalentPreservesAllWork(string prefix, string port)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TargetedRevisions", "dropped-writer.json")))!;
        Rename(fixture);
        var baseline = fixture["baseline"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var rejected = fixture["rejected"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var paths = fixture["editablePaths"]!.AsArray().Select(p => p!.ToString().Replace("/tasks/", "/tasks/" + prefix, StringComparison.Ordinal)).ToList();
        Assert.Contains(TaskPlanRevisions.Validate(baseline, rejected, paths, editablePaths: paths), d => d.Code == "REVISION_SCOPE_CHANGED");
        var corrected = JsonSerializer.SerializeToNode(baseline, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var tasks = TaskPlanRevisions.Tasks(corrected).ToDictionary(t => t.Id, StringComparer.Ordinal);
        tasks[prefix + "aggregateVisitedProducts"].Inputs = TaskPlanRevisions.Tasks(rejected).Single(t => t.Id == prefix + "aggregateVisitedProducts").Inputs;
        foreach (var id in new[] { "buildWorkbookTsv", "writeWorkbook" })
            tasks[prefix + id].Requires = new() { Kind = "predicate", Predicate = "and", Items = [tasks[prefix + id].Requires!,
                new() { Kind = "predicate", Predicate = "not", Items = [Output(prefix + "collectIfInitialAllowed", "searchCaptchaDetected")] }] };
        Assert.Empty(TaskPlanRevisions.Validate(baseline, corrected, paths, editablePaths: paths));
        Assert.Equal(baseline.Root.Tasks.Select(t => t.Id), corrected.Root.Tasks.Select(t => t.Id));
        Assert.Equal(prefix + "writeWorkbook", corrected.Root.Outputs.Single(o => o.Name == port).Value.Source);
        Assert.Equal(Plan(new() { Root = new() { Always = baseline.Root.Always } }), Plan(new() { Root = new() { Always = corrected.Root.Always } }));

        void Rename(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "id", "source", "operation" }) if (obj[key] is JsonValue value) obj[key] = prefix + value.ToString();
                foreach (var key in new[] { "name", "port" }) if (obj[key]?.ToString() == "filePath") obj[key] = port;
                if (obj["dependsOn"] is JsonArray dependencies)
                    for (var i = 0; i < dependencies.Count; i++) dependencies[i] = prefix + dependencies[i]!.ToString();
                foreach (var value in obj) Rename(value.Value);
            }
            else if (node is JsonArray array) foreach (var item in array) Rename(item);
        }
    }

    [Theory]
    [InlineData("alpha", "payload")]
    [InlineData("beta", "résultat")]
    public async Task TargetedCorrectionPreservesUnrelatedTasksAndAccountingAcrossRestart(string id, string port)
    {
        var state = PlannerFixture.Session(); state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Root = new() { Tasks = [
            new() { Id = id, Kind = "value", Objective = "Retain observed payload", Outputs = [new(port, Text("exact payload"))] },
            new() { Id = "consume", Kind = "transform", Mode = "interpret", Objective = "Interpret only compact observations", Inputs = [new("raw", Output(id, port))],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "text", Type = new() { Kind = "string" } }] } },
            new() { Id = "persist", Kind = "value", Objective = "Preserve this independent task", Outputs = [new("record", Output(id, port))] }
        ], Outputs = [new("message", Output("consume", "text"))] } };
        state.Requirements = new() { Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }] };
        state.EditablePaths = state.RevisionScope = ["/tasks/consume/inputs", "/tasks/consume/requires"];
        state.ModelCalls = 7; state.ReplanAttempts = 2;
        var before = Plan(state.Plan); var request = RepairPatchTests.Request(state); var restored = PlannerFixture.Clone(state);
        Assert.True(PlanningRepairPatch.Verify(restored, request));
        var repaired = RepairPatchTests.Apply(restored,
            RepairPatchTests.Edit(restored, "/tasks/consume/inputs", "replace", new JsonArray(new JsonObject { ["name"] = "compact", ["value"] = new JsonObject { ["kind"] = "output", ["source"] = id, ["port"] = port } })),
            RepairPatchTests.Edit(restored, "/tasks/consume/requires", "replace", JsonNode.Parse($$"""{"kind":"present","source":"{{id}}"}""")));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, restored.Catalog!).Diagnostics);
        Assert.NotEqual(before, Plan(repaired)); Assert.Equal(before, Plan(restored.Plan));
        Assert.Equal(7, restored.ModelCalls); Assert.Equal(2, restored.ReplanAttempts);
        Assert.Equal(JsonSerializer.Serialize(state.Plan.Root.Tasks[2], PlanningJsonContext.Default.PlanTask), JsonSerializer.Serialize(repaired.Root.Tasks[2], PlanningJsonContext.Default.PlanTask));
        Assert.Null(restored.ApprovedHash);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("overlap")]
    [InlineData("structure")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("stale")]
    [InlineData("intent")]
    public async Task InvalidRevisionAuthorityRejectsBeforeDispatchOrCheckpoint(string variant)
    {
        var runtime = new TestRuntime(); var state = await CurrentPlanningFixture.LiteralAsync("targeted-authority", null, null, PlannerFixture.Ct);
        var path = "/root/outputs/result"; var command = Revise(state, path);
        command.EditablePaths = variant switch
        {
            "duplicate" => [path, path], "unknown" => ["/root/outputs/absent"], "overlap" => [path, path + "/value"],
            "structure" => ["/tasks"], "empty" => [], "null" => [null!], _ => command.EditablePaths
        };
        if (variant == "stale") command.ArtifactHash = "old";
        if (variant == "intent") command.PreserveRequirements = false;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession); var count = runtime.Checkpoints.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => new HybridWorkflowPlanner().AdvanceAsync(state, command, runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls); Assert.Equal(count, runtime.Checkpoints.Count);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("objective")]
    [InlineData("guard")]
    public void UnrelatedChangeCannotReplaceScopedBaseline(string change)
    {
        var original = PlanningCorpus.Greeting(); var updated = PlanningConstruction.Clone(original);
        var task = updated.Root.Tasks[0];
        if (change == "delete") updated.Root.Tasks.Clear();
        if (change == "move") { updated.Root.Tasks.Clear(); updated.Root.Always.Add(task); }
        if (change == "objective") task.Objective = "Skip business work";
        if (change == "guard") task.Requires = new() { Kind = "boolean", Boolean = false };
        Assert.Contains(TaskPlanRevisions.Validate(original, updated, ["/root/outputs/message"]), diagnostic => diagnostic.Code == "REVISION_SCOPE_CHANGED");
    }

    [Theory]
    [InlineData("gone", "file")]
    [InlineData("deleted_action", "résultat")]
    public void MissingProducerRequiresAuthorityWithoutLiteralSubstitution(string id, string port)
    {
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Catalog = new();
        state.Plan.Root.Outputs = [new("message", Output(id, port))];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(state), diagnostic => diagnostic.Code == "REVISION_REQUIRED" && diagnostic.Location == "/root/outputs/message");
        Assert.Equal(id, state.Plan.Root.Outputs[0].Value.Source);
    }

    [Fact]
    public void BadPortRepairOffersOnlyCompatiblePortsOfTheExistingProducer()
    {
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Catalog = new();
        state.Requirements = new() { Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }] };
        state.Plan.Root.Tasks[0].Outputs.Add(new("count", new() { Kind = "number", Number = 4 }));
        var producer = state.Plan.Root.Tasks[0].Id; state.Plan.Root.Outputs[0] = new("message", Output(producer, "wrong"));
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        var slot = Assert.Single(RepairPatchTests.Slots(state));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(Text("invented"), PlanningJsonContext.Default.TaskValue), slot.ValueSchema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(Output(producer, "count"), PlanningJsonContext.Default.TaskValue), slot.ValueSchema));
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/root/outputs/message", "replace", JsonNode.Parse($$"""{"kind":"output","source":"{{producer}}","port":"message"}""")));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog).Diagnostics);
    }

    [Fact]
    public async Task CompositeReferenceRepairCannotReuseASiblingLiteralAsMissingEvidence()
    {
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Catalog = new();
        await Task.CompletedTask;
        var source = state.Plan.Root.Tasks[0].Id;
        state.Plan.Root.Outputs[0] = new("message", new() { Kind = "object", Members = [
            new("observed", Output(source, "invalid")), new("label", Text("explicit label"))] });
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = ["/root/outputs/message"];
        var before = Plan(state.Plan);
        var replacement = new TaskValue { Kind = "object", Members = [
            new("observed", Text("explicit label")), new("label", Text("explicit label"))] };
        var edit = RepairPatchTests.Edit(state, "/root/outputs/message", "replace",
            PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue)));
        Assert.Throws<PlanningResponseException>(() => RepairPatchTests.Apply(state, edit));
        Assert.Equal(before, Plan(state.Plan));
        replacement.Members[0] = new("observed", Output(source, "message"));
        var corrected = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/root/outputs/message", "replace",
            PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue))));
        Assert.Equal(source, corrected.Root.Outputs[0].Value.Members[0].Value.Source);
        Assert.Equal("explicit label", corrected.Root.Outputs[0].Value.Members[1].Value.Text);
    }

    [Fact]
    public void ExplicitBusinessLiteralRevisionRemainsAvailableAndMissingFieldsStayOmitted()
    {
        Assert.DoesNotContain("editablePaths", JsonSerializer.Serialize(new PlanningCommand(), PlanningJsonContext.Default.PlanningCommand));
        Assert.DoesNotContain("editablePaths", JsonSerializer.Serialize(PlannerFixture.Session(), PlanningJsonContext.Default.PlanningSession));
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Catalog = new(); state.Requirements = PlannerFixture.Requirements();
        state.EditablePaths = state.RevisionScope = ["/root/outputs/message"];
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/root/outputs/message", "replace", JsonNode.Parse("""{"kind":"string","text":"explicit business literal"}""")));
        Assert.Equal("explicit business literal", repaired.Root.Outputs[0].Value.Text);
        Assert.Equal("Hello", state.Plan.Root.Tasks[0].Outputs[0].Value.Text);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("paths")]
    [InlineData("baseline")]
    public async Task PendingTargetedRequestCannotBeRebased(string change)
    {
        var runtime = new TestRuntime(); var state = await CurrentPlanningFixture.LiteralAsync("pending-repair", null, null, PlannerFixture.Ct);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, Revise(state, "/root/outputs/result"), runtime, PlannerFixture.Ct);
        var request = RepairPatchTests.Request(state);
        var restored = PlannerFixture.Clone(state);
        Assert.True(PlanningRepairPatch.Verify(restored, request));
        if (change == "tenant") restored.Request.TenantId = "other";
        if (change == "paths") restored.RevisionScope.Add("/tasks/" + restored.Plan!.Root.Tasks[0].Id + "/objective");
        if (change == "baseline") restored.Plan!.Root.Tasks[0].Objective += " changed";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(restored, request));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task TargetedRevisionCannotReplenishTheCumulativeCallBudget()
    {
        var runtime = new TestRuntime(); var state = await CurrentPlanningFixture.LiteralAsync("revision-budget", null, null, PlannerFixture.Ct);
        state.Request.MaxModelCalls = state.ModelCalls;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            Revise(state, "/root/outputs/result"), runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls);
    }

}
