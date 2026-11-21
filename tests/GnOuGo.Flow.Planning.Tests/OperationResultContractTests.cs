using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OperationResultContractTests
{
    [Theory]
    [InlineData("root", false)]
    [InlineData("sequence", false)]
    [InlineData("capture", false)]
    [InlineData("conditional", false)]
    [InlineData("conditional", true)]
    [InlineData("parallel", false)]
    [InlineData("call", false)]
    [InlineData("foreach", false)]
    public async Task WholeAgentResultsCrossScopesAndExecute(string boundary, bool alternate)
    {
        var (plan, catalog, engine, runner) = await Fixture();
        Wrap(plan, boundary, alternate);
        var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var contracts = JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog);
        var result = await Execute(plan, catalog, engine);
        Assert.True(result.Success, result.Error?.Message);
        var expected = JsonSerializer.SerializeToNode(runner.Result with
        { Verification = [new("check", true, "Matched observed execution evidence.")] }, AgentTaskJsonContext.Default.AgentTaskResult)!;
        var actual = result.Outputs!["result"]!;
        if (boundary == "foreach")
        {
            Assert.Equal(2, actual.AsArray().Count);
            foreach (var item in actual.AsArray()) Verify(item!);
        }
        else if (alternate) Assert.Equal("skipped", actual.GetValue<string>());
        else Verify(actual);
        Assert.Equal(alternate ? 0 : boundary == "foreach" ? 2 : 1, runner.Dispatches);
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(contracts, JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog));

        void Verify(JsonNode envelope)
        {
            // Independent value oracle: include unknown nested data and actual verifier findings.
            Assert.True(JsonNode.DeepEquals(expected["output"], envelope["output"]));
            Assert.True(JsonNode.DeepEquals(expected["evidence"], envelope["evidence"]));
            Assert.True(JsonNode.DeepEquals(expected["usage"], envelope["usage"]));
            Assert.True(JsonNode.DeepEquals(expected["artifacts"], envelope["artifacts"]));
            Assert.Equal("completed", envelope["status"]!.ToString());
            Assert.True(envelope["verification"]![0]!["passed"]!.GetValue<bool>());
            Assert.Equal("check", envelope["verification"]![0]!["requirement_id"]!.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedPayloadSchemaEstablishesNamedAndWholeResultFields(bool namedPort)
    {
        var (plan, catalog, engine, _) = await Fixture();
        var payload = namedPort ? Reference("produce", "output") : Field(Reference("produce"), "output");
        plan.Root.Outputs = [new("result", Field(payload, "answer"))];
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        Assert.Equal("string", compiled.Graph!.Workflows[0].Outputs[0].Schema.Contract!["type"]!.ToString());
        Assert.Equal("done", compiled.Graph.Workflows[0].Outputs[0].Schema.Contract!["enum"]![0]!.ToString());
        var result = await Execute(plan, catalog, engine);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("done", result.Outputs!["result"]!.ToString());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("unknown")]
    [InlineData("items")]
    public async Task UnknownContentsNeverEstablishTypedFields(string selection)
    {
        var (plan, catalog, _, _) = await Fixture();
        var payload = Reference("produce", "output");
        TaskValue selected = Field(payload, selection);
        if (selection == "open") selected = Field(selected, "invented");
        if (selection == "items")
        {
            plan.Root.Tasks.Add(new() { Id = "each", Kind = "foreach", Objective = "Inspect opaque items", Items = selected,
                MaxItems = 2, Body = new() { Outputs = [new("value", Field(new() { Kind = "item" }, "invented"))] } });
            plan.Root.Outputs = [new("result", Reference("each", "value"))];
        }
        else
        {
            catalog.Capabilities.Add(new() { Id = "sink", StepType = "mcp.call", InputSchema = Schema("""{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}""") });
            plan.Root.Tasks.Add(new() { Id = "consume", Kind = "operation", Operation = "sink", Objective = "Consume a string", Inputs = [new("text", selected)] });
        }
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compiled.Graph); Assert.NotEmpty(compiled.Diagnostics);
        Assert.DoesNotContain(compiled.Diagnostics, d => d.Code == "TASK_COMPILER_VALIDATION");
    }

    [Theory]
    [InlineData("denied", "AGENT_SCOPE_UNSUPPORTED", 0)]
    [InlineData("verification", "AGENT_VERIFICATION_FAILED", 1)]
    [InlineData("payload", "AGENT_OUTPUT_INVALID", 1)]
    public async Task ExecutionFailureNeverPublishesAnApprovedResult(string failure, string code, int dispatches)
    {
        var (plan, catalog, engine, runner) = await Fixture();
        if (failure == "denied") runner.Denied = true;
        if (failure == "verification") runner.Result = runner.Result with { Evidence = [] };
        if (failure == "payload") runner.Result = runner.Result with { Output = new JsonObject { ["answer"] = 7 } };
        Wrap(plan, "sequence", false);
        var result = await Execute(plan, catalog, engine);
        Assert.False(result.Success); Assert.Equal(code, result.Error?.Code); Assert.Equal(dispatches, runner.Dispatches);
    }

    [Fact]
    public async Task InvalidOutputDeclarationFailsAtItsBusinessInput()
    {
        var (plan, catalog, _, _) = await Fixture();
        plan.Root.Tasks[0].Inputs.Single(p => p.Name == "output_schema").Value.Members.Single(p => p.Name == "type").Value.Text = "invented";
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Location == "/tasks/produce/inputs/output_schema" && d.Code == "TASK_INPUT_SCHEMA");
    }

    [Fact]
    public async Task UnsupportedProducerImportRemainsACompilerFailure()
    {
        var (plan, catalog, _, _) = await Fixture();
        var capability = catalog.Capabilities.Single(c => c.StepType == "agent.run");
        JsonObject nested = new();
        for (var depth = 0; depth < 34; depth++) nested = new() { ["type"] = "object", ["properties"] = new JsonObject { ["nested"] = nested } };
        capability.OutputSchema = nested; capability.Operation = null;
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_COMPILER_VALIDATION");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TASK_INPUT_SCHEMA");
    }

    [Theory]
    [InlineData("renamed-source", "root", false, false)]
    [InlineData("unrelated-catalog", "capture", true, false)]
    [InlineData("renamed-source", "foreach", true, false)]
    [InlineData("unrelated-catalog", "sequence", false, true)]
    public async Task OrdinaryMcpContractsRetainPartialAndNullableContents(string server, string boundary, bool named, bool nullPayload)
    {
        var output = Schema("""{"type":"object","required":["parcel"],"properties":{"parcel":{"type":["object","null"],"required":["free","rows","closed"],"properties":{"free":{},"rows":{"type":"array","maxItems":3},"closed":{"type":"object","additionalProperties":false},"optional":true}}}}""");
        var payload = nullPayload ? null : JsonNode.Parse("""{"free":{"x":[null,"é"]},"rows":[false,1],"closed":{},"optional":["unknown"]}""");
        var response = new JsonObject { ["parcel"] = payload };
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new() { Tools = [new() { Name = "obtain", InputSchema = new JsonObject { ["type"] = "object" }, OutputSchema = output, EffectKind = "read" }],
            ToolHandlers = new() { ["obtain"] = _ => new() { Content = response.DeepClone() } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await Catalog(engine);
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "produce", Kind = "operation", Objective = "Obtain declared data",
            Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Server == server)).Id }], Outputs = [new("result", Reference("produce", named ? "parcel" : null))] } };
        Wrap(plan, boundary, false);
        var result = await Execute(plan, catalog, engine);
        Assert.True(result.Success, result.Error?.Message);
        var expected = named ? payload : response;
        Assert.True(JsonNode.DeepEquals(boundary == "foreach" ? new JsonArray(expected!.DeepClone(), expected.DeepClone()) : expected, result.Outputs!["result"]));
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("empty")]
    [InlineData("other_object")]
    [InlineData("array")]
    [InlineData("array_index")]
    [InlineData("root_constraint")]
    [InlineData("opaque_contract")]
    [InlineData("open_contract")]
    [InlineData("undeclared_field")]
    [InlineData("matching", true)]
    [InlineData("empty", true)]
    [InlineData("other_object", true)]
    [InlineData("array_index", true)]
    [InlineData("root_constraint", true)]
    [InlineData("opaque_contract", true)]
    [InlineData("open_contract", true)]
    [InlineData("undeclared_field", true)]
    [InlineData("matching", false, true)]
    [InlineData("matching", false, true, true)]
    [InlineData("array_index", false, true)]
    [InlineData("other_object", false, true)]
    [InlineData("open_contract", false, true)]
    [InlineData("unproven_alternative", false, true)]
    public async Task RequiredFieldOfAClosedMcpResultVariantChecksBeforeDispatch(string variant, bool retainInObject = false,
        bool forwardThroughBranches = false, bool alternate = false)
    {
        var resultSchema = Schema("""{"oneOf":[{"type":"null"},{"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false},{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false},{"type":"array","items":{"type":"string"}}]}""");
        if (forwardThroughBranches)
            resultSchema["oneOf"]!.AsArray().Add(Schema("""{"type":"object","properties":{"label":{"type":"string"},"extra":{"type":"string"}},"required":["label"],"additionalProperties":false}"""));
        if (variant == "opaque_contract") resultSchema["oneOf"]!.AsArray().Add(new JsonObject());
        if (variant == "open_contract") resultSchema["oneOf"]![1]!["additionalProperties"] = true;
        if (variant == "root_constraint") resultSchema["maxProperties"] = 0;
        var selectedField = variant == "array_index" ? "0" : "message";
        if (variant == "array_index")
            resultSchema["oneOf"]![2] = Schema("""{"type":"object","properties":{"0":{"type":"string"}},"required":["0"],"additionalProperties":false}""");
        var source = variant switch
        {
            "array" or "array_index" => (JsonNode)new JsonArray("another"),
            "other_object" => new JsonObject { ["label"] = "different" },
            _ => new JsonObject { ["message"] = variant == "empty" ? "" : "observed" }
        };
        var empty = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
        var message = Schema("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""");
        var received = new List<string>();
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("service", new()
        {
            Tools = [new() { Name = "read", InputSchema = empty, OutputSchema = resultSchema, EffectKind = "read" },
                new() { Name = "deliver", InputSchema = message, OutputSchema = empty, EffectKind = "write" }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = source.DeepClone() },
                ["deliver"] = args => { received.Add(args!["message"]!.GetValue<string>()); return new() { Content = new JsonObject() }; } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await Catalog(engine);
        string Operation(string method) => TaskOperations.Describe(catalog.Capabilities.Single(c => c.Server == "service" && c.Method == method)).Id;
        TaskValue Whole() => retainInObject
            ? TaskFieldBindingTests.Field(Reference("retain", "bundle"), "observation")
            : forwardThroughBranches ? Reference("forward", "observed") : Reference("obtain", null);
        TaskValue Field() => TaskFieldBindingTests.Field(Whole(), variant == "undeclared_field" ? "missing" : selectedField);
        TaskValue Compare(string op, params TaskValue[] operands) => new() { Kind = "predicate", Predicate = op, Items = [.. operands] };
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "obtain", Kind = "operation", Objective = "Read the current result.", Operation = Operation("read") },
            new() { Id = "deliver", Kind = "operation", Objective = "Deliver the observed message.", Operation = Operation("deliver"),
                Requires = Compare("and", Compare("not_equal", Whole(), new()), Compare("not_equal", Field(), Text(""))),
                Inputs = [new("message", Field())] }] } };
        if (retainInObject)
            plan.Root.Tasks.Insert(1, new() { Id = "retain", Kind = "value", Objective = "Retain the complete source observation.",
                Outputs = [new("bundle", new() { Kind = "object", Members = [new("observation", Reference("obtain"))] })] });
        if (forwardThroughBranches)
        {
            var choose = new PlanTask
            {
                Id = "choose", Kind = "conditional", Objective = "Return the observation from either path.",
                Condition = alternate ? new() { Kind = "boolean", Boolean = false } : Compare("not_equal", Reference("obtain"), new()),
                Body = new() { Outputs = [new("observed", Reference("obtain"))] },
                Otherwise = new() { Outputs = [new("observed", variant == "unproven_alternative" ? Text("fallback") : Reference("obtain"))] }
            };
            plan.Root.Tasks.Insert(1, new() { Id = "forward", Kind = "sequence", Objective = "Forward the observation.",
                Body = new() { Tasks = [choose], Outputs = [new("observed", Reference("choose", "observed"))] } });
        }
        var state = PlannerFixture.Session();
        state.Requirements = PlannerFixture.Requirements(); state.Catalog = catalog;
        var grammar = PlanningSchemas.TaskPlanGrammar(state);
        var wire = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan), grammar, grammar)!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, grammar));
        var parsed = wire.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        if (variant is "opaque_contract" or "open_contract" or "undeclared_field" or "unproven_alternative")
        {
            Assert.Contains(new TaskPlanCompiler().Compile(parsed, catalog).Diagnostics, d => d.Code is "TASK_FIELD_TYPE" or "TASK_FIELD_UNKNOWN");
            return;
        }
        foreach (var compact in new[] { false, true })
        {
            received.Clear();
            var run = await Execute(parsed, catalog, engine, compact);
            Assert.Equal(variant == "matching", run.Success);
            Assert.Equal(variant == "matching" ? new[] { "observed" } : [], received);
        }
    }

    [Fact]
    public void RemovingANullAlternativeCannotGrantAValueExcludedByOneOf()
    {
        var original = Schema("""{"oneOf":[{"type":"null"},{}]}""");
        var changed = Schema("""{"oneOf":[{}]}""");
        Assert.Empty(PlanningContractValidation.ValidateInstance(null, changed));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(null, original));
        Assert.False(PlanningContractCompatibility.Fits(changed, original));
    }

    [Fact]
    public void ImportPreservesConstraintsAndDoesNotInterpretInstanceDataAsSchemas()
    {
        var contract = Schema("""{"type":"object","required":["bag","rows","fixed"],"properties":{"bag":{"type":["object","null"]},"rows":{"type":"array","minItems":1,"maxItems":2},"fixed":{"const":{"type":"array","properties":{}}}},"additionalProperties":false}""");
        var before = contract.ToJsonString();
        var imported = PlanningContractShapes.Producer(contract);
        Assert.Equal(before, contract.ToJsonString());
        Assert.True(JsonNode.DeepEquals(contract["properties"]!["fixed"], imported["properties"]!["fixed"]));
        Assert.Empty(PlanningContractValidation.ValidateSchema(imported));
        PlanningGraphValidation.RequireTyped(imported, 0);
        foreach (var instance in new[] {
            """{"bag":null,"rows":[{}],"fixed":{"type":"array","properties":{}}}""",
            """{"bag":{"open":true},"rows":[null,1],"fixed":{"type":"array","properties":{}}}""",
            """{"bag":1,"rows":[],"fixed":{}}""",
            """{"bag":{},"rows":[1,2,3],"fixed":{"type":"array","properties":{}},"extra":true}""" })
            Assert.Equal(PlanningContractValidation.ValidateInstance(JsonNode.Parse(instance), contract).Count == 0,
                PlanningContractValidation.ValidateInstance(JsonNode.Parse(instance), imported).Count == 0);
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("continued")]
    public async Task AgentAvailabilityProofRejectsSkippedOrContinuedProducers(string mode)
    {
        var (plan, catalog, _, _) = await Fixture();
        plan.Root.Outputs = [new("result", Field(Reference("produce", "output"), "answer"))];
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        var agent = compilation.Graph!.Workflows[0].Steps.Single(n => n.Type == "agent.run");
        if (mode == "skipped") agent.If = new() { Kind = "boolean", Boolean = false };
        else agent.OnError.Add(new(null, "continue", new() { Kind = "object" }, null));
        Assert.Contains(PlanningExecutableValidation.Validate(compilation.Graph, catalog), d => d.Code == "BINDING_UNAVAILABLE");
    }

    [Fact]
    public async Task SchemaFailureNamesTheNestedPointerAndBusinessProducer()
    {
        var (plan, catalog, _, _) = await Fixture();
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        // An invalid generated contract remains a compiler failure, not a new repair permission.
        compilation.Graph!.Workflows[0].Outputs[0].Schema.Contract!["properties"]!["output"]!["properties"]!["unknown"] = new JsonObject();
        var finding = Assert.Single(PlanningGraphValidation.Validate(compilation.Graph, catalog), d => d.Code == "SCHEMA_INVALID");
        var located = compilation.Locate(finding);
        Assert.Equal("/root/outputs/result", located.Location);
        Assert.Contains("/properties/output/properties/unknown", located.Message);
        Assert.Contains("Producer: /tasks/produce", located.Message);
    }

    private static async Task<GnOuGo.Flow.Core.Models.RunResult> Execute(TaskPlan plan, PlanningCatalog catalog, WorkflowEngine engine, bool compact = false)
    {
        var request = new PlanningRequest();
        if (compact) request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compiled.Diagnostics); Assert.NotNull(compiled.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph, catalog, compact));
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph, catalog));
        var yaml = compact ? new PlanningGraphCompiler().Compile(compiled.Graph, catalog, "variants", descriptions: true,
            fuseBindings: true, consumerBindings: true, directProjections: true, nativeMappings: true, readableMappings: true,
            leanMappings: true, compactProjections: true, inlineConsumers: true, directValues: true, checkedControls: true,
            readableBranches: true, simplifiedLocals: true) : new PlanningGraphCompiler().Compile(compiled.Graph, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
    }

    private static void Wrap(TaskPlan plan, string kind, bool alternate)
    {
        if (kind == "root") return;
        var body = new TaskScope { Tasks = plan.Root.Tasks, Outputs = plan.Root.Outputs };
        var scope = new PlanTask { Id = "boundary", Kind = kind, Objective = "Export the observed result", Body = body };
        plan.Root = new() { Tasks = [scope], Outputs = [new("result", Reference("boundary", "result"))] };
        if (kind == "capture") { scope.Kind = "sequence"; plan.Root.Tasks.InsertRange(0, body.Tasks); body.Tasks = []; }
        if (kind == "conditional") { scope.Condition = new() { Kind = "boolean", Boolean = !alternate }; scope.Otherwise = new() { Outputs = [new("result", Text("skipped"))] }; }
        if (kind == "parallel") { scope.Body = null; scope.Branches = [body, new() { Outputs = [new("other", Text("independent"))] }]; }
        if (kind == "call") { scope.Body = null; scope.Group = "reusable"; plan.Groups = [new() { Id = "reusable", Body = body }]; }
        if (kind == "foreach") { scope.MaxItems = 2; scope.Items = new() { Kind = "array", Items = [Text("first"), Text("second")] }; }
    }

    private static async Task<(TaskPlan, PlanningCatalog, WorkflowEngine, Runner)> Fixture()
    {
        var runner = new Runner(); var engine = new WorkflowEngine { Limits = new() { TenantId = "fixture", RunId = "result-contract" } };
        engine.AgentTaskRunners["arbitrary-runner"] = runner;
        var catalog = await Catalog(engine);
        var input = Schema("""
            {"objective":"Observe a bounded result","workspace":"workflows/fixture","capabilities":["fixture.read"],
             "budget":{"max_elapsed_milliseconds":10000,"max_model_calls":1,"max_total_tokens":100},
             "output_schema":{"type":"object","required":["answer","open","items","unknown"],"properties":{
                "answer":{"type":"string","enum":["done"]},"open":{"type":"object"},"items":{"type":"array"},"unknown":{}}},
             "verification":[{"id":"check","kind":"observation","subject":"fixture","facts_schema":{"type":"object","required":["ok"],"properties":{"ok":{"const":true}}}}]}
            """);
        return (new() { Root = new() { Tasks = [new() { Id = "produce", Kind = "operation", Objective = "Observe a bounded result",
            Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.StepType == "agent.run")).Id,
            Inputs = input.Select(p => new TaskOutput(p.Key, Literal(p.Value))).ToList() }], Outputs = [new("result", Reference("produce"))] } }, catalog, engine, runner);
    }

    private static async Task<PlanningCatalog> Catalog(WorkflowEngine engine)
    {
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(PlannerFixture.Ct))
            foreach (var summary in (await runtime.Capabilities.ListAsync(source.Id, null, PlannerFixture.Ct)).Capabilities)
                catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, PlannerFixture.Ct));
        return catalog;
    }

    private sealed class Runner : IAgentTaskRunner
    {
        internal int Dispatches;
        internal bool Denied;
        internal AgentTaskResult Result = new("completed", Schema("""{"answer":"done","open":{"nested":[null,4,"é"]},"items":[{"opaque":true},null],"unknown":[1,"free"]}"""),
            [new("receipt", "observation", "fixture", new() { ["ok"] = true })], [new("artifact", "fixture", "observed")], new(0, 0, 1));
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Denied ? ["Fixture permission denied"] : []);
        public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct) { Dispatches++; return Task.FromResult(Result); }
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => throw new InvalidOperationException("No recovery dispatch expected");
    }

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
    private static TaskValue Reference(string source, string? port = null) => new() { Kind = "output", Source = source, Port = port };
    private static TaskValue Field(TaskValue value, string name) => new() { Kind = "field", Items = [value], Port = name };
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskValue Literal(JsonNode? node) => node switch
    {
        JsonObject o => new() { Kind = "object", Members = o.Select(p => new TaskOutput(p.Key, Literal(p.Value))).ToList() },
        JsonArray a => new() { Kind = "array", Items = a.Select(Literal).ToList() },
        JsonValue v when v.TryGetValue<string>(out var text) => Text(text),
        JsonValue v when v.TryGetValue<bool>(out var boolean) => new() { Kind = "boolean", Boolean = boolean },
        JsonValue v when v.TryGetValue<double>(out var number) => new() { Kind = "number", Number = number },
        null => new() { Kind = "null" },
        _ => throw new InvalidOperationException()
    };
}
