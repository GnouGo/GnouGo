using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Configuration;

/// <summary>Protected operator preference. Each new session/run captures its own immutable policy.</summary>
public sealed class ApplicationLlmBudgetPolicy(IKeyVaultRecordStore records, IConfiguration configuration,
    IOptions<OpenTelemetrySettings> telemetry, IOptions<WorkflowPlanningBudgetSettings> planning)
{
    private const string Collection = "agent-application-llm-budget-v1";
    private const string Key = "operator-preference";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Selection(string Mode, string Origin, string FinancialForecastMode);
    private volatile Selection _current = new(ValidateMode(configuration["ApplicationLlmBudget:Mode"] ?? "unlimited"), "host_default", ValidateForecastMode(configuration["ApplicationLlmBudget:FinancialForecastMode"] ?? "strict"));
    public string Mode => _current.Mode;
    public string FinancialForecastMode => _current.FinancialForecastMode;
    internal MonetaryAmount PlanningAllowance => new(planning.Value.Amount, planning.Value.Currency);
    public JsonObject Capture()
    {
        var selected = _current;
        return new() { ["version"] = 1, ["mode"] = selected.Mode, ["origin"] = selected.Origin, ["currency"] = planning.Value.Currency, ["financialForecastMode"] = selected.FinancialForecastMode };
    }
    public JsonObject Describe() => new() { ["policy"] = Capture(),
        ["planningLimits"] = new JsonObject { ["amount"] = planning.Value.Amount, ["currency"] = planning.Value.Currency },
        ["executionLimits"] = System.Text.Json.JsonSerializer.SerializeToNode(ConfiguredExecutionBudgetFactory.Read(configuration),
            WorkflowRunJsonContext.Default.LLMUsageBudgetLimits) };
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var saved = await records.GetAsync(Collection, WorkflowExecutionTenant.Resolve(telemetry), Key, EfPlanningSessionStore.Author, ct);
            if (saved is null) return;
            var value = JsonNode.Parse(saved.Value) as JsonObject;
            if (value?["version"]?.GetValue<int>() != 1) throw new InvalidOperationException("The saved application LLM budget policy is invalid.");
            _current = new(ValidateMode(value["mode"]!.GetValue<string>()), "operator_preference",
                ValidateForecastMode(value["financialForecastMode"]?.GetValue<string>() ?? configuration["ApplicationLlmBudget:FinancialForecastMode"] ?? "strict"));
        }
        finally { _gate.Release(); }
    }
    public async Task SetAsync(string mode, LocalOperatorAccess access, CancellationToken ct, string? financialForecastMode = null)
    {
        if (!await access.CanInspectAsync()) throw new UnauthorizedAccessException("Local operator access is required to change application budgets.");
        mode = ValidateMode(mode);
        await _gate.WaitAsync(ct);
        try
        {
            var forecast = financialForecastMode is null ? _current.FinancialForecastMode : ValidateForecastMode(financialForecastMode);
            await records.UpsertAsync(Collection, WorkflowExecutionTenant.Resolve(telemetry), Key,
                new JsonObject { ["version"] = 1, ["mode"] = mode, ["financialForecastMode"] = forecast }.ToJsonString(), EfPlanningSessionStore.Author, ct);
            _current = new(mode, "operator_preference", forecast);
        }
        finally { _gate.Release(); }
    }
    private static string ValidateForecastMode(string mode) => mode is "strict" or "experimental_warning" ? mode
        : throw new ArgumentException("ApplicationLlmBudget:FinancialForecastMode must be strict or experimental_warning.");
    private static string ValidateMode(string mode) => mode is "unlimited" or "limited" ? mode
        : throw new ArgumentException("ApplicationLlmBudget:Mode must be unlimited or limited.");
}
