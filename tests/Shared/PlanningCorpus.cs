using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Planning.Examples;

/// <summary>Scripted semantic responses for offline tests. Requests and outcome oracles are frozen separately.</summary>
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
        var compiled = new TaskPlanCompiler().Compile(Tasks(name, catalog), catalog);
        if (compiled.Diagnostics.Count != 0) throw new InvalidOperationException(JsonSerializer.Serialize(compiled.Diagnostics.ToList(), PlanningJsonContext.Default.ListPlanningDiagnostic));
        return compiled.Graph!;
    }
    public static TaskPlan Greeting(string message = "Hello") => new() { Root = new()
    {
        Tasks = [new() { Id = "greet", Kind = "value", Objective = "Return a greeting", Outputs = [new("message", String(message))] }],
        Outputs = [new("message", Business("output", "greet", "message"))]
    } };
    public static TaskPlan Decision()
    {
        var plan = Greeting();
        plan.Choices = [new() { Id = "tone", Question = "Which tone?", Recommended = "formal", Alternatives = [new("formal", "Formal greeting", String("Hello")), new("casual", "Casual greeting", String("Hi"))] }];
        plan.Root.Tasks[0].Outputs = [new("message", Business("choice", "tone"))];
        return plan;
    }
    public static TaskPlan LiteralResult() => new() { Root = new() { Outputs = [new("result", Number(42))] } };

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
                plan.Groups.Add(new() { Id = "double_group", Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }],
                    Body = new() { Tasks = [Math("multiply_item", "number.multiply", Business("input", "value"), Number(2))], Outputs = [new("result", Output("multiply_item"))] } });
                main.Tasks.Add(new() { Id = "result", Objective = "Double each value preserving order", Kind = "foreach", Items = Business("input", "values"), Parallel = true,
                    Body = new() { Tasks = [new() { Id = "double", Objective = "Double this item", Kind = "call", Group = "double_group", Inputs = [new("value", Business("item"))] }], Outputs = [new("values", Output("double", "result"))] } });
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
            if (!local && discovery.Pages.Count == 0) proposal.DiscoveryRequests = [new(discovery.Sources[0].Id)];
            else if (!local && discovery.Pages[^1].NextCursor is { } cursor)
            { proposal.DiscoveryRequests = [new(discovery.Pages[^1].SourceId, cursor)]; }
            else
            {
                var issued = new PlanningCatalog { Capabilities = catalog.Capabilities.Concat(discovery.Pages.SelectMany(p => p.Capabilities)
                    .Where(c => catalog.Capabilities.All(resolved => resolved.Id != c.Id)).Select(c => new PlanningCapability
                    { Id = c.Id, Method = c.Name, StepType = c.StepType, Operation = c.Operation })).ToList() };
                proposal.Plan = Tasks(name, issued);
            }
            var json = JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal);
            return new() { Json = Transport(json, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) { _snapshot = state; DiagnosticHistory.AddRange(state.Diagnostics.Where(d => !DiagnosticHistory.Contains(d))); return Task.CompletedTask; }
    }
}
