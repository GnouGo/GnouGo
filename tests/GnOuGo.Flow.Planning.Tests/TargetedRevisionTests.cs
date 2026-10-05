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
        var runtime = new TestRuntime();
        runtime.Proposal.Plan = new() { Root = new() { Tasks = [
            new() { Id = id, Kind = "value", Objective = "Retain observed payload", Outputs = [new(port, Text("exact payload"))] },
            new() { Id = "consume", Kind = "transform", Objective = "Interpret only compact observations", Inputs = [new("raw", Output(id, port))],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "text", Type = new() { Kind = "string" } }] } },
            new() { Id = "persist", Kind = "value", Objective = "Preserve this independent task", Outputs = [new("record", Output(id, port))] }
        ], Outputs = [new("message", Output("consume", "text"))], Always = [new() { Id = "cleanup", Kind = "value", Objective = "Release after preservation" }] } };
        var state = await PlannerFixture.RunAsync(runtime); Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var previous = Plan(state.Plan); var hash = state.ComputeArtifactHash(); var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, Revise(state, "/tasks/consume/inputs", "/tasks/consume/requires"), runtime, PlannerFixture.Ct);
        Assert.Equal(previous, Plan(state.Plan)); Assert.Equal(previous, Plan(state.Request.Baseline));
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash); Assert.Equal(1, state.ModelCalls);
        state = PlannerFixture.Clone(state);
        runtime.Proposal.Plan.Root.Tasks[1].Inputs = [new("compact", Output(id, port))];
        runtime.Proposal.Plan.Root.Tasks[1].Requires = new() { Kind = "present", Source = id };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.EditablePaths);
        Assert.NotEqual(hash, state.ComputeArtifactHash()); Assert.Null(state.ApprovedHash);
        Assert.Equal(new[] { id, "consume", "persist" }, state.Plan!.Root.Tasks.Select(t => t.Id)); Assert.Single(state.Plan.Root.Always);
        var request = runtime.Calls[1]; Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
        Assert.DoesNotContain("plan", request.StructuredOutputSchema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(8, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
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
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var path = "/root/outputs/message"; var command = Revise(state, path);
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
        Assert.Single(runtime.Calls); Assert.Equal(count, runtime.Checkpoints.Count);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("objective")]
    [InlineData("guard")]
    public async Task FullRegenerationOrUnrelatedChangeCannotReplaceScopedBaseline(string change)
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, Revise(state, "/root/outputs/message"), runtime, PlannerFixture.Ct);
        var before = Plan(state.Plan); var task = runtime.Proposal.Plan!.Root.Tasks[0];
        if (change == "delete") runtime.Proposal.Plan.Root.Tasks.Clear();
        if (change == "move") { runtime.Proposal.Plan.Root.Tasks.Clear(); runtime.Proposal.Plan.Root.Always.Add(task); }
        if (change == "objective") task.Objective = "Skip business work";
        if (change == "guard") task.Requires = new() { Kind = "boolean", Boolean = false };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(before, Plan(state.Plan)); Assert.Null(state.Yaml);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("gone", "file")]
    [InlineData("deleted_action", "résultat")]
    public async Task MissingProducerStopsWithoutRepairOrLiteralSubstitution(string id, string port)
    {
        var runtime = new TestRuntime(); runtime.Proposal.Plan!.Root.Outputs = [new("message", Output(id, port))];
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_REQUIRED" && d.Location == "/root/outputs/message");
        Assert.Equal(id, state.Plan!.Root.Outputs[0].Value.Source); Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task BadPortRepairOffersOnlyCompatiblePortsOfTheExistingProducer()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Plan!.Root.Tasks[0].Outputs.Add(new("count", new() { Kind = "number", Number = 4 }));
        var producer = runtime.Proposal.Plan.Root.Tasks[0].Id;
        runtime.Proposal.Requirements!.Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }];
        runtime.Proposal.Plan.Root.Outputs[0] = new("message", Output(producer, "wrong"));
        var planner = new HybridWorkflowPlanner(); var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        var slot = Assert.Single(RepairPatchTests.Slots(state));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(Text("invented"), PlanningJsonContext.Default.TaskValue), slot.ValueSchema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(Output(producer, "count"), PlanningJsonContext.Default.TaskValue), slot.ValueSchema));
        runtime.Proposal.Plan.Root.Outputs[0] = new("message", Output(producer, "message"));
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ReplanAttempts);
    }

    [Fact]
    public async Task CompositeReferenceRepairCannotReuseASiblingLiteralAsMissingEvidence()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var source = state.Plan!.Root.Tasks[0].Id;
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
    public async Task ExplicitBusinessLiteralRevisionRemainsAvailableAndMissingFieldsStayOmitted()
    {
        Assert.DoesNotContain("editablePaths", JsonSerializer.Serialize(new PlanningCommand(), PlanningJsonContext.Default.PlanningCommand));
        Assert.DoesNotContain("editablePaths", JsonSerializer.Serialize(PlannerFixture.Session(), PlanningJsonContext.Default.PlanningSession));
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, Revise(state, "/root/outputs/message"), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Outputs[0] = new("message", Text("explicit business literal"));
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal("explicit business literal", state.Plan!.Root.Outputs[0].Value.Text);
    }

    [Fact]
    public async Task IssuedVersionSevenRetainsItsOriginalSchemaAndAuthority()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        state.Plan!.Root.Outputs[0] = new("message", Output("absent", "result"));
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = ["/root/outputs/message"];
        var schema = PlanningRepairPatch.Schema(state, PlanningSchemas.FullProposal(state, compact: false), version: 7);
        var request = new GnOuGo.Flow.Core.Runtime.LLMRequest { StructuredOutputSchema = schema,
            Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = 7, ["authority"] = PlanningRepairPatch.Authority(state, version: 7) } }.ToJsonString() };
        var original = request.StructuredOutputSchema.ToJsonString();
        var candidate = PlanningRepairPatch.Apply(PlannerFixture.Clone(state), new() { Edits = [new() { Slot = "s0", Action = "replace",
            Value = JsonNode.Parse("""{"kind":"string","text":"historical literal"}""") }] }, request);
        Assert.Equal("historical literal", candidate.Root.Outputs[0].Value.Text);
        Assert.Equal(original, request.StructuredOutputSchema.ToJsonString());
        Assert.Contains(TaskPlanRevisions.UnrepairableRequirements(state), d => d.Code == "REVISION_REQUIRED");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("paths")]
    [InlineData("baseline")]
    public async Task PendingTargetedRequestCannotBeRebased(string change)
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, Revise(state, "/root/outputs/message"), runtime, PlannerFixture.Ct);
        var request = new PlanningPrompt(state).Request();
        var restored = PlannerFixture.Clone(state);
        Assert.True(PlanningRepairPatch.Verify(restored, request));
        if (change == "tenant") restored.Request.TenantId = "other";
        if (change == "paths") restored.EditablePaths!.Add("/tasks/greet/requires");
        if (change == "baseline") restored.Plan!.Root.Tasks[0].Objective += " changed";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(restored, request));
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task TargetedRevisionCannotReplenishTheCumulativeCallBudget()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.MaxModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state); var planner = new HybridWorkflowPlanner();
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, Revise(state, "/root/outputs/message"), runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession)); Assert.Single(runtime.Calls);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }
}
