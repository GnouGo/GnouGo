using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
var rows = new List<object>();
foreach (var name in new[] { "local", "read_transform", "conditional", "collections", "protected_cleanup" })
for (var repetition = -1; repetition < 3; repetition++) {
    var env = new PlanningBenchmarkCases.Environment(name);
    var engine = new WorkflowEngine { McpClientFactory = env.Factory(), HumanInputProvider = new PlanningCorpus.Human(true) };
    var runtime = new MeasuredRuntime(engine, name);
    var state = new PlanningSession { Request = new() { TenantId = "measurement", Prompt = PlanningCorpus.Prompt(name) } };
    var clock = Stopwatch.StartNew();
    for (var i = 0; i < 8 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, default);
    var planningMs = clock.Elapsed.TotalMilliseconds;
    if (state.Status != PlanningStatus.FinalReview) throw new Exception(JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
    PlanningArtifactApproval.Verify(state);
    var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
    clock.Restart(); var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, "nominal"), default);
    if (!env.Verify(result)) throw new Exception("Execution oracle failed: " + name);
    if (repetition >= 0) rows.Add(new { name, repetition, calls = state.ModelCalls, repairs = state.ReplanAttempts, discoveryReads = runtime.Reads,
        schemaBytes = runtime.Calls.Sum(c => System.Text.Encoding.UTF8.GetByteCount(c.StructuredOutputSchema!.ToJsonString())),
        inputTokenEstimate = runtime.Calls.Sum(c => PlanningJsonTransport.EstimateInputTokens(c.Prompt, c.StructuredOutputSchema!.AsObject())), planningMs, executionMs = clock.Elapsed.TotalMilliseconds, success = result.Success, oracleCorrect = env.Verify(result) });
}
Console.WriteLine(JsonSerializer.Serialize(rows));
sealed class MeasuredRuntime(WorkflowEngine engine, string name) : IPlanningRuntime {
    readonly WorkflowPlanningRuntime actual = new(engine, (_, _) => Task.CompletedTask);
    PlanningCatalog? catalog;
    public readonly List<LLMRequest> Calls = [];
    public int Reads;
    public ICapabilityCatalog Capabilities => actual.Capabilities;
    public async Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) {
        catalog = await actual.DiscoverAsync(request, ct);
        foreach (var source in await Capabilities.ListSourcesAsync(ct)) {
            string? cursor = null; do { var page = await Capabilities.ListAsync(source.Id, cursor, ct); Reads++;
                foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await Capabilities.ResolveAsync(summary, ct)); cursor = page.NextCursor;
            } while (cursor is not null);
        }
        return catalog;
    }
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) {
        Calls.Add(request); var plan = PlanningCorpus.Tasks(name, catalog!);
        var proposal = new PlanningProposal { Requirements = PlanningCorpus.Requirements(name), Plan = plan };
        return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => actual.ValidateCatalogAsync(catalog, ct);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
}
