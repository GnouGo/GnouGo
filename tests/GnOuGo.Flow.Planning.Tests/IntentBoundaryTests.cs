using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class IntentBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CanonicalIntentKeepsMeaningWithoutInactiveUnionFields()
    {
        var intent = PlannerFixture.Greeting();
        intent.Workflows[0].Inputs = [new() { Name = "optional", Required = false, Schema = new() { Nullable = true }, Default = new() { Kind = "null" } }];
        intent.Workflows[0].Steps[0].Input.Members.Add(new("omitted", new() { Kind = "omitted" }));
        intent.Workflows[0].Steps[0].Input.Members.Add(new("missing", new() { Kind = "hole" }));
        var json = PlanningJsonTransport.Intent(intent);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
        var literal = json["workflows"]![0]!["steps"]![0]!["input"]!["members"]![0]!["value"]!.AsObject();
        Assert.Equal(new[] { "kind", "text" }, literal.Select(p => p.Key));
        Assert.Equal("default", json["workflows"]![0]!["outputs"]![0]!["value"]!["resultChannel"]!.ToString());
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowIntentPlan)!;
        Assert.Equal("null", restored.Workflows[0].Inputs[0].Default!.Kind);
        Assert.Equal("omitted", restored.Workflows[0].Steps[0].Input.Members[1].Value.Kind);
        Assert.Equal("hole", restored.Workflows[0].Steps[0].Input.Members[2].Value.Kind);
        Assert.True(JsonNode.DeepEquals(json, PlanningJsonTransport.Intent(restored)));
        Assert.True(json.ToJsonString().Length < JsonSerializer.Serialize(intent, PlanningJsonContext.Default.WorkflowIntentPlan).Length);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("array")]
    public void IncompleteSchemasRemainVisibleButFailTheResponseBoundary(string type)
    {
        var intent = PlannerFixture.Greeting(); intent.Workflows[0].Outputs[0].Schema = new() { Type = type };
        var json = PlanningJsonTransport.Intent(intent);
        Assert.Equal(type, json["workflows"]![0]!["outputs"]![0]!["schema"]!["type"]!.ToString());
        Assert.Contains(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()),
            f => f.InstancePointer.StartsWith("/workflows/0/outputs/0/schema", StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalIntentRetainsInvalidFieldsForRepairEvidence()
    {
        var intent = PlannerFixture.Greeting();
        intent.Workflows[0].Steps[0].Input.Members[0].Value.Boolean = false;
        intent.Workflows[0].Steps[0].OutputSchema = new() { CapabilityId = "catalog", SchemaPointer = "/output", Nullable = true };
        var json = PlanningJsonTransport.Intent(intent);
        Assert.False(json["workflows"]![0]!["steps"]![0]!["input"]!["members"]![0]!["value"]!["boolean"]!.GetValue<bool>());
        Assert.True(json["workflows"]![0]!["steps"]![0]!["outputSchema"]!["nullable"]!.GetValue<bool>());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
    }

    [Fact]
    public void TypedMapsArraysAndCatalogReferencesConformToStrictShape()
    {
        var intent = PlannerFixture.Greeting();
        intent.Workflows[0].Outputs[0].Schema = new() { Type = "array", Items = new() { Type = "object", AdditionalProperties = new() { Type = "number" } } };
        intent.Workflows[0].Steps[0].OutputSchema = new() { CapabilityId = "catalog", SchemaPointer = "/output" };
        var json = PlanningJsonTransport.Intent(intent);
        Assert.Empty(PlanningContractValidation.ValidateSchema(PlanningSchemas.Intent(), strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
        Assert.Equal(new[] { "capabilityId", "schemaPointer" }, json["workflows"]![0]!["steps"]![0]!["outputSchema"]!.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData("hole")]
    [InlineData("omitted")]
    [InlineData("input")]
    [InlineData("compute")]
    public void FixturesRejectExecutableValuesInInputsAndObservations(string kind)
    {
        var intent = PlannerFixture.Greeting(); var invalid = new PlanningValue { Kind = kind, Source = "user", Text = "1" };
        intent.Fixtures = new() { Inputs = Obj("review", invalid), Observations = [new("main", "external", [invalid])] };
        var findings = PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(intent), PlanningSchemas.Intent());
        Assert.Contains(findings, f => f.InstancePointer.StartsWith("/fixtures/inputs", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.InstancePointer.StartsWith("/fixtures/observations/0/responses/0", StringComparison.Ordinal));
        Assert.Equal(kind, PlanningJsonTransport.Intent(intent)["fixtures"]!["inputs"]!["members"]![0]!["value"]!["kind"]!.ToString());
    }

    [Fact]
    public async Task GraphCoordinatesMapThroughHostWrapperReorderingAndMcpArguments()
    {
        var catalog = await Catalog(); catalog.Capabilities.Add(Capability());
        var intent = new WorkflowIntentPlan { Workflows = [new() { Steps = [
            new() { Key = "later", Kind = "set", Dependencies = ["read"] },
            new() { Key = "read", CapabilityId = "cap", Input = new() { Kind = "object", Members = [new("first", Text("a")), new("second", Text("b"))] } }
        ] }] };
        var state = State(intent, catalog); PlanningConfirmationGuards.Apply(state.Graph!, catalog);
        Assert.Equal("read", state.Graph!.Workflows[1].Steps[0].Key);
        state.Graph.Workflows[1].Steps[0].Input.Members[0].Value.Members.Reverse();
        state.Diagnostics = [new("CAPABILITY_ARGUMENT_INVALID", "/workflows/1/steps/0/input/members/0/value/members/0/value/text", "Bad second argument.")];
        var mapped = Assert.Single(PlanningDiagnosticLocations.ForIntent(state));
        Assert.Equal("/workflows/0/steps/1/input/members/1/value/text", mapped.Location);
        Assert.Contains("step 'read'", mapped.Message);
        Assert.Equal("b", PlanningFieldPaths.Read(PlanningJsonTransport.Intent(intent), mapped.Location)!.ToString());
        Assert.StartsWith("/workflows/1/", state.Diagnostics[0].Location);
    }

    [Fact]
    public async Task MissingArgumentMapsToExistingContainerAndNamesTheField()
    {
        var catalog = await Catalog(); var capability = Capability();
        capability.InputSchema["required"] = new JsonArray("first"); catalog.Capabilities.Add(capability);
        var intent = new WorkflowIntentPlan { Workflows = [new() { Steps = [new() { Key = "read", CapabilityId = "cap" }] }] };
        var state = State(intent, catalog);
        state.Diagnostics = [new("HOLE_UNRESOLVED", "/workflows/0/steps/0/input/members/0/value/members/0/value", "Supply a value.")];
        var mapped = Assert.Single(PlanningDiagnosticLocations.ForIntent(state));
        Assert.Equal("/workflows/0/steps/0/input/members", mapped.Location);
        Assert.Contains("Missing field 'first'", mapped.Message);
        Assert.IsType<JsonArray>(PlanningFieldPaths.Read(PlanningJsonTransport.Intent(intent), mapped.Location));
    }

    [Fact]
    public async Task NamedArgumentPathsMapAndCatalogBindingsRemainHostOwned()
    {
        var catalog = await Catalog(); var capability = Capability(); catalog.Capabilities.Add(capability);
        capability.RequestBindings = [new("/first", JsonValue.Create(5))];
        var intent = new WorkflowIntentPlan { Workflows = [new() { Steps = [new() { Key = "read", CapabilityId = "cap",
            Input = new() { Kind = "object", Members = [new("second", Text("value")), new("first", Text("model"))] } }] }] };
        var state = State(intent, catalog);
        state.Diagnostics = [new("CAPABILITY_ARGUMENT_INVALID", "/workflows/0/steps/0/input/request/second", "Invalid second."),
            new("CAPABILITY_ARGUMENT_INVALID", "/workflows/0/steps/0/input/request/first", "Invalid fixed first.")];
        var mapped = PlanningDiagnosticLocations.ForIntent(state);
        Assert.Equal("/workflows/0/steps/0/input/members/0/value", mapped[0].Location);
        Assert.Equal("PLANNING_HOST_CONTRACT", mapped[1].Code);
        state.Diagnostics = [new("ARTIFACT_BINDING_INVALID", "/workflows/0/steps/0/input/request/missing", "Supply the artifact.")];
        var missing = Assert.Single(PlanningDiagnosticLocations.ForIntent(state));
        Assert.Equal("/workflows/0/steps/0/input", missing.Location);
        Assert.Contains("Missing field 'missing'", missing.Message);
    }

    [Fact]
    public async Task UnmodifiedFinalizerComputationIsEditable()
    {
        var intent = new WorkflowIntentPlan { Workflows = [new() { Finally = [new() { Key = "cleanup", Kind = "set",
            If = new() { Kind = "compute", Text = "flag", Members = [new("flag", new() { Kind = "boolean", Boolean = true })] } }] }] };
        var state = State(intent, await Catalog());
        state.Diagnostics = [new("BOOLEAN_CONDITION_INVALID", "/workflows/0/finally/0/if/members/0/value/boolean", "Invalid flag.")];
        var mapped = Assert.Single(PlanningDiagnosticLocations.ForIntent(state));
        Assert.Equal("BOOLEAN_CONDITION_INVALID", mapped.Code);
        Assert.Equal("/workflows/0/finally/0/if/members/0/value/boolean", mapped.Location);
    }

    [Fact]
    public async Task ReservedReceiptUsesItsOriginalResponseSchema()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Catalog = await Catalog(); state.ModelCalls = 1;
        var originalSchema = PlanningSchemas.Intent();
        var stringVariant = originalSchema["$defs"]!["value"]!["anyOf"]![1]!;
        stringVariant["properties"]!["number"] = new JsonObject { ["type"] = "null" };
        stringVariant["required"]!.AsArray().Add("number");
        var response = PlanningJsonTransport.Intent(PlannerFixture.Greeting());
        response["workflows"]![0]!["steps"]![0]!["input"]!["members"]![0]!["value"]!["number"] = null;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, PlanningSchemas.Intent()));
        state.PendingCall = new() { Id = "reserved", Purpose = "intent", Request = new() { Prompt = "Original reserved prompt", StructuredOutputSchema = originalSchema } };
        runtime.Respond = request =>
        {
            Assert.Equal("Original reserved prompt", request.Prompt);
            Assert.True(JsonNode.DeepEquals(originalSchema, request.StructuredOutputSchema));
            return new() { Json = response };
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
    }

    [Fact]
    public async Task NestedStepsAndFinalizerConditionsRetainTheirIntentLocations()
    {
        var catalog = await Catalog();
        var intent = new WorkflowIntentPlan { Workflows = [new() {
            Steps = [new() { Key = "branch", Kind = "parallel", Branches = [new([
                new() { Key = "later", Kind = "set", Dependencies = ["first"] }, new() { Key = "first", Kind = "set" }
            ])] }],
            Finally = [new() { Key = "cleanup", Kind = "set", Dependencies = ["branch"], If = new() { Kind = "boolean", Boolean = true } }]
        }] };
        var state = State(intent, catalog);
        state.Diagnostics = [new("STEP_TYPE_DENIED", "/workflows/0/steps/0/branches/0/steps/0/type", "Bad kind."),
            new("BOOLEAN_CONDITION_INVALID", "/workflows/0/finally/0/if/members/1/value/boolean", "Bad condition.")];
        var findings = PlanningDiagnosticLocations.ForIntent(state);
        Assert.Equal("/workflows/0/steps/0/branches/0/steps/1/kind", findings[0].Location);
        Assert.Equal("/workflows/0/finally/0/if/boolean", findings[1].Location);
    }

    [Fact]
    public async Task GeneratedHostDefectsStopWithoutSpendingARepair()
    {
        var catalog = await Catalog(); catalog.Capabilities.Add(Capability());
        var state = State(new() { Workflows = [new() { Steps = [new() { Key = "action", CapabilityId = "cap" }] }] }, catalog);
        PlanningConfirmationGuards.Apply(state.Graph!, catalog); state.ModelCalls = 1;
        state.Diagnostics = [new("BOOLEAN_CONDITION_INVALID", "/workflows/0/steps/1/input", "Bad host permission.")];
        var runtime = new TestRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_HOST_CONTRACT");
    }

    [Fact]
    public async Task GeneratedOutputBindingsAndFinalizerGuardExpressionsAreHostOwned()
    {
        var catalog = await Catalog(); catalog.Capabilities.Add(Capability());
        var intent = new WorkflowIntentPlan { Workflows = [new() {
            Steps = [new() { Key = "action", CapabilityId = "cap" }],
            Outputs = [new() { Name = "result", Value = Text("value") }],
            Finally = [new() { Key = "cleanup", Kind = "set", Dependencies = ["action"], If = new() { Kind = "boolean", Boolean = true } }]
        }] };
        var state = State(intent, catalog); PlanningConfirmationGuards.Apply(state.Graph!, catalog);
        state.Diagnostics = [new("OUTPUT_REFERENCE_INVALID", "/workflows/0/outputs/0/value/source", "Invalid forwarding binding."),
            new("COMPUTATION_INVALID", "/workflows/1/finally/0/if/text", "Invalid generated conjunction.")];
        Assert.All(PlanningDiagnosticLocations.ForIntent(state), d => Assert.Equal("PLANNING_HOST_CONTRACT", d.Code));
    }

    [Fact]
    public async Task InvalidIntentOutputSchemaIsRepairableThroughConfirmationForwarding()
    {
        var catalog = await Catalog(); catalog.Capabilities.Add(Capability());
        var intent = new WorkflowIntentPlan { Workflows = [new() {
            Steps = [new() { Key = "action", CapabilityId = "cap" }],
            Outputs = [new() { Name = "result", Schema = new() { Type = "object" }, Value = new() { Kind = "output", Source = "action" } }]
        }] };
        var state = State(intent, catalog);
        await PlanningValidationPipeline.ValidateAsync(state, new TestRuntime(), Ct);
        var mapped = PlanningDiagnosticLocations.ForIntent(state);
        Assert.Contains(mapped, d => d.Code == "SCHEMA_INVALID" && d.Location == "/workflows/0/outputs/0/schema");
        Assert.DoesNotContain(mapped, d => d.Code == "PLANNING_HOST_CONTRACT");
    }

    [Fact]
    public async Task ReplayReportsSchemaAndFixtureRootsWithoutLosingIndependentFailures()
    {
        var intent = new WorkflowIntentPlan { Workflows = [new() { Steps = [
            new() { Key = "checks", Kind = "set", Input = Obj("items", new() { Kind = "compute", Text = "[]" }),
                OutputSchema = new() { Type = "object", Properties = [new() { Name = "items", Schema = new() { Type = "array", Items = new() { Type = "object" } } }] } },
            new() { Key = "consumer", Kind = "set", Input = new() { Kind = "object", Members = [
                new("items", new() { Kind = "output", Source = "checks", Path = ["items"] }),
                new("unrelated", new() { Kind = "output", Source = "absent" })] } }
        ] }], Fixtures = new() { Inputs = Obj("review", new() { Kind = "hole" }) } };
        var state = State(intent, await Catalog()); var runtime = new TestRuntime();
        await PlanningValidationPipeline.ValidateAsync(state, runtime, Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "SCHEMA_INVALID");
        Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_INPUT_INVALID");
        Assert.Contains(state.Diagnostics, d => d.Code == "BINDING_UNAVAILABLE" && d.Rule == "producer:checks");
        var mapped = PlanningDiagnosticLocations.ForIntent(state);
        var root = Assert.Single(mapped, d => d.Code == "SCHEMA_INVALID");
        Assert.Equal("/workflows/0/steps/0/outputSchema/properties/0/schema/items", root.Location);
        Assert.Contains("consumer", root.Message);
        Assert.DoesNotContain(mapped, d => d.Code is "BINDING_UNAVAILABLE" or "OUTPUT_REFERENCE_INVALID" && d.Rule == "producer:checks");
        Assert.Contains(mapped, d => d.Rule == "availability:absent");
        Assert.Contains(mapped, d => d.Code == "SCENARIO_INPUT_INVALID");
        Assert.Empty(runtime.Calls); Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task InvalidProducerSchemaDoesNotHideItsIndependentConditionalBindingFailure()
    {
        var intent = new WorkflowIntentPlan { Workflows = [new() { Steps = [
            new() { Key = "producer", Kind = "set", If = new() { Kind = "boolean", Boolean = false }, OutputSchema = new() { Type = "object" } },
            new() { Key = "consumer", Kind = "set", Input = Obj("value", new() { Kind = "output", Source = "producer" }) }
        ] }] };
        var state = State(intent, await Catalog());
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph!, state.Catalog!).ToList();
        var mapped = PlanningDiagnosticLocations.ForIntent(state);
        Assert.Contains(mapped, d => d.Code == "SCHEMA_INVALID");
        Assert.Contains(mapped, d => d.Code == "BINDING_UNAVAILABLE" && d.Rule == "availability:producer");
    }

    [Fact]
    public async Task RepairPromptUsesIntentLocationsAndCanonicalValues()
    {
        var runtime = new TestRuntime(); var catalog = await Catalog();
        var bad = PlannerFixture.Greeting(); bad.Workflows[0].Steps[0].Kind = "missing";
        var state = State(bad, catalog); state.ModelCalls = 1;
        state.Diagnostics = [new("STEP_TYPE_DENIED", "/workflows/0/steps/0/type", "Choose a supported kind.")];
        runtime.Respond = request =>
        {
            var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            Assert.Equal("/workflows/0/steps/0/kind", context["diagnostics"]![0]!["location"]!.ToString());
            Assert.Null(context["currentIntent"]!["workflows"]![0]!["steps"]![0]!["input"]!["number"]);
            return new() { Json = PlanningJsonTransport.Intent(PlannerFixture.Greeting()) };
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
    }

    private static async Task<PlanningCatalog> Catalog() => await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, Ct);
    private static PlanningSession State(WorkflowIntentPlan intent, PlanningCatalog catalog)
    {
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.IntentPlan = intent;
        state.Graph = PlanningGraphBuilder.Build(intent, catalog); state.Status = PlanningStatus.Generating; return state;
    }
    private static PlanningCapability Capability() => new() { Id = "cap", StepType = "mcp.call", Kind = "tool", Server = "fixture", Method = "read", EffectKind = "write",
        InputSchema = JsonNode.Parse("""{"type":"object","properties":{"first":{"type":"string"},"second":{"type":"string"}},"required":[]}""")!.AsObject(),
        OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""")!.AsObject() };
    private static PlanningValue Obj(string name, PlanningValue value) => new() { Kind = "object", Members = [new(name, value)] };
    private static PlanningValue Text(string text) => new() { Kind = "string", Text = text };
}
