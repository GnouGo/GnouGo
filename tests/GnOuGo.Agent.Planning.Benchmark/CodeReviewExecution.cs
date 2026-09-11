using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class CodeReviewExecution
{
    internal static async Task RunAsync(PlanningSnapshot state, JsonArray discovery, LLMRuntimeOptionsStore options,
        IKeyVaultRuntimeConfigStore vault, IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records,
        IExchangeRateProvider rates, string inputPort, string instructionPort, CancellationToken ct)
    {
        if (state.Status is not (PlanningStatus.FinalReview or PlanningStatus.Approved) || state.Graph is null || state.Preparation is null || state.ArtifactHash is null)
            throw new InvalidOperationException("Independent execution requires a current final-review artifact.");
        var yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Preparation);
        if (yaml != state.Yaml || PlanningGraphCompiler.Fingerprint(yaml) != state.ArtifactHash || yaml != new PlanningGraphCompiler().Compile(state.Graph, state.Preparation))
            throw new InvalidOperationException("The final artifact does not match deterministic lowering.");
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var workflow = compiled.Workflows[compiled.Entrypoint!];
        var fixtureHash = CodeReviewExecutionFixture.Fingerprint;
        var catalogHash = PlanningGraphCompiler.Fingerprint(discovery.ToJsonString());
        var caseReports = new JsonArray();
        foreach (var pull in new[] { 449, 510 })
        foreach (var variant in new[] { "passing", "mixed", "empty", "boundary", "rejected", "omitted_defaults", "invalid_input", "setup_failure", "execution_failure" })
        {
            var fixture = new CodeReviewExecutionFixture(pull, variant);
            var factory = new SecureWorkflowRuntimeFactory(options, vault,
                mcpClientFactoryOverride: new FrozenCatalog(discovery, fixture.Invoke), humanInputProvider: fixture);
            await using var runtime = await factory.CreateAsync(ct);
            // A case is revision/hash-owned. Restart reuses its journal and budget;
            // successful or unverifiable dispatches never receive a fresh identity.
            var owner = state.Request.SessionId + ":execution:" + state.ArtifactHash + ":" + fixtureHash + ":" + catalogHash + ":" + pull + ":" + variant;
            var receipt = await records.GetAsync(PlanningBudgetSink.Collection, state.Request.TenantId, owner, EfPlanningSessionStore.Author, ct);
            var initial = receipt is null ? null : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            var settings = new TypedWorkflowPlanningSettings();
            var budget = new LLMUsageBudgetScope(new LLMUsageBudgetLimits { MaxCalls = settings.MaxModelCalls, MaxTotalTokens = settings.MaxTotalTokens,
                MaxEstimatedCost = new WorkflowPlanningBudgetSettings().CreateLimit() }, initial,
                sink: new PlanningBudgetSink(records, state.Request.TenantId, owner), exchangeRateProvider: rates);
            var estimator = new ModelMetadataUsageCostEstimator(runtime.Options);
            var journal = new PlanningModelJournal(runtime.LlmClient, contexts, records, state.Request.TenantId, owner, budget, estimator, state.Request.Generation);
            var engine = new WorkflowEngine
            {
                LLMClient = new ExecutionRequests(journal, owner, state.Request.Generation), McpClientFactory = runtime.McpClientFactory,
                HumanInputProvider = fixture, ModelUsageCostEstimator = estimator, ExchangeRateProvider = rates,
                LlmDefaults = new() { Provider = runtime.Options.DefaultProvider, Model = runtime.Options.DefaultModel },
                Limits = new() { LogStepContent = false, TenantId = state.Request.TenantId, RunId = owner }
            };
            var inputs = new JsonObject { [inputPort] = variant == "invalid_input" ? JsonValue.Create(42) : JsonValue.Create(fixture.Url), [instructionPort] = CodeReviewExecutionFixture.Instructions };
            // All optional public inputs are deliberately omitted, exercising the
            // runtime's declared defaults without inventing benchmark overrides.
            bool success; string? errorCode, errorMessage; JsonNode? outputs;
            try
            {
                var result = await engine.ExecuteAsync(workflow, inputs, ct);
                (success, errorCode, errorMessage, outputs) = (result.Success, result.Error?.Code, result.Error?.Message, result.Outputs);
            }
            catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException error) when (variant == "invalid_input")
            { (success, errorCode, errorMessage, outputs) = (false, error.Code, error.Message, null); }
            var report = new JsonObject { ["case"] = pull + ":" + variant, ["artifactHash"] = state.ArtifactHash, ["success"] = success,
                ["fixtureHash"] = fixtureHash, ["catalogHash"] = catalogHash,
                ["errorCode"] = errorCode, ["usage"] = JsonSerializer.SerializeToNode(budget.Snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot) };
            // Retain failures too, before assertions; raw runtime/model evidence stays encrypted.
            await records.UpsertAsync("agent-planning-benchmark-execution-v4", state.Request.TenantId, owner,
                new JsonObject { ["report"] = report.DeepClone(), ["outputs"] = outputs?.DeepClone(), ["error"] = errorMessage }.ToJsonString(), EfPlanningSessionStore.Author, ct);
            Console.WriteLine(report.ToJsonString());
            fixture.AssertComplete(success, variant == "invalid_input" ? errorCode : errorMessage, outputs);
            caseReports.Add(report);
        }
        await records.UpsertAsync("agent-planning-benchmark-validation-v4", state.Request.TenantId, state.Request.SessionId + ":" + state.ArtifactHash,
            caseReports.ToJsonString(), EfPlanningSessionStore.Author, ct);
    }

    private sealed class ExecutionRequests(ILLMClient journal, string owner, PlanningGenerationOptions generation) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            PlanningGenerationPolicy.Apply(request, generation);
            var estimate = (Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema?.ToJsonString() ?? "{}") + 2) / 3 + 256;
            if (estimate > generation.MaxInputTokensPerRequest) throw new InvalidOperationException("Independent execution request exceeds the unchanged input ceiling.");
            request.ClientRequestId = null;
            var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            request.ClientRequestId = owner + ":" + hash;
            return journal.CallAsync(request, ct);
        }
    }
}
