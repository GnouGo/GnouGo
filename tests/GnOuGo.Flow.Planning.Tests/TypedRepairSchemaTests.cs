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
        var state = PlannerFixture.HistoricalSession(); state.Request.SessionId = "typed-repair";
        state.Requirements = PlannerFixture.Requirements();
        state.Requirements.Outputs = type is null ? null : [new() { Name = name, Type = type }];
        state.Catalog = new() { AllowedStepTypes = ["set"] };
        state.Plan = new() { Root = new() { Outputs = [new(name, new() { Kind = "string", Text = "original" })] } };
        state.RevisionScope = ["/root/outputs/" + name];
        return state;
    }

    private static JsonObject Schema(PlanningSession state, int version = 11) =>
        PlanningRepairPatch.Schema(state, PlanningRepairPatch.Template(state, version), version);

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
    public void MissingAcceptedOutputContractRetainsSemanticFallback()
    {
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(Arithmetic), Schema(Output(null))));
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 11)]
    public void OnlyCapturedFreshPolicyChangesTheNewRequestEnvelope(bool fresh, int version)
    {
        var state = Output(new() { Kind = "string" });
        state.EditablePaths = state.RevisionScope;
        if (fresh) state.Request.Options["planning_diagnostics_version"] = 1;
        var request = new PlanningPrompt(state).Request();
        Assert.Equal(version, PlanningRepairPatch.RequestContext(request)["repair"]!["version"]!.GetValue<int>());
        Assert.Equal(!fresh, PlanningContractValidation.ValidateInstance(Response(Arithmetic), request.StructuredOutputSchema!).Count == 0);
        Assert.True(PlanningRepairPatch.Verify(state, request));
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
    public void FreshOwnedBindingKeepsRemovalOnlyAndReferenceRepairKeepsExactChoices()
    {
        var state = RepairPatchTests.State(); state.Request.Options["planning_diagnostics_version"] = 1;
        state.Plan!.Root.Tasks[1].Inputs.Add(new("fixed", new() { Kind = "string", Text = "host" }));
        state.RevisionScope = ["/tasks/consumer/inputs/fixed"];
        var slots = PlanningRepairPatch.Slots(state, PlanningRepairPatch.Template(state)["$defs"]!.AsObject());
        Assert.Equal(new[] { "remove" }, Assert.Single(slots).Actions);
        state.RevisionScope = ["/tasks/consumer/inputs/text"];
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response("""{"kind":"field","port":"unrelated","items":[{"kind":"input","source":"record"}]}"""), Schema(state)));

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
    public void VersionTenSchemasAndAuthorityRemainHistorical()
    {
        var state = Output(new() { Kind = "string" });
        var schema = Schema(state, 10);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(Arithmetic), schema));
        // Collected before changing the production repair implementation.
        Assert.Equal("93985cb523481b0d41747e80c396e6201a19b24b1114cf4611c6e9d7c4d0770e", PlanningGraphCompiler.Fingerprint(schema.ToJsonString()));
        Assert.Equal("9715b0097d988d8d35e026dbe44b1ab8748005534cb65b1b375df60eeed658f9", PlanningRepairPatch.Authority(state, version: 10));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void PendingRepairRetainsIssuedSchemaAndAuthorityAfterSerialization(int version)
    {
        var state = Output(new() { Kind = "string" });
        if (version == 11) state.Request.Options["planning_diagnostics_version"] = 1;
        var request = new LLMRequest { ClientRequestId = "pending-version-" + version,
            Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = version, ["authority"] = PlanningRepairPatch.Authority(state, version: version) } },
            StructuredOutputSchema = Schema(state, version) };
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
}
