using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Planning.Examples;

/// <summary>Scripted graph responses for offline tests. Requests and outcome oracles are frozen separately.</summary>
public static class PlanningCorpus
{
    public static readonly string[] Names = PlanningBenchmarkCases.Names;
    public static string Prompt(string name) => PlanningBenchmarkCases.Prompt(name);
    public static PlanningValue Ref(string kind, string source, params string[] path) => new() { Kind = kind, Source = source, Path = path.ToList() };
    public static PlanningValue Num(decimal n) => new() { Kind = "number", Number = n };
    public static PlanningValue Text(string text) => new() { Kind = "string", Text = text };
    public static PlanningValue Obj(params (string Name, PlanningValue Value)[] fields) => new() { Kind = "object", Members = fields.Select(f => new PlanningMember(f.Name, f.Value)).ToList() };
    public static PlanningGraph Graph(string name, PlanningCatalog catalog)
    {
        var main = new PlanningWorkflow(); var graph = new PlanningGraph { Summary = Prompt(name), Workflows = [main] };
        PlanningNode Invoke(string id, string method, params (string Name, PlanningValue Value)[] fields) => new()
        { Key = id, Type = "mcp.call", CapabilityId = catalog.Capabilities.Single(c => c.Method == method).Id, Input = Obj(("request", Obj(fields))) };
        PlanningNode Math(string id, string operation, PlanningValue left, PlanningValue right) => new() { Key = id, Type = operation, Input = Obj(("left", left), ("right", right)) };
        var result = Ref("output", "result", "value"); var schema = new PlanningSchema { Type = "number" };
        switch (name)
        {
            case "local": main.Steps.Add(Math("result", "number.multiply", Num(6), Num(7))); break;
            case "read_transform":
                main.Steps.Add(Invoke("read", "read")); main.Steps.Add(Math("result", "number.multiply", Ref("output", "read", "value"), Num(2))); break;
            case "nullable_defaults":
                main.Inputs.Add(new() { Name = "increment", Schema = new() { Type = "number" }, Required = false, Default = Num(2) });
                main.Steps.Add(Invoke("read", "read_optional")); main.Steps.Add(Math("fallback", "number.default", Ref("output", "read", "value"), Num(0)));
                main.Steps.Add(Math("result", "number.add", Ref("output", "fallback", "value"), Ref("input", "increment"))); break;
            case "conditional":
                main.Inputs.Add(new() { Name = "enabled", Schema = new() { Type = "boolean" } });
                main.Steps.Add(new() { Key = "choose", Type = "switch", Expr = Ref("input", "enabled"), Cases = [new("true", null, [Invoke("read", "read")])],
                    Default = [new() { Key = "zero", Type = "set", Input = Obj(("value", Num(0))) }] });
                main.Steps.Add(new() { Key = "result", Type = "value.project", OutputSchema = new() { Type = "object", Properties = [new() { Name = "value", Schema = schema }] },
                    Input = Obj(("value", Ref("output", "choose")), ("paths", new() { Kind = "array", Items = [new() { Kind = "array", Items = [Text("read"), Text("response"), Text("value")] }, new() { Kind = "array", Items = [Text("zero"), Text("value")] }] })) });
                break;
            case "collections":
                main.Inputs.Add(new() { Name = "values", Schema = new() { Type = "array", Items = schema } });
                graph.Workflows.Add(new() { Key = "double", Inputs = [new() { Name = "value", Schema = schema }],
                    Steps = [Math("double", "number.multiply", Ref("input", "value"), Num(2))], Outputs = [new() { Name = "result", Schema = schema, Value = Ref("output", "double", "value") }] });
                main.Steps.Add(new() { Key = "each", Type = "loop.parallel", Input = Obj(("items", Ref("input", "values"))),
                    Steps = [new() { Key = "call", Type = "workflow.call", Input = Obj(("ref", Ref("workflow", "double")), ("args", Obj(("value", Ref("loop_item", "each"))))) }] });
                main.Steps.Add(new() { Key = "result", Type = "array.project", Input = Obj(("items", Ref("output", "each", "results")), ("path", new() { Kind = "array", Items = [Text("call"), Text("outputs"), Text("result")] })),
                    OutputSchema = new() { Type = "object", Properties = [new() { Name = "values", Schema = new() { Type = "array", Items = schema } }] } });
                schema = new() { Type = "array", Items = schema }; result = Ref("output", "result", "values"); break;
            case "protected_cleanup":
                main.Steps.Add(Invoke("write", "write")); main.Finally.Add(Invoke("cleanup", "cleanup")); result = Num(42); break;
            case "review_french": case "review_distractors":
                main.Inputs = [new() { Name = "pr_url" }, new() { Name = "review_text" }];
                main.Steps.Add(Invoke("clone", "clone_repository", ("pr_url", Ref("input", "pr_url"))));
                foreach (var check in new[] { "dependencies", "lint", "unit", "integration" })
                    main.Steps.Add(Invoke(check, "run_check", ("directory", Ref("output", "clone", "directory")), ("check", Text(check))));
                main.Steps.Add(Invoke("review", "review_changes", ("directory", Ref("output", "clone", "directory")), ("review_text", Ref("input", "review_text"))));
                main.Steps.Add(Invoke("evaluate", "evaluate_review", ("directory", Ref("output", "clone", "directory"))));
                main.Steps.Add(Invoke("publish", "publish_review", ("draftId", Ref("output", "evaluate", "draftId"))));
                var cleanup = Invoke("cleanup", "remove_workspace", ("directory", Ref("output", "clone", "directory")));
                cleanup.If = new() { Kind = "expression", Text = "data.steps[\"clone\"] != null" }; main.Finally.Add(cleanup);
                main.Outputs.Add(new() { Name = "review", Schema = new() { CapabilityId = catalog.Capabilities.Single(c => c.Method == "evaluate_review").Id, SchemaPointer = "/output" }, Value = Ref("output", "evaluate") });
                return graph;
        }
        main.Outputs.Add(new() { Name = "result", Schema = schema, Value = result }); return graph;
    }

