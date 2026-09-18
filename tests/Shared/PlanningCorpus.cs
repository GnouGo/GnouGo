using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Planning.Examples;

public static class PlanningCorpus
{
    public static readonly string[] Names = ["local", "read_transform", "protected_cleanup"];
    public static string Prompt(string name) => name switch
    {
        "local" => "Return a numeric output named result equal to 6 times 7. No inputs or external calls.",
        "read_transform" => "Read the number with the available read tool, double its value, and return a numeric output named result. No inputs.",
        _ => "Perform the write once, then always invoke cleanup once in finally. Return a numeric output named result equal to 42. No inputs."
    };
    public static InMemoryMcpClientFactory Factory(List<string> effects)
    {
        var factory = new InMemoryMcpClientFactory(); var config = new MockMcpServerConfig();
        foreach (var method in new[] { "read", "write", "cleanup" })
        {
            config.Tools.Add(new() { Name = method, Description = method == "read" ? "Read a numeric value" : method,
                EffectKind = method == "read" ? "read" : "write", InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
                OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}"""), ExampleResponse = new JsonObject { ["value"] = 21 } });
            config.ToolHandlers[method] = _ => { lock (effects) effects.Add(method); return new() { Content = new JsonObject { ["value"] = 21 } }; };
        }
        factory.RegisterServer("fixture", config); return factory;
    }
    public static WorkflowIntentPlan Intent(string name, PlanningCatalog catalog)
    {
        var workflow = new WorkflowIntent(); var value = new PlanningValue { Kind = "number", Number = 42 };
        if (name == "read_transform")
        {
            workflow.Steps.Add(new() { Key = "read", CapabilityId = catalog.Capabilities.Single(c => c.Method == "read").Id });
            value = new() { Kind = "compute", Text = "value * 2", Members = [new("value", new() { Kind = "output", Source = "read", Path = ["value"] })] };
        }
        else if (name == "protected_cleanup")
        {
            workflow.Steps.Add(new() { Key = "write", CapabilityId = catalog.Capabilities.Single(c => c.Method == "write").Id });
            workflow.Finally.Add(new() { Key = "cleanup", CapabilityId = catalog.Capabilities.Single(c => c.Method == "cleanup").Id });
        }
        else value = new() { Kind = "compute", Text = "6 * 7" };
        workflow.Steps.Add(new() { Key = "result", Kind = "set", Input = new() { Kind = "object", Members = [new("value", value)] }, OutputSchema = new() { Type = "object", Properties = [new() { Name = "value", Schema = new() { Type = "number" } }] } });
        workflow.Outputs.Add(new() { Name = "result", Schema = new() { Type = "number" }, Value = new() { Kind = "output", Source = "result", Path = ["value"] } });
        return new() { Summary = Prompt(name), Workflows = [workflow] };
    }
    public sealed class Human(bool answer = true) : IHumanInputProvider
    { public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer }); }
    public sealed class Runtime(string name, WorkflowEngine engine, ILLMClient? live = null) : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _actual = new(engine, (_, _) => Task.CompletedTask);
        private PlanningCatalog? _catalog;
        public async Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _catalog = await _actual.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) => live?.CallAsync(request, ct) ?? Task.FromResult(new LLMResponse { Json = IntentResponse.Response(Intent(name, _catalog!), request.StructuredOutputSchema!.AsObject()) });
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => _actual.ValidateScenariosAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
    }
}
