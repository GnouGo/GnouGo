using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

string? Option(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("Missing " + name); }
var command = Option("--live-command");
var keyVaultProvider = Option("--keyvault-provider");
var isLive = command is not null || keyVaultProvider is not null;
ILLMClient? configured = keyVaultProvider is null ? null : new KeyVaultBenchmarkModel(keyVaultProvider,
    Option("--model") ?? throw new ArgumentException("--model is required."), Option("--campaign") ?? throw new ArgumentException("--campaign is required."), Directory.GetCurrentDirectory());
var repetitions = int.Parse(Option("--repetitions") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
if (repetitions is < 1 or > 20) throw new ArgumentException("Repetitions must be between 1 and 20.");
var names = Option("--case") is { } selected ? [selected] : isLive ? PlanningBenchmarkCases.Names : PlanningCorpus.Names;
var results = new JsonArray();
for (var repetition = 1; repetition <= repetitions; repetition++)
foreach (var name in names)
{
    var environment = new PlanningBenchmarkCases.Environment(name);
    var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
    var runtime = new MeasuredRuntime(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask), name, configured ?? (command is null ? null : new CommandModel(command)));
    var planner = new TypedWorkflowPlanner();
    var state = new PlanningSession { Request = new() { TenantId = "benchmark", Name = name, Prompt = PlanningBenchmarkCases.Prompt(name), Generation = new() { Reasoning = "medium", MaxInputTokensPerRequest = 96_000, MaxOutputTokens = 32_768 } } };
    var firstPass = false; var execution = false; var finalReview = false; string? failure = null;
    var clock = Stopwatch.StartNew();
    try
    {
        for (var advance = 0; advance < 40 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
        {
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            if (state.ModelCalls == 1 && state.Graph is not null && state.Diagnostics.All(d => !d.Required) && PlanningHoleEligibility.Find(state.Graph, state.Catalog!).Count == 0) firstPass = true;
        }
        finalReview = state.Status == PlanningStatus.FinalReview;
        if (finalReview && environment.Effects.Count == 0)
        {
            state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
            execution = state.Status == PlanningStatus.Approved;
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
            var variants = name.StartsWith("review_", StringComparison.Ordinal) ? new[] { "nominal", "failure", "incomplete" } : name == "protected_cleanup" ? ["nominal", "failure"] : ["nominal", "alternate"];
            foreach (var variant in variants)
            {
                var sample = new PlanningBenchmarkCases.Environment(name, variant);
                var runner = new WorkflowEngine { McpClientFactory = sample.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
                var result = await runner.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs(name, variant), CancellationToken.None);
                execution &= sample.Verify(result);
            }
        }
    }
    catch (Exception ex) { failure = ex.GetType().Name; }
    var row = new JsonObject
    {
        ["case"] = name, ["repetition"] = repetition, ["mode"] = isLive ? "live" : "fixture", ["first_pass_valid"] = firstPass,
        ["final_review"] = finalReview, ["execution_correct"] = execution,
        ["calls"] = state.ModelCalls, ["repairs"] = state.RepairAttempts, ["input_tokens"] = runtime.InputTokens, ["output_tokens"] = runtime.OutputTokens,
        ["usage_complete"] = runtime.UsageComplete, ["estimated_cost_eur"] = isLive ? runtime.Cost : null,
        ["initial_request_bytes"] = runtime.InitialRequestBytes, ["initial_estimated_input_tokens"] = runtime.InitialEstimatedTokens,
        ["scenarios"] = state.Scenarios.Count, ["elapsed_ms"] = clock.ElapsedMilliseconds,
        ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => d.Code).Distinct().Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()), ["failure"] = failure
    };
    results.Add(row); Console.WriteLine(row.ToJsonString());
    // The adapter enforces the aggregate budget. Uncertain usage stops the campaign.
    if (!runtime.UsageComplete && isLive) goto Complete;
}
Complete:
var total = results.Count;
var complex = results.OfType<JsonObject>().Where(r => r["case"]!.ToString() is "collections" or "protected_cleanup" or "review_french" or "review_distractors").Select(r => r["calls"]!.GetValue<int>()).Order().ToArray();
Console.WriteLine(new JsonObject { ["summary"] = true, ["runs"] = total,
    ["first_pass_valid_rate"] = total == 0 ? 0 : results.Count(r => r!["first_pass_valid"]!.GetValue<bool>()) / (double)total,
    ["final_review_rate"] = total == 0 ? 0 : results.Count(r => r!["final_review"]!.GetValue<bool>()) / (double)total,
    ["execution_correct_rate"] = total == 0 ? 0 : results.Count(r => r!["execution_correct"]!.GetValue<bool>()) / (double)total,
    ["complex_median_calls"] = complex.Length == 0 ? null : complex[(complex.Length - 1) / 2],
    ["complex_p75_calls"] = complex.Length == 0 ? null : complex[(int)Math.Ceiling(complex.Length * .75) - 1]
}.ToJsonString());
if (results.Any(r => !r!["execution_correct"]!.GetValue<bool>())) Environment.ExitCode = 1;

sealed class MeasuredRuntime(IPlanningRuntime inner, string name, ILLMClient? live) : IPlanningRuntime
{
    private PlanningCatalog? _catalog;
    public long InputTokens, OutputTokens, InitialRequestBytes, InitialEstimatedTokens;
    public decimal Cost;
    public bool UsageComplete = true;
    public async Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _catalog = await inner.DiscoverAsync(request, ct);
    public async Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        if (InitialRequestBytes == 0)
        {
            InitialRequestBytes = Encoding.UTF8.GetByteCount(request.Prompt ?? "") + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString());
            InitialEstimatedTokens = PlanningJsonTransport.EstimateInputTokens(request.Prompt ?? "", request.StructuredOutputSchema!.AsObject());
        }
        if (live is null) return new() { Json = PlanningJsonTransport.Intent(PlanningCorpus.Intent(name, _catalog!)) };
        try
        {
            var response = await live.CallAsync(request, ct);
            long? Tokens(params string[] keys) => keys.Select(k => response.Usage?[k]).OfType<JsonValue>().Select(v => v.TryGetValue<long>(out var n) ? (long?)n : null).FirstOrDefault(n => n is not null);
            var input = Tokens("input_tokens", "prompt_tokens", "inputTokens"); var output = Tokens("output_tokens", "completion_tokens", "outputTokens");
            UsageComplete &= input is not null && output is not null;
            InputTokens += input ?? 0; OutputTokens += output ?? 0;
            if (response.Usage?["benchmark_cost_eur"] is JsonValue cost && cost.TryGetValue<decimal>(out var amount)) Cost += amount;
            else UsageComplete = false;
            return response;
        }
        catch { UsageComplete = false; throw; }
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => inner.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => inner.ValidateScenariosAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => inner.ValidateCatalogAsync(catalog, ct);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
}

sealed class CommandModel(string executable) : ILLMClient
{
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("Model adapter did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest).AsMemory(), deadline.Token);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token); await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Model adapter exited with code " + process.ExitCode);
            return JsonSerializer.Deserialize(await output, PlanningJsonContext.Default.LLMResponse) ?? throw new InvalidOperationException("Empty model receipt.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