    public static TaskValue Business(string kind, string? source = null, string? port = null) => new() { Kind = kind, Source = source, Port = port };
    public static TaskValue Number(decimal value) => new() { Kind = "number", Number = value };
    public static TaskValue String(string value) => new() { Kind = "string", Text = value };
    public static TaskPlan Tasks(string name, PlanningCatalog catalog)
    {
        var plan = new TaskPlan(); var main = plan.Root;
        PlanTask Invoke(string id, string method, params TaskOutput[] inputs) => new() { Id = id, Objective = "Perform " + method, Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id, Inputs = inputs.ToList() };
        PlanTask Math(string id, string operation, TaskValue left, TaskValue right) => new() { Id = id, Objective = "Calculate result", Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.StepType == operation)).Id, Inputs = [new("left", left), new("right", right)] };
        TaskValue Output(string id, string? port = "value") => Business("output", id, port);
        var result = Output("result");
        switch (name)
        {
            case "local": main.Tasks.Add(Math("result", "number.multiply", Number(6), Number(7))); break;
            case "read_transform": main.Tasks.Add(Invoke("read", "read")); main.Tasks.Add(Math("result", "number.multiply", Output("read"), Number(2))); break;
            case "nullable_defaults":
                plan.Inputs.Add(new() { Name = "increment", Type = new() { Kind = "number" }, Required = false, Default = Number(2) });
                main.Tasks.Add(Invoke("read", "read_optional")); main.Tasks.Add(Math("fallback", "number.default", Output("read"), Number(0)));
                main.Tasks.Add(Math("result", "number.add", Output("fallback"), Business("input", "increment"))); break;
            case "conditional":
                plan.Inputs.Add(new() { Name = "enabled", Type = new() { Kind = "boolean" } });
                main.Tasks.Add(new() { Id = "result", Objective = "Read only when enabled", Kind = "conditional", Condition = Business("input", "enabled"),
                    Body = new() { Tasks = [Invoke("read", "read")], Outputs = [new("value", Output("read"))] }, Otherwise = new() { Outputs = [new("value", Number(0))] } }); break;
            case "collections":
                plan.Inputs.Add(new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "number" } } });
                plan.Groups.Add(new() { Id = "double", Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }],
                    Body = new() { Tasks = [Math("double", "number.multiply", Business("input", "value"), Number(2))], Outputs = [new("result", Output("double"))] } });
                main.Tasks.Add(new() { Id = "result", Objective = "Double each value preserving order", Kind = "foreach", Items = Business("input", "values"), Parallel = true,
                    Body = new() { Tasks = [new() { Id = "double", Objective = "Double this item", Kind = "call", Group = "double", Inputs = [new("value", Business("item"))] }], Outputs = [new("values", Output("double", "result"))] } });
                result = Output("result", "values"); break;
            case "protected_cleanup":
                main.Tasks.Add(Invoke("write", "write")); main.Always.Add(Invoke("cleanup", "cleanup")); result = Number(42); break;
            case "review_french": case "review_distractors":
                plan.Inputs = [new() { Name = "pr_url" }, new() { Name = "review_text" }];
                main.Tasks.Add(Invoke("clone", "clone_repository", new TaskOutput("pr_url", Business("input", "pr_url"))));
                foreach (var check in new[] { "dependencies", "lint", "unit", "integration" }) main.Tasks.Add(Invoke(check, "run_check", new TaskOutput("directory", Output("clone", "directory")), new("check", String(check))));
                main.Tasks.Add(Invoke("review", "review_changes", new TaskOutput("directory", Output("clone", "directory")), new("review_text", Business("input", "review_text"))));
                main.Tasks.Add(Invoke("evaluate", "evaluate_review", new TaskOutput("directory", Output("clone", "directory"))));
                main.Tasks.Add(Invoke("publish", "publish_review", new TaskOutput("draftId", Output("evaluate", "draftId"))));
                main.Always.Add(Invoke("cleanup", "remove_workspace", new TaskOutput("directory", Output("clone", "directory"))));
                main.Outputs.Add(new("review", Output("evaluate", null))); return plan;
        }
        main.Outputs.Add(new("result", result)); return plan;
    }

    public static PlanningRequirements Requirements(string name) => new()
    { Summary = Prompt(name), Outcomes = [new(name.StartsWith("review_", StringComparison.Ordinal) ? "review" : "result", Prompt(name))] };

    /// <summary>Projects fixture DTOs to the exact strict transport schema. Never used in production.</summary>
    public static JsonNode? Transport(JsonNode? value, JsonObject schema, JsonObject root)
    {
        if (schema["$ref"] is { } reference) return Transport(value, root["$defs"]![reference.ToString().Split('/')[^1]]!.AsObject(), root);
        if (schema["anyOf"] is JsonArray alternatives)
        {
            var selected = alternatives.OfType<JsonObject>().First(s => Matches(value, s, root));
            return Transport(value, selected, root);
        }
        if (value is null) return null;
        if (schema["properties"] is JsonObject properties)
            return new JsonObject(properties.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Transport(value[p.Key], p.Value!.AsObject(), root))));
        if (schema["items"] is JsonObject item && value is JsonArray array) return new JsonArray(array.Select(v => Transport(v, item, root)).ToArray());
        return value.DeepClone();
    }
    private static bool Matches(JsonNode? value, JsonObject schema, JsonObject root)
    {
        if (schema["$ref"] is { } reference) return Matches(value, root["$defs"]![reference.ToString().Split('/')[^1]]!.AsObject(), root);
        if (schema["type"]?.ToString() == "null") return value is null;
        if (value is null) return false;
        if (schema["properties"]?["schemaPointer"] is not null) return value["capabilityId"] is not null;
        if (schema["properties"]?["kind"]?["enum"] is JsonArray kinds) return kinds.Any(k => k?.ToString() == value["kind"]?.ToString());
        if (schema["properties"]?["type"]?["enum"] is JsonArray types) return types.Any(t => t?.ToString() == value["type"]?.ToString());
        return true;
    }
    public sealed class Human(bool answer = true) : IHumanInputProvider
    { public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer }); }
    public sealed class Runtime(string name, WorkflowEngine engine, ILLMClient? live = null) : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _actual = new(engine, (_, _) => Task.CompletedTask);
        private PlanningSession? _snapshot;
        public ICapabilityCatalog Capabilities => _actual.Capabilities;
        public List<PlanningDiagnostic> DiagnosticHistory { get; } = [];
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _actual.DiscoverAsync(request, ct);
        public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (live is not null) return await live.CallAsync(request, ct);
            var catalog = _snapshot!.Catalog!;
            var discovery = _snapshot.Discovery;
            var proposal = new PlanningProposal { Requirements = Requirements(name) };
            var local = name is "local" or "collections";
            if (!local && discovery.Pages.Count == 0) proposal.SourceId = discovery.Sources[0].Id;
            else if (!local && discovery.Pages[^1].NextCursor is { } cursor)
            { proposal.SourceId = discovery.Pages[^1].SourceId; proposal.Cursor = cursor; }
            else if (!local && catalog.Capabilities.Count == 0)
                proposal.CapabilityIds = discovery.Pages.SelectMany(p => p.Capabilities).Where(c => !c.Name.StartsWith("unrelated_", StringComparison.Ordinal)).Select(c => c.Id).ToList();
            else proposal.Graph = Graph(name, catalog);
            var json = JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal);
            return new() { Json = Transport(json, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) { _snapshot = state; DiagnosticHistory.AddRange(state.Diagnostics.Where(d => !DiagnosticHistory.Contains(d))); return Task.CompletedTask; }
    }
}
