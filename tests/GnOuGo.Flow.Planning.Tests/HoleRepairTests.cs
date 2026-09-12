using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;
using static GnOuGo.Flow.Planning.Tests.HoleSessionTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class HoleRepairTests
{
    [Fact]
    public void IdentityComputationRetainsOnlyTheOriginalTypedBinding()
    {
        var values = new Dictionary<string, PlanningValue> { ["source"] = new() { Kind = "input", Source = "records" }, ["unused"] = Str("Unrelated") };
        var assignment = new JsonObject { ["kind"] = "compute", ["expression"] = "source", ["bindings"] = new JsonArray("source") };
        var value = PlanningHoleAssignments.Value(assignment, values);
        Assert.Equal("input", value!.Kind); Assert.Equal("records", value.Source);
        assignment["bindings"] = new JsonArray("source", "unused");
        Assert.Equal("records", PlanningHoleAssignments.Value(assignment, values)!.Source);
    }

    [Fact]
    public void RetainedReceiptRefinesAnOpenProducerBeforeItsConsumerBindingCanCommit()
    {
        var state = Ready(); var workflow = state.Graph!.Workflows[0]; var producer = workflow.Steps[0];
        producer.Input = Obj(("message", Str("Hello"))); producer.OutputSchema = new() { CapabilityId = "open", SchemaPointer = "/output" };
        state.Preparation!.Capabilities.Add(new() { Id = "open", Resolution = "local", StepType = "set", OutputSchema = new() { ["type"] = "object", ["additionalProperties"] = true } });
        workflow.Outputs[0].Schema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
        state.Construction.Holes = state.Construction.Holes.Where(h => h.Path == "/workflows/0/outputs/0/value").ToList();
        PlanningDataflowResolver.Resolve(state);
        var hole = Assert.Single(state.Construction.Holes);
        var original = new PlanningValue { Kind = "output", Source = producer.Key };
        var binding = "p_" + PlanningBindingIdentity.Id(original)[2..14];
        var request = new PlanningHoleRequests.Request("Retained request", PlanningHoleRequests.Object(("assignments", PlanningHoleRequests.Object((hole.Id,
            PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("binding")), ("binding", PlanningHoleRequests.Enum(binding))))))),
            new Dictionary<string, PlanningValue> { [binding] = original }, new JsonObject());
        var delta = new PlanningStagedAssignments { WorkflowKey = workflow.Key, WorkflowFingerprint = PlanningHoleAssignments.WorkflowFingerprint(workflow),
            DependencyFingerprint = PlanningWorkflowConstruction.DependencyFingerprint(state, state.Construction.Workflows[0]), Targets = [hole], Bindings = request.Bindings,
            ResponseSchema = request.Schema, Payload = new() { ["assignments"] = new JsonObject { [hole.Id] = new JsonObject { ["kind"] = "binding", ["binding"] = binding } } },
            Diagnostics = [new("OUTPUT_TYPE_MISMATCH", "/workflows/0/outputs/0/schema", "Producer contract incomplete")] };
        var before = PlanningGraphCompiler.Fingerprint(state.Graph);
        Assert.True(PlanningProducerSchemaPropagation.Stage(state, delta));
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph));
        var target = Assert.Single(PlanningExactPatches.Scope(delta.Payload, delta.ResponseSchema, delta.Diagnostics));
        Assert.EndsWith("/properties", target.Path);
        Assert.True(PlanningProducerSchemaPropagation.Resolve(state, delta, out var result));
        Assert.Equal(binding, delta.Payload["assignments"]![hole.Id]!["binding"]!.ToString());
        Assert.Equal("message", Assert.Single(result!.Workflows[0].Steps[0].OutputSchema!.Properties).Name);
        Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public void NestedLiteralRepairHasOneExactFieldAndCannotRewriteItsNeighbor()
    {
        var payload = JsonNode.Parse("""{"assignments":{"h":{"kind":"literal","value":{"kind":"object","members":[{"name":"keep","value":{"kind":"string","text":"unchanged"}},{"name":"fix","value":{"kind":"string","text":"bad"}}]}}}}""")!.AsObject();
        var schema = PlanningHoleRequests.Object(("assignments", PlanningHoleRequests.Object(("h", PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("literal")), ("value", new JsonObject { ["$ref"] = "#/$defs/value" }))))));
        schema["$defs"] = PlanningSchemas.ValueDefinitions();
        const string path = "/assignments/h/value/members/1/value/text";
        var targets = PlanningExactPatches.Scope(payload, schema, [new("INVALID", path, "Correct the text")]);
        var target = Assert.Single(targets); Assert.Equal(path, target.Path);
        var result = PlanningExactPatches.Apply(payload, new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target.Id, ["value"] = "fixed" }) }, targets, PlanningExactPatches.Schema(targets, schema));
        Assert.Equal("unchanged", result["assignments"]!["h"]!["value"]!["members"]![0]!["value"]!["text"]!.ToString());
        Assert.Equal("fixed", PlanningFieldPaths.Read(result, path)!.ToString());
    }

    [Fact]
    public void StructuralRemovalsUseOriginalNumericCoordinates()
    {
        var payload = new JsonObject { ["items"] = new JsonArray(Enumerable.Range(0, 12).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };
        var targets = new List<PlanningExactPatches.Target> { new("two", "/items/2", PlanningHoleRequests.Type("null"), Remove: true), new("ten", "/items/10", PlanningHoleRequests.Type("null"), Remove: true) };
        var patches = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = "two", ["value"] = null }, new JsonObject { ["target"] = "ten", ["value"] = null }) };
        var result = PlanningExactPatches.Apply(payload, patches, targets, PlanningExactPatches.Schema(targets, new()));
        Assert.Equal(Enumerable.Range(0, 12).Except([2, 10]), result["items"]!.AsArray().Select(i => i!.GetValue<int>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLiteralRemainsStagedAndAnExactPatchPreservesItsNeighborsAcrossRestart(bool restartAfterReservation)
    {
        var state = Ready(); var runtime = new FakeRuntime();
        var schema = new PlanningSchema { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = new() { Type = "string" } }, new() { Name = "retained", Required = true, Schema = new() { Type = "string" } }] };
        state.Graph!.Workflows[0].Steps[0].OutputSchema = schema;
        state.Graph.Workflows[0].Outputs[0].Schema = schema;
        foreach (var hole in state.Construction.Holes.Where(h => h.Kind == "schema")) hole.Resolved = true;
        string? before = null;
        var invalid = new FakeRuntime { OnCheckpoint = checkpoint =>
        { if (checkpoint.Construction.PendingCalls.Count > 0) before = PlanningGraphCompiler.Fingerprint(checkpoint.Graph!); return Task.CompletedTask; } };
        invalid.OnCall = (_, request, _) =>
        {
            var executable = FakeRuntime.ExecutableWorkflow();
            executable.Steps[0].Input = Obj(("message", Str("Hello")), ("retained", Str("untouched")));
            var assignments = invalid.FillHoles(request, new() { Workflows = [executable] });
            Assert.Equal(2, assignments["assignments"]!.AsObject().Count);
            var id = assignments["assignments"]!.AsObject().Single(p => p.Value?["json"]?.ToString() == "Hello").Key;
            assignments["assignments"]![id] = new JsonObject { ["kind"] = "literal", ["json"] = 7 };
            return Task.FromResult(new LLMResponse { Json = assignments });
        };
        state = await Advance(state, invalid);
        Assert.Equal(PlanningPhase.Repair, state.CurrentPhase);
        var staged = Assert.Single(state.Construction.Candidates);
        Assert.Contains(staged.Payload["assignments"]!.AsObject(), p => p.Value?["json"]?.ToString() == "untouched");
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Contains(staged.Diagnostics, d => d.Location.StartsWith("/assignments/", StringComparison.Ordinal));
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        if (restartAfterReservation)
        {
            PlanningSnapshot? reserved = null;
            var interrupted = new FakeRuntime { OnCheckpoint = snapshot =>
            {
                if (snapshot.Construction.PendingCalls.Count > 0)
                { reserved = PlanningContext.Clone(snapshot); throw new IOException("Restart before dispatch"); }
                return Task.CompletedTask;
            } };
            await Assert.ThrowsAsync<IOException>(() => new PlanningHoleRepair().AdvanceAsync(state, interrupted, TestContext.Current.CancellationToken));
            Assert.Empty(interrupted.Requests); Assert.NotNull(reserved); state = reserved;
        }
        var repair = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal(PlanningPhase.Repair, phase);
            var target = Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject()).Key;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [target] = "Hello" } });
        } };
        state = await Advance(state, repair);
        Assert.Empty(state.Construction.Candidates);
        Assert.Equal("untouched", state.Graph!.Workflows[0].Steps[0].Input.Members.Single(m => m.Name == "retained").Value.Text);
        Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
        Assert.Equal(PlanningGates.Response, state.RepairAllowances[0].Gate);
        for (var i = 0; i < 10 && !PlanningStatus.IsWaiting(state.Status); i++) state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(invalid.Requests);
    }

    [Fact]
    public void ExactPatchRejectsDuplicatesAndOverlappingTargets()
    {
        var payload = JsonNode.Parse("""{"a":{"x":"old"}}""")!.AsObject();
        var targets = new List<PlanningExactPatches.Target> { new("x", "/a/x", PlanningHoleRequests.Type("string")), new("a", "/a", PlanningHoleRequests.Object(("x", PlanningHoleRequests.Type("string")))) };
        var schema = PlanningExactPatches.Schema(targets, new());
        foreach (var second in new[] { "x", "a" })
        {
            var patches = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = "x", ["value"] = "new" },
                new JsonObject { ["target"] = second, ["value"] = second == "x" ? JsonValue.Create("other") : new JsonObject { ["x"] = "other" } }) };
            Assert.Throws<InvalidOperationException>(() => PlanningExactPatches.Apply(payload, patches, targets, schema));
            Assert.Equal("old", payload["a"]!["x"]!.ToString());
        }
    }
}
