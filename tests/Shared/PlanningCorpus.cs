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
    public static PlanningValue Num(double n) => new() { Kind = "number", Number = n };
    public static PlanningValue Text(string text) => new() { Kind = "string", Text = text };
    public static PlanningValue Projection(params (string Name, PlanningValue Value)[] members)
        => new() { Kind = "projection", Members = members.Select(m => new PlanningMember(m.Name, m.Value)).ToList() };
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
    public static TaskValue Number(double value) => new() { Kind = "number", Number = value };
    public static TaskValue String(string value) => new() { Kind = "string", Text = value };
    public static TaskPlan Tasks(string name, PlanningCatalog catalog)
    {
        var plan = new TaskPlan(); var main = plan.Root;
        PlanTask Invoke(string id, string method, params TaskOutput[] inputs) => new() { Id = id, Objective = "Perform " + method, Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id, Inputs = inputs.ToList() };
        PlanTask Math(string id, string operation, TaskValue left, TaskValue right) => new() { Id = id, Objective = "Calculate result", Kind = "value", Outputs = [new("value", new() { Kind = "arithmetic", Text = operation, Items = [left, right] })] };
        TaskValue Output(string id, string? port = "value") => Business("output", id, port);
        var result = Output("result");
        switch (name)
        {
            case "local": main.Tasks.Add(Math("result", "multiply", Number(6), Number(7))); break;
            case "read_transform": main.Tasks.Add(Invoke("read", "read")); main.Tasks.Add(Math("result", "multiply", Output("read"), Number(2))); break;
            case "nullable_defaults":
                plan.Inputs.Add(new() { Name = "increment", Type = new() { Kind = "number" }, Required = false, Default = Number(2) });
                main.Tasks.Add(Invoke("read", "read_optional")); main.Tasks.Add(new() { Id = "fallback", Objective = "Use zero when the observed number is null", Kind = "conditional",
                    Condition = new() { Kind = "predicate", Predicate = "not_equal", Items = [Output("read"), new()] },
                    Body = new() { Outputs = [new("value", Output("read"))] }, Otherwise = new() { Outputs = [new("value", Number(0))] } });
                main.Tasks.Add(Math("result", "add", Output("fallback"), Business("input", "increment"))); break;
            case "conditional":
                plan.Inputs.Add(new() { Name = "enabled", Type = new() { Kind = "boolean" } });
                main.Tasks.Add(new() { Id = "result", Objective = "Read only when enabled", Kind = "conditional", Condition = Business("input", "enabled"),
                    Body = new() { Tasks = [Invoke("read", "read")], Outputs = [new("value", Output("read"))] }, Otherwise = new() { Outputs = [new("value", Number(0))] } }); break;
            case "collections":
                plan.Inputs.Add(new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "number" } } });
                plan.Groups.Add(new() { Id = "double_group", Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }],
                    Body = new() { Tasks = [Math("multiply_item", "multiply", Business("input", "value"), Number(2))], Outputs = [new("result", Output("multiply_item"))] } });
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
            var selected = alternatives.OfType<JsonObject>().FirstOrDefault(s => Matches(value, s, root));
            if (selected is null) throw new InvalidOperationException("The scripted value cannot be represented by the issued schema.");
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
        if (schema["anyOf"] is JsonArray alternatives) return alternatives.OfType<JsonObject>().Any(s => Matches(value, s, root));
        if (schema["type"]?.ToString() == "null") return value is null;
        if (value is null) return false;
        JsonNode? Resolve(JsonNode? node) => node?["$ref"] is { } link ? Resolve(root["$defs"]![link.ToString().Split('/')[^1]]) : node;
        var properties = schema["properties"] as JsonObject;
        foreach (var discriminator in new[] { "kind", "scope" })
            if (properties?[discriminator] is JsonObject domain && value[discriminator] is { } supplied && !Matches(supplied, domain, root)) return false;
        if (value is JsonObject task && task["kind"]?.ToString() == "transform" && properties?.ContainsKey("mode") == true &&
            properties.ContainsKey("each") != (task["each"] is not null)) return false;
        if (properties?.ContainsKey("sourceId") == true &&
            (Resolve(properties["operationIds"])?["type"]?.ToString() == "array") != (value["operationIds"] is not null)) return false;
        if (properties?.ContainsKey("sourceId") == true && properties.ContainsKey("producedArtifactKind") != (value["producedArtifactKind"] is not null)) return false;
        if (value is JsonObject obj && obj.ContainsKey("nullable") && properties?["kind"] is not null &&
            (properties.ContainsKey("nullable") ? !Matches(obj["nullable"], properties["nullable"]!.AsObject(), root) : obj["nullable"]?.ToString() == "true")) return false;
        if (properties?["text"] is null && properties?["kind"]?["enum"] is JsonArray typeKinds &&
            typeKinds.Any(k => k?.ToString() == "string"))
        {
            var enumSchema = properties["enum"] as JsonObject;
            if (enumSchema is null ? value["enum"] is not null : !Matches(value["enum"], enumSchema, root)) return false;
        }
        if (properties?["name"] is not null && properties["type"] is not null)
        {
            if (!properties.ContainsKey("required") && value["required"]?.ToString() == "false") return false;
            if (!properties.ContainsKey("default") && value["default"] is not null) return false;
        }
        if (schema["enum"] is JsonArray allowed) return allowed.Any(v => JsonNode.DeepEquals(value, v));
        if (properties is not null && properties.Any(p => p.Value?["enum"] is JsonArray && !Matches(value[p.Key], p.Value.AsObject(), root))) return false;
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
        public ICapabilityCatalog Capabilities => _actual.Capabilities;
        public List<PlanningDiagnostic> DiagnosticHistory { get; } = [];
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _actual.DiscoverAsync(request, ct);
        public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (live is not null) return await live.CallAsync(request, ct);
            throw new InvalidOperationException("Offline fixture '" + name + "' must provide explicit current incremental responses.");
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) { DiagnosticHistory.AddRange(state.Diagnostics.Where(d => !DiagnosticHistory.Contains(d))); return Task.CompletedTask; }
    }
}

/// <summary>Static TaskPlan input for compiler and oracle fixtures; never a planning response protocol.</summary>
public sealed class TaskPlanFixture
{
    public PlanningRequirements? Requirements { get; set; }
    public TaskPlan? Plan { get; set; }
    public List<PlanningDiscoveryRequest>? DiscoveryRequests { get; set; }
    public List<PlanningQuestion>? Clarifications { get; set; }
    public string Explanation { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(TaskPlanFixture))]
public partial class PlanningFixtureJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
