using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Planning.Examples;

public static class PlanningCorpus
{
    public static readonly string[] Names = PlanningBenchmarkCases.Names;
    public static string Prompt(string name) => PlanningBenchmarkCases.Prompt(name);
    public static GroundedPlan Intent(string name, PlanningCatalog catalog)
    {
        var plan = new GroundedPlan { Summary = Prompt(name) };
        InvokeGroundedOperation Invoke(string id, string method, params GroundedMember[] args) => new() { Id = id, Capability = catalog.Capabilities.Single(c => c.Method == method).Id, Arguments = args.ToList() };
        GroundedValue Ref(string kind, string source, params string[] path) => new() { Kind = kind, Source = source, Path = path.ToList() };
        GroundedValue Num(decimal value) => new() { Kind = "number", Number = value };
        GroundedValue Text(string value) => new() { Kind = "string", Text = value };
        var value = new GroundedValue { Kind = "compute", Text = "6 * 7" };
        switch (name)
        {
            case "read_transform":
                plan.Operations.Add(Invoke("read", "read"));
                value = new() { Kind = "compute", Text = "value * 2", Members = [new("value", Ref("result", "read", "value"))] };
                break;
            case "nullable_defaults":
                plan.Inputs.Add(new("increment", Optional: true, Default: Num(2)));
                plan.Operations.Add(Invoke("read", "read_optional"));
                value = new() { Kind = "compute", Text = "(value ?? 0) + increment", Members = [new("value", Ref("result", "read", "value")), new("increment", Ref("input", "increment"))] };
                break;
            case "conditional":
                plan.Inputs.Add(new("enabled", new() { Type = "boolean" }));
                plan.Operations.Add(new ChooseGroundedOperation { Id = "choose", Condition = Ref("input", "enabled"), Then = new([Invoke("read", "read")], Ref("result", "read", "value")), Otherwise = new([], Num(0)) });
                value = Ref("result", "choose");
                break;
            case "collections":
                plan.Inputs.Add(new("values", new() { Type = "array", Items = new() { Type = "number" } }));
                plan.Operations.Add(new EachGroundedOperation { Id = "each", Items = Ref("input", "values"), Parallel = true,
                    Body = new([new CallGroundedOperation { Id = "call", Flow = "double", Arguments = [new("value", Ref("item", "each"))] }], Ref("result", "call", "result")) });
                plan.Subflows.Add(new("double", [new("value", new() { Type = "number" })], [new CalculateGroundedOperation { Id = "double", Value = new() { Kind = "compute", Text = "value * 2", Members = [new("value", Ref("input", "value"))] } }], [new("result", Ref("result", "double"))]));
                value = Ref("result", "each");
                break;
            case "protected_cleanup":
                plan.Operations.Add(Invoke("write", "write"));
                plan.Operations.Add(new CleanupGroundedOperation { Id = "finalize", Operations = [Invoke("cleanup", "cleanup")] });
                break;
            case "review_french":
            case "review_distractors":
                plan.Inputs = [new("pr_url"), new("review_text")];
                plan.Operations.Add(Invoke("clone", "clone_repository", new GroundedMember("pr_url", Ref("input", "pr_url"))));
                foreach (var check in new[] { "dependencies", "lint", "unit", "integration" })
                {
                    var operation = Invoke(check, "run_check", new GroundedMember("directory", Ref("result", "clone", "directory")), new GroundedMember("check", Text(check)));
                    if (check != "dependencies") operation.After = ["dependencies"];
                    plan.Operations.Add(operation);
                }
                var review = Invoke("review", "review_changes", new GroundedMember("directory", Ref("result", "clone", "directory")), new GroundedMember("review_text", Ref("input", "review_text")));
                review.After = ["dependencies", "lint", "unit", "integration"]; plan.Operations.Add(review);
                var evaluate = Invoke("evaluate", "evaluate_review", new GroundedMember("directory", Ref("result", "clone", "directory")));
                evaluate.After = ["review"]; plan.Operations.Add(evaluate);
                plan.Operations.Add(Invoke("publish", "publish_review", new GroundedMember("draftId", Ref("result", "evaluate", "draftId"))));
                plan.Operations.Add(new CleanupGroundedOperation { Id = "finalize", Operations = [Invoke("cleanup", "remove_workspace", new GroundedMember("directory", Ref("result", "clone", "directory")))] });
                plan.Outputs.Add(new("review", Ref("result", "evaluate"))); return plan;
        }
        plan.Operations.Add(new CalculateGroundedOperation { Id = "result", Value = value });
        plan.Outputs.Add(new("result", Ref("result", "result")));
        return plan;
    }
    // Test-only scripted stage responses. Independent execution oracles remain separate.
    public static SemanticPlan Semantic(GroundedPlan plan)
    {
        var actions = new List<SemanticAction>();
        foreach (var (operation, path) in GroundedTraversal.Located(plan))
        {
            operation.SemanticAction = "a_" + PlanningGraphCompiler.Fingerprint(path)[..12];
            operation.BusinessOutputs = operation is CleanupGroundedOperation ? [] : [new("value", [])];
            actions.Add(new() { Id = operation.SemanticAction, Kind = operation is InvokeGroundedOperation ? "action" : "calculate",
                Purpose = string.IsNullOrWhiteSpace(operation.Purpose) ? "Perform " + operation.Id : operation.Purpose,
                Outputs = operation is CleanupGroundedOperation ? [] : [new("value", "The required result")] });
        }
        return new() { Summary = plan.Summary, Actions = actions };
    }
    public static LLMResponse FixtureResponse(LLMRequest request, string purpose, GroundedPlan plan)
    {
        var semantic = Semantic(plan);
        if (purpose == "semantic") return new() { Json = SemanticPlanning.Json(semantic) };
        if (purpose == "grounding")
        {
            var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            var ids = context["capabilities"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ToHashSet();
            return new() { Json = new JsonObject { ["decisions"] = new JsonArray(context["actions"]!.AsArray().Select(a =>
            {
                var id = a!["id"]!.GetValue<string>();
                var op = GroundedTraversal.Located(plan).Select(p => p.Operation).OfType<InvokeGroundedOperation>().SingleOrDefault(o => o.SemanticAction == id);
                var matched = op?.Capability is not null && ids.Contains(op.Capability);
                return (JsonNode)new JsonObject { ["actionId"] = id, ["outcome"] = matched ? "matched" : "none_of_the_above", ["reason"] = "Scripted semantic fixture decision.",
                    ["matches"] = matched ? new JsonArray(new JsonObject { ["capabilityId"] = op!.Capability, ["reason"] = "The fixture declares this behavior." }) : new JsonArray() };
            }).ToArray()) } };
        }
        if (purpose == "replan")
        {
            if (request.StructuredOutputSchema?["properties"]?["operations"] is not null)
            {
                var grounded = PlanningJsonTransport.Grounded(plan);
                return new() { Json = PlanningJsonTransport.ModelGrounded(new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, grounded[p.Key]?.DeepClone()))), request.StructuredOutputSchema) };
            }
            if (request.StructuredOutputSchema?["properties"]?["summary"] is not null) return new() { Json = SemanticPlanning.Json(semantic) };
            var json = SemanticPlanning.Json(semantic);
            return new() { Json = new JsonObject { ["actions"] = json["actions"]!.DeepClone(), ["questions"] = json["questions"]!.DeepClone() } };
        }
        return new() { Json = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(plan), request.StructuredOutputSchema!) };
    }
    public sealed class Human(bool answer = true) : IHumanInputProvider
    { public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer }); }
    public sealed class Runtime(string name, WorkflowEngine engine, ILLMClient? live = null) : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _actual = new(engine, (_, _) => Task.CompletedTask);
        private PlanningCatalog? _catalog;
        public async Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _catalog = await _actual.DiscoverAsync(request, ct);
        public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (live is not null) return await live.CallAsync(request, ct);
            _catalog ??= await _actual.DiscoverAsync(new(), ct);
            return FixtureResponse(request, purpose, Intent(name, _catalog));
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => _actual.ValidateScenariosAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
    }
}
