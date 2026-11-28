using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TypedRepairSchemaTests
{
    private const string Arithmetic = """{"kind":"arithmetic","text":"add","items":[{"kind":"number","number":1},{"kind":"number","number":2}]}""";
    private const string Reference = """{"kind":"output","source":"undeclared","port":"value"}""";

    private static PlanningSession Output(TaskType? type, string name = "result")
    {
        var state = PlannerFixture.Session(); state.Request.SessionId = "typed-repair";
        state.Requirements = PlannerFixture.Requirements();
        state.Requirements.Outputs = type is null ? null : [new() { Name = name, Type = type }];
        state.Catalog = new() { AllowedStepTypes = ["set"] };
        state.Plan = new() { Root = new() { Outputs = [new(name, new() { Kind = "string", Text = "original" })] } };
        state.RevisionScope = ["/root/outputs/" + name];
        return state;
    }

    private static JsonObject Schema(PlanningSession state) =>
        PlanningRepairPatch.Schema(state, PlanningRepairPatch.Template(state));

    private static JsonObject Response(string value) => new()
    {
        ["discoveryRequests"] = null, ["clarifications"] = null,
        ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["slot"] = "s0", ["action"] = "replace", ["value"] = JsonNode.Parse(value) }) }
    };

    [Theory]
    [InlineData("string", false)]
    [InlineData("boolean", false)]
    [InlineData("number", true)]
    [InlineData("integer", true)]
    public void AcceptedOutputTypeConstrainsArithmeticWithoutGuessingReferenceTypes(string kind, bool arithmetic)
    {
        var schema = Schema(Output(new() { Kind = kind }));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Equal(arithmetic, PlanningContractValidation.ValidateInstance(Response(Arithmetic), schema).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(Reference), schema));
    }

    [Theory]
    [InlineData("string", false, "{\"kind\":\"string\",\"text\":\"value\"}", true)]
    [InlineData("string", false, "{\"kind\":\"boolean\",\"boolean\":true}", false)]
    [InlineData("boolean", false, "{\"kind\":\"boolean\",\"boolean\":true}", true)]
    [InlineData("boolean", false, "{\"kind\":\"string\",\"text\":\"true\"}", false)]
    [InlineData("number", false, "{\"kind\":\"number\",\"number\":2}", true)]
    [InlineData("number", false, "{\"kind\":\"null\"}", false)]
    [InlineData("string", true, "{\"kind\":\"null\"}", true)]
    public void AcceptedOutputTypePreservesExactScalarAndNullabilityDomains(string kind, bool nullable, string value, bool accepted)
    {
        var schema = Schema(Output(new() { Kind = kind, Nullable = nullable }));
        Assert.Equal(accepted, PlanningContractValidation.ValidateInstance(Response(value), schema).Count == 0);
    }

    [Fact]
    public void LiteralNamesAndNestedAssemblyRetainExactContractsAndBindings()
    {
        var state = Output(new() { Kind = "object", Fields = [
            new() { Name = "résultat/a.~", Type = new() { Kind = "string" } },
            new() { Name = "rows", Type = new() { Kind = "array", Items = new() { Kind = "boolean", Nullable = true } } }
        ] }, "résultat/a.~");
        var schema = Schema(state);
        var assembly = """{"kind":"object","members":[{"name":"résultat/a.~","value":{"kind":"field","port":"déclaré/a.~","items":[{"kind":"input","source":"record"}]}},{"name":"rows","value":{"kind":"array","items":[{"kind":"null"},{"kind":"output","source":"unknown","port":"ok"},{"kind":"boolean","boolean":false}]}}]}""";
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(assembly), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(assembly.Replace("résultat/a.~", "result", StringComparison.Ordinal)), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(assembly.Replace("{\"kind\":\"boolean\",\"boolean\":false}", "{\"kind\":\"string\",\"text\":\"false\"}", StringComparison.Ordinal)), schema));
    }

    [Fact]
    public void AcceptedArrayStillSupportsLookupFlattenAndNestedReferences()
    {
        var schema = Schema(Output(new() { Kind = "array", Items = new() { Kind = "string", Nullable = true } }));
        foreach (var value in new[] {
            """{"kind":"flatten","items":[{"kind":"input","source":"pages"}]}""",
            """{"kind":"lookup","items":[{"kind":"input","source":"records"},{"kind":"input","source":"selected"}],"port":"identity"}""",
            """{"kind":"array","items":[{"kind":"null"},{"kind":"field","port":"text","items":[{"kind":"output","source":"producer","port":"record"}]}]}"""
        }) Assert.Empty(PlanningContractValidation.ValidateInstance(Response(value), schema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitOperationValueRevisionUsesOnlyInspectedExactContract(bool opaque)
    {
        var state = RepairPatchTests.State();
        state.RevisionScope = state.EditablePaths = ["/tasks/consumer/inputs/text"];
        if (opaque) state.Catalog!.Capabilities[0].InputSchema!["properties"]!["text"] = new JsonObject();
        var schema = Schema(state);
        Assert.Equal(opaque, PlanningContractValidation.ValidateInstance(Response(Arithmetic), schema).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(Reference), schema));
        var projection = """{"kind":"field","port":"literal.name/é","items":[{"kind":"output","source":"producer","port":"record"}]}""";
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(projection), schema));
    }

    [Fact]
    public void OrdinaryStringRepairCanCorrectArrayAssemblyWithoutEchoingMultilineConstants()
    {
        const string instructions = "Preserve the observed report and caller request.\n\n--- OBSERVATION ---\n";
        var state = RepairPatchTests.State();
        state.Plan!.Root.Tasks[0] = new() { Id = "producer", Kind = "value", Objective = "Retain an observed report.",
            Outputs = [new("text", new() { Kind = "string", Text = "observed report" })] };
        state.Plan.Inputs = [new() { Name = "requestText", Type = new() { Kind = "string" } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        var parts = new List<TaskValue> { new() { Kind = "string", Text = instructions },
            new() { Kind = "output", Source = "producer", Port = "text" }, new() { Kind = "input", Source = "requestText" } };
        state.Plan.Root.Tasks[1].Inputs = [new("text", new() { Kind = "json", Items = [new() { Kind = "flatten", Items = parts }] })];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "TASK_FLATTEN_INVALID");
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        Assert.Equal(["/tasks/consumer/inputs/text"], state.RevisionScope);
        var request = RepairPatchTests.Request(state);
        Assert.DoesNotContain("\\n", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var corrected = new JsonObject { ["kind"] = "json", ["items"] = new JsonArray(new JsonObject {
            ["kind"] = "array", ["items"] = new JsonArray(new JsonObject { ["kind"] = "string", ["text"] = instructions },
                JsonNode.Parse("""{"kind":"output","source":"producer","port":"text"}"""),
                JsonNode.Parse("""{"kind":"input","source":"requestText"}""")) }) };
        var edit = RepairPatchTests.Edit(state, "/tasks/consumer/inputs/text", "replace",
            corrected);
        var repaired = RepairPatchTests.Apply(state, edit);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var value = repaired.Root.Tasks[1].Inputs.Single(input => input.Name == "text").Value;
        Assert.Equal(instructions, value.Items[0].Items[0].Text);
        Assert.Equal("producer", value.Items[0].Items[1].Source);
        Assert.Equal("requestText", value.Items[0].Items[2].Source);
        Assert.Equal(JsonSerializer.Serialize(state.Plan.Root.Tasks[2], PlanningJsonContext.Default.PlanTask),
            JsonSerializer.Serialize(repaired.Root.Tasks[2], PlanningJsonContext.Default.PlanTask));
    }

    [Fact]
    public void MissingAcceptedOutputContractRetainsSemanticFallback()
    {
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(Arithmetic), Schema(Output(null))));
    }

    [Theory]
    [InlineData("objective", false)]
    [InlineData("workspace", true)]
    public void ExplicitAgentRevisionRetainsLiteralAndWorkspaceRestrictions(string input, bool outputReference)
    {
        var state = Output(null);
        state.Catalog = new() { AllowedStepTypes = ["agent.run"], Capabilities = [new() { Id = "runner", Version = "v1", StepType = "agent.run",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"objective":{"type":"string"},"workspace":{"type":"string"}},"required":["objective","workspace"],"additionalProperties":false}""")!.AsObject() }] };
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "work", Operation = "runner", Objective = "Requested work",
            Inputs = [new("objective", new() { Kind = "string", Text = "requested work" }), new("workspace", new() { Kind = "string", Text = "approved" })] }] } };
        state.RevisionScope = state.EditablePaths = ["/tasks/work/inputs/" + input];
        var schema = Schema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response("""{"kind":"string","text":"approved"}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response("""{"kind":"input","source":"dynamic"}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response("""{"kind":"json","items":[{"kind":"input","source":"dynamic"}]}"""), schema));
        Assert.Equal(outputReference, PlanningContractValidation.ValidateInstance(Response(Reference), schema).Count == 0);
    }

    [Fact]
    public void FreshOwnedBindingKeepsRemovalOnlyAndReferenceRepairsKeepExactChoices()
    {
        var state = RepairPatchTests.State(); state.Request.Options["planning_diagnostics_version"] = 1;
        state.Plan!.Root.Tasks[1].Inputs.Add(new("fixed", new() { Kind = "string", Text = "host" }));
        state.RevisionScope = ["/tasks/consumer/inputs/fixed"];
        var slots = PlanningRepairPatch.Slots(state, PlanningRepairPatch.Template(state)["$defs"]!.AsObject());
        Assert.Equal(new[] { "remove" }, Assert.Single(slots).Actions);
        state.RevisionScope = ["/tasks/consumer/inputs/text"];
        // Composed repairs share the generation grammar; the compiler, rather than
        // a second reference vocabulary, rejects inaccessible or undeclared values.
        var invalid = JsonNode.Parse("""{"kind":"field","port":"unrelated","items":[{"kind":"input","source":"record"}]}""");
        Assert.Throws<PlanningResponseException>(() => RepairPatchTests.Apply(state,
            RepairPatchTests.Edit(state, "/tasks/consumer/inputs/text", "replace", invalid)));

        state = Output(new() { Kind = "string" }); state.Request.Options["planning_diagnostics_version"] = 1;
        state.Plan!.Root.Tasks = [new() { Id = "producer", Kind = "value", Objective = "Return supplied value", Outputs = [new("text", new() { Kind = "string", Text = "value" })] }];
        state.Plan.Root.Outputs[0].Value.Kind = "output"; state.Plan.Root.Outputs[0].Value.Source = "producer";
        state.Plan.Root.Outputs[0].Value.Port = "missing"; state.Plan.Root.Outputs[0].Value.Text = null;
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        var schema = Schema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response("""{"kind":"output","source":"producer","port":"text"}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response("""{"kind":"string","text":"replacement"}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(Reference), schema));
    }


    [Fact]
    public void PendingRepairRetainsIssuedSchemaAndAuthorityAfterSerialization()
    {
        var state = Output(new() { Kind = "string" });
        var request = new LLMRequest { ClientRequestId = "pending-current-repair",
            Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = PlanningRepairPatch.CurrentVersion, ["authority"] = PlanningRepairPatch.Authority(state) } },
            StructuredOutputSchema = Schema(state) };
        state.PendingCall = new() { Id = request.ClientRequestId, Request = request, Purpose = "replan" };
        var restored = PlannerFixture.Clone(state);
        Assert.Equal(request.Prompt, restored.PendingCall!.Request.Prompt);
        Assert.Equal(request.StructuredOutputSchema.ToJsonString(), restored.PendingCall.Request.StructuredOutputSchema!.ToJsonString());
        Assert.True(PlanningRepairPatch.Verify(restored, restored.PendingCall.Request));
        var patch = Response("""{"kind":"string","text":"corrected"}""")["patch"]!.Deserialize(RepairJsonContext.Default.RepairPatch)!;
        var repaired = PlanningRepairPatch.Apply(restored, patch, restored.PendingCall.Request);
        Assert.Equal("corrected", repaired.Root.Outputs[0].Value.Text);
        Assert.Equal("original", restored.Plan!.Root.Outputs[0].Value.Text);
    }

    [Theory]
    [InlineData(true)]
    public void ReconstructedPatchUsesItsIssuedCharacterInterpretationWithoutChangingAuthority(bool unicode)
    {
        var state = Output(new() { Kind = "string" });
        state.EditablePaths = state.RevisionScope;
        var schema = Schema(state);
        // A captured wire bound constrains the edit text; it adds no business
        // contract or permission to the independently compiled output.
        Assert.True(BoundText(schema) > 0);
        if (unicode) schema["$comment"] = PlanningModelCalls.UnicodeStringLengths;
        var request = new LLMRequest { ClientRequestId = "issued-unicode-repair",
            Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = PlanningRepairPatch.CurrentVersion,
                ["authority"] = PlanningRepairPatch.Authority(state) } }, StructuredOutputSchema = schema };
        state.PendingCall = new() { Id = request.ClientRequestId, Request = request, Purpose = "replan" };
        var restored = PlannerFixture.Clone(state);
        var issued = restored.PendingCall!.Request;
        Assert.Equal(schema.ToJsonString(), issued.StructuredOutputSchema!.ToJsonString());
        Assert.True(PlanningRepairPatch.Verify(restored, issued));
        var before = JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession);
        var text = new string('a', 255) + "🌍";
        var response = Response(new JsonObject { ["kind"] = "string", ["text"] = text }.ToJsonString());
        var patch = response["patch"]!.Deserialize(RepairJsonContext.Default.RepairPatch)!;
        Assert.Equal(unicode, PlanningModelCalls.ValidateResponseFindings(response, issued.StructuredOutputSchema).Count == 0);
        if (unicode)
        {
            var repaired = PlanningRepairPatch.Apply(restored, patch, issued);
            Assert.Equal(text, repaired.Root.Outputs[0].Value.Text);
            Assert.Empty(new TaskPlanCompiler().Compile(repaired, restored.Catalog!).Diagnostics);
            var invalidReference = Response(Reference)["patch"]!.Deserialize(RepairJsonContext.Default.RepairPatch)!;
            Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(restored, invalidReference, issued));
        }
        else Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(restored, patch, issued));
        Assert.Equal(before, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        patch.Edits[0].Slot = "not-authorized";
        Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(restored, patch, issued));
        restored.Plan!.Root.Outputs[0].Value.Text = "changed baseline";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(restored, issued));

        static int BoundText(JsonNode? node)
        {
            var count = 0;
            if (node is JsonObject obj)
            {
                if (obj["properties"] is JsonObject properties && properties["kind"]?["enum"] is JsonArray kinds &&
                    kinds.Any(k => k?.ToString() == "string") && properties["text"] is JsonObject text)
                { text["maxLength"] = 256; count++; }
                foreach (var value in obj.Select(p => p.Value)) count += BoundText(value);
            }
            else if (node is JsonArray array) foreach (var value in array) count += BoundText(value);
            return count;
        }
    }
}
