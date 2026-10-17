using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairVocabularyTests
{
    [Theory]
    [InlineData("lookup")]
    [InlineData("flatten")]
    public async Task ActualIssuedRequestAcceptsOneTypedRepairAndReachesReview(string kind)
    {
        var plan = LookupCompilationTests.Plan();
        var expected = plan.Root.Tasks[0].Outputs[0].Value;
        if (kind == "flatten")
        {
            plan.Inputs[0].Type = LookupCompilationTests.ArrayOf(plan.Inputs[0].Type);
            expected = new() { Kind = "flatten", Items = [LookupCompilationTests.Input("records")] };
        }
        var invalid = JsonSerializer.SerializeToNode(expected, PlanningJsonContext.Default.TaskValue)!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
        if (kind == "lookup") invalid.Items.Reverse(); else invalid.Items[0] = LookupCompilationTests.Input("selected");
        plan.Root.Tasks[0].Outputs[0] = new("rows", invalid);
        var runtime = new TestRuntime { Proposal = new() { Plan = plan, Requirements = new()
        {
            Summary = "Return original records", Inputs = plan.Inputs, Outputs = [new() { Name = "rows", Type = kind == "flatten" ? plan.Inputs[0].Type.Items! : plan.Inputs[0].Type }],
            Outcomes = [new("rows", "Return original records in selected order")]
        } } };
        Exception? responseFailure = null;
        runtime.Respond = (request, _) =>
        {
            try
            {
                if (!PlanningRepairPatch.Issued(request.StructuredOutputSchema?.AsObject())) return TestRuntime.Response(request, runtime.Proposal);
                var context = PlanningRepairPatch.RequestContext(request)["repair"]!;
                Assert.Equal(10, context["version"]!.GetValue<int>());
                var slot = Assert.Single(context["slots"]!.AsArray());
                Assert.Equal("/tasks/resolve/outputs/rows", slot!["location"]!.ToString());
                var response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
                    ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject {
                        ["slot"] = slot["id"]!.ToString(), ["action"] = "replace",
                        ["value"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(expected, PlanningJsonContext.Default.TaskValue)) }) } };
                response = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!.AsObject();
                Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
                Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
                var baseline = runtime.Checkpoints[^1];
                Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response,
                    PlanningRepairPatch.Schema(baseline, PlanningRepairPatch.Template(baseline, version: 9), version: 9)));
                return new() { Json = response };
            }
            catch (Exception error) { responseFailure = error; throw; }
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Null(responseFailure);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join('\n', state.Diagnostics) + " calls=" + runtime.Calls.Count);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, runtime.Discoveries);
        Assert.Equal(kind, state.Plan!.Root.Tasks[0].Outputs[0].Value.Kind);
        Assert.Null(state.ApprovedHash);
        var result = await LookupCompilationTests.Execute(state.Yaml!, new() {
            ["records"] = JsonNode.Parse(kind == "flatten" ? "[[{\"key\":\"a\",\"payload\":null}],[],[{\"key\":\"a\",\"payload\":null}]]" : "[{\"key\":\"a\",\"payload\":null}]"),
            ["selected"] = new JsonArray("a", "a") }, new WorkflowEngine { HumanInputProvider = new PlanningCorpus.Human(true) });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("[{\"key\":\"a\",\"payload\":null},{\"key\":\"a\",\"payload\":null}]", result.Outputs!["rows"]!.ToJsonString());
    }

    [Theory]
    [InlineData(7, "66ec5c18ae929c1957159e7fc65ea47901d6323b94a88e89e917459b3aa5590e")]
    [InlineData(8, "0ae17ae4cef2dd453d3a693d7754b8a1d118e36658367a117314e17cd81a3c1e")]
    [InlineData(9, "784b862d1aa67e99c685192524ba7c002055f40054703b40f2a0c8d57e5d9d39")]
    public void HistoricalAuthoritiesAreStable(int version, string expectedAuthority)
    {
        var state = RepairPatchTests.State(); state.Request.SessionId = "frozen-vocabulary";
        var template = PlanningSchemas.FullProposal(state, compact: false, flatten: false, lookup: false, arrayBounds: version >= 9);
        var schema = PlanningRepairPatch.Schema(state, template, version);
        var authority = PlanningRepairPatch.Authority(state, version: version);
        // Independently collected with the unchanged 990f58c5 production implementation.
        Assert.Equal(expectedAuthority, authority);
        Assert.Equal("e347db4aa6864b7ca835a783fd14780902b6401a3408ab80085b46b4a8e6528f", PlanningGraphCompiler.Fingerprint(schema.ToJsonString()));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task CompletedRepairKeepsItsIssuedVocabularyAcrossRestart(int version)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Requirements = PlannerFixture.Requirements();
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = PlanningCorpus.Greeting();
        state.Plan.Root.Outputs.Add(new("broken", new() { Kind = "output", Source = state.Plan.Root.Tasks[0].Id, Port = "missing" }));
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        var context = PlanningRepairContext.Build(state); context["version"] = version; context["authority"] = PlanningRepairPatch.Authority(state, version: version);
        var request = new LLMRequest { ClientRequestId = "committed-repair-" + version, Prompt = "Repair\n" + new JsonObject { ["repair"] = context },
            StructuredOutputSchema = PlanningRepairPatch.Schema(state, PlanningRepairPatch.Template(state, version), version) };
        var original = request.StructuredOutputSchema.ToJsonString();
        state.PendingCall = new() { Id = request.ClientRequestId, Request = request, Purpose = "replan" };
        state.ModelCalls = 7; state.ReplanAttempts = 1;
        var payload = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null, ["patch"] = new JsonObject {
            ["edits"] = new JsonArray(new JsonObject { ["slot"] = "s0", ["action"] = "replace",
                ["value"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(state.Plan.Root.Outputs[0].Value, PlanningJsonContext.Default.TaskValue)) }) } };
        runtime.Respond = (issued, _) =>
        {
            Assert.Equal(request.ClientRequestId, issued.ClientRequestId); Assert.Equal(request.Prompt, issued.Prompt);
            Assert.Equal(original, issued.StructuredOutputSchema!.ToJsonString());
            return new() { Json = payload.DeepClone() };
        };
        var restored = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new(), runtime, PlannerFixture.Ct);
        Assert.True(restored.Status == PlanningStatus.FinalReview, string.Join('\n', restored.Diagnostics));
        Assert.Equal(7, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts); Assert.Single(runtime.Calls);
        Assert.Null(restored.PendingCall);
    }

    [Theory]
    [InlineData("encoded", "string", "output:encoded.rows")]
    [InlineData("reversed", "array", "input:selected")]
    public async Task LookupDiagnosticIdentifiesOperandTypeAndProducer(string variant, string type, string producer)
    {
        var plan = LookupCompilationTests.Plan(); var lookup = plan.Root.Tasks[0].Outputs[0].Value;
        if (variant == "reversed") lookup.Items.Reverse();
        else
        {
            plan.Root.Tasks.Insert(0, new() { Id = "encoded", Kind = "value", Objective = "Explicit text encoding",
                Outputs = [new("rows", new() { Kind = "json", Items = [LookupCompilationTests.Input("records")] })] });
            lookup.Items[0] = new() { Kind = "output", Source = "encoded", Port = "rows" };
        }
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var error = Assert.Single(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_LOOKUP_INVALID");
        Assert.Equal("/tasks/resolve/outputs/rows", error.Location);
        Assert.Contains("items[0] expected records", error.Message); Assert.Contains("received \"" + type + "\"", error.Message);
        Assert.Contains(producer, error.Message); Assert.Contains("items[1] expected IDs", error.Message);
    }

    [Theory]
    [InlineData("alpha", "records")]
    [InlineData("beta", "résultats")]
    public async Task EncodedProducerNeedsExplicitAuthorityAndIsNeverAutomaticallyDecoded(string producer, string port)
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var plan = LookupCompilationTests.Plan(); plan.Inputs[0].Type = LookupCompilationTests.ArrayOf(plan.Inputs[0].Type);
        plan.Root.Tasks.Insert(0, new() { Id = producer, Kind = "value", Objective = "Preserve supplied records",
            Outputs = [new(port, new() { Kind = "json", Items = [LookupCompilationTests.Input("records")] })] });
        plan.Root.Tasks[1].Outputs[0].Value.Items[0] = new() { Kind = "output", Source = producer, Port = port };
        state.Plan = plan; state.Diagnostics = new TaskPlanCompiler().Compile(plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        var producerPath = "/tasks/" + producer + "/outputs/" + port;
        Assert.DoesNotContain(producerPath, state.RevisionScope);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var request = new PlanningPrompt(state).Request();
        Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(state, new() { Edits = [new() {
            Slot = producerPath, Action = "replace", Value = JsonNode.Parse("""{"kind":"flatten","items":[{"kind":"input","source":"records"}]}""") }] }, request));
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));

        state.EditablePaths = [producerPath]; state.RevisionScope = [producerPath];
        request = new PlanningPrompt(state).Request();
        var repaired = PlanningRepairPatch.Apply(state, new() { Edits = [new() { Slot = "s0", Action = "replace",
            Value = JsonNode.Parse("""{"kind":"flatten","items":[{"kind":"input","source":"records"}]}""") }] }, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog).Diagnostics);
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(JsonSerializer.Serialize(plan.Root.Tasks[1], PlanningJsonContext.Default.PlanTask),
            JsonSerializer.Serialize(repaired.Root.Tasks[1], PlanningJsonContext.Default.PlanTask));
        Assert.Equal("json", plan.Root.Tasks[0].Outputs[0].Value.Kind);
        Assert.Equal("flatten", repaired.Root.Tasks[0].Outputs[0].Value.Kind);
    }
}
