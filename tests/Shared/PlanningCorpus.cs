using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Planning.Examples;

public static class PlanningCorpus
{
    public static readonly string[] Names = PlanningBenchmarkCases.Names;
    public static string Prompt(string name) => PlanningBenchmarkCases.Prompt(name);
    public static WorkflowIntentPlan Intent(string name, PlanningCatalog catalog)
    {
        var plan = new WorkflowIntentPlan { Summary = Prompt(name) };
        InvokeIntentOperation Invoke(string id, string method, params IntentMember[] args) => new() { Id = id, Capability = catalog.Capabilities.Single(c => c.Method == method).Id, Arguments = args.ToList() };
        IntentValue Ref(string kind, string source, params string[] path) => new() { Kind = kind, Source = source, Path = path.ToList() };
        IntentValue Num(decimal value) => new() { Kind = "number", Number = value };
        IntentValue Text(string value) => new() { Kind = "string", Text = value };
        var value = new IntentValue { Kind = "compute", Text = "6 * 7" };
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
                plan.Operations.Add(new ChooseIntentOperation { Id = "choose", Condition = Ref("input", "enabled"), Then = new([Invoke("read", "read")], Ref("result", "read", "value")), Otherwise = new([], Num(0)) });
                value = Ref("result", "choose");
                break;
            case "collections":
                plan.Inputs.Add(new("values", new() { Type = "array", Items = new() { Type = "number" } }));
                plan.Operations.Add(new EachIntentOperation { Id = "each", Items = Ref("input", "values"), Parallel = true,
                    Body = new([new CallIntentOperation { Id = "call", Flow = "double", Arguments = [new("value", Ref("item", "each"))] }], Ref("result", "call", "result")) });
                plan.Subflows.Add(new("double", [new("value")], [new CalculateIntentOperation { Id = "double", Value = new() { Kind = "compute", Text = "value * 2", Members = [new("value", Ref("input", "value"))] } }], [new("result", Ref("result", "double"))]));
                value = Ref("result", "each");
                break;
            case "protected_cleanup":
                plan.Operations.Add(Invoke("write", "write"));
                plan.Operations.Add(new CleanupIntentOperation { Id = "finalize", Operations = [Invoke("cleanup", "cleanup")] });
                break;
            case "review_french":
            case "review_distractors":
                plan.Inputs = [new("pr_url"), new("review_text")];
                plan.Operations.Add(Invoke("clone", "clone_repository", new IntentMember("pr_url", Ref("input", "pr_url"))));
                foreach (var check in new[] { "dependencies", "lint", "unit", "integration" })
                {
                    var operation = Invoke(check, "run_check", new IntentMember("directory", Ref("result", "clone", "directory")), new IntentMember("check", Text(check)));
                    if (check != "dependencies") operation.After = ["dependencies"];
                    plan.Operations.Add(operation);
                }
                var review = Invoke("review", "review_changes", new IntentMember("directory", Ref("result", "clone", "directory")), new IntentMember("review_text", Ref("input", "review_text")));
                review.After = ["dependencies", "lint", "unit", "integration"]; plan.Operations.Add(review);
                var evaluate = Invoke("evaluate", "evaluate_review", new IntentMember("directory", Ref("result", "clone", "directory")));
                evaluate.After = ["review"]; plan.Operations.Add(evaluate);
                plan.Operations.Add(Invoke("publish", "publish_review", new IntentMember("draftId", Ref("result", "evaluate", "draftId"))));
                plan.Operations.Add(new CleanupIntentOperation { Id = "finalize", Operations = [Invoke("cleanup", "remove_workspace", new IntentMember("directory", Ref("result", "clone", "directory")))] });
                plan.Outputs.Add(new("review", Ref("result", "evaluate"))); return plan;
        }
        plan.Operations.Add(new CalculateIntentOperation { Id = "result", Value = value });
        plan.Outputs.Add(new("result", Ref("result", "result")));
        return plan;
    }
    public sealed class Human(bool answer = true) : IHumanInputProvider
    { public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer }); }
    public sealed class Runtime(string name, WorkflowEngine engine, ILLMClient? live = null) : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _actual = new(engine, (_, _) => Task.CompletedTask);
        private PlanningCatalog? _catalog;
        public async Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _catalog = await _actual.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) => live?.CallAsync(request, ct) ?? Task.FromResult(new LLMResponse { Json = PlanningJsonTransport.Intent(Intent(name, _catalog!)) });
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => _actual.ValidateScenariosAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
    }
}
