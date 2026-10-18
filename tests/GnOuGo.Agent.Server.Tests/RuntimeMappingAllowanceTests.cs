using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RuntimeMappingAllowanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    [InlineData("2147483648")]
    [InlineData("")]
    public void InvalidAllowanceFailsAtNormalHostStartup(string setting)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BudgetHost.Create(RuntimeExecutionBudgetTests.AdaptiveYaml,
            new RuntimeExecutionBudgetTests.BudgetModel(), new FakeMcpClientFactory(), ["--Flow:Execution:MaxMappingInputTokens=" + setting]));
        Assert.Contains("Flow:Execution:MaxMappingInputTokens", error.Message);
    }

    [Fact]
    public void NestedAllowanceIsRejected()
        => Assert.Throws<InvalidOperationException>(() => BudgetHost.Create(RuntimeExecutionBudgetTests.AdaptiveYaml,
            new RuntimeExecutionBudgetTests.BudgetModel(), new FakeMcpClientFactory(), ["--Flow:Execution:MaxMappingInputTokens:Nested=4"]));

    [Theory]
    [InlineData(false, 96000)]
    [InlineData(true, null)]
    public async Task ShippedConfigurationAndHistoricalOmissionUseSeparatePolicies(bool omitSettings, int? expected)
    {
        await using var host = BudgetHost.Create(RuntimeExecutionBudgetTests.AdaptiveYaml,
            new RuntimeExecutionBudgetTests.BudgetModel(), new FakeMcpClientFactory(), [], withoutAppSettings: omitSettings);
        Assert.Equal(expected, host.App.Services.GetRequiredService<IOptions<WorkflowExecutionSettings>>().Value.MaxMappingInputTokens);
    }

    [Fact]
    public void EnvironmentAndCommandLineOverridesPreserveNormalPrecedence()
    {
        var prefix = "GNouGoMappingTest_" + Guid.NewGuid().ToString("N") + "_";
        var key = prefix + "Flow__Execution__MaxMappingInputTokens";
        Environment.SetEnvironmentVariable(key, "48000");
        try
        {
            var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Flow:Execution:MaxMappingInputTokens"] = "96000" }).AddEnvironmentVariables(prefix);
            using (var environment = (ConfigurationRoot)builder.Build())
                Assert.Equal(48000, WorkflowExecutionSettings.Read(environment).MaxMappingInputTokens);
            using var commandLine = (ConfigurationRoot)builder.AddCommandLine(["--Flow:Execution:MaxMappingInputTokens=64000"]).Build();
            Assert.Equal(64000, WorkflowExecutionSettings.Read(commandLine).MaxMappingInputTokens);
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public async Task ExplicitOverrideReachesNewRunAndStoredValueSurvivesChangedConfiguration()
    {
        var model = new RuntimeExecutionBudgetTests.BudgetModel { InputAllowance = 96000 };
        var effects = 0;
        var transport = new FakeMcpClientFactory(new FakeMcpSession("fixture").OnTool("effect", (_, _) =>
        { effects++; return Task.FromResult(new McpCallResult { Content = new JsonObject() }); }));
        var store = new InMemoryWorkflowRunStore();
        var yaml = RuntimeExecutionBudgetTests.AdaptiveYaml.Replace("{records: [{label: first}, {label: second}, {label: first}]}",
            "{records: '${data.inputs.records}'}", StringComparison.Ordinal);
        var label = new string('x', 24000) + "é漢🙂\"\\";
        await using (var host = BudgetHost.Create(yaml, model, transport, ["--Flow:Execution:MaxMappingInputTokens=80000"], store))
        {
            var events = await host.ExecuteAsync("mapping-policy", Ct, new JsonObject { ["records"] = new JsonArray(new JsonObject { ["label"] = label }) });
            Assert.DoesNotContain(events, e => e.Type == "error");
        }
        var saved = (await store.ReadAsync("local", "mapping-policy", Ct))!;
        Assert.Equal(80000, saved.Limits.MaxMappingInputTokens); Assert.Equal(1, model.Calls); Assert.Equal(1, effects);
        Assert.Equal(label, saved.Result!.Outputs!["labels"]![0]!.GetValue<string>());
        await using var restarted = BudgetHost.Create(yaml, model, transport, ["--Flow:Execution:MaxMappingInputTokens=12000"], store);
        var recovered = await restarted.App.Services.GetRequiredService<WorkflowRunService>().CommandAsync("mapping-policy", "resume", new(saved.Revision), Ct);
        Assert.True(recovered.Result!.Success); Assert.Equal(80000, recovered.Limits.MaxMappingInputTokens);
        Assert.Equal(1, model.Calls); Assert.Equal(1, effects);
    }

    [Fact]
    public void MappingFailurePresentationIncludesBoundedMeasurementsOnly()
    {
        var presentation = WorkflowFailureFormatter.Format(new()
        {
            Code = "CONTRACT_UNSATISFIED", Message = "No complete initial mapping example fits the safe request allowance.",
            Details = new JsonObject
            {
                ["classification"] = "mapping_request_admission", ["host_limit"] = 12000, ["model_limit"] = 96000,
                ["effective_limit"] = 12000, ["smallest_complete_example_request_bound"] = 33000L,
                ["empty_request_bound"] = 9000L, ["framing_allowance"] = 4096, ["examined_items"] = 54,
                ["reason"] = "no_initial_example_fits", ["limiting_factor"] = "host", ["dispatch_status"] = "not_started",
                ["observation"] = "PRIVATE_OBSERVATION"
            }
        });
        Assert.Contains("Host limit: 12000", presentation.UserMessage);
        Assert.Contains("Smallest complete example request tried: 33000", presentation.UserMessage);
        Assert.Contains("Flow:Execution:MaxMappingInputTokens", presentation.UserMessage);
        Assert.Contains("not_started", presentation.UserMessage); Assert.DoesNotContain("PRIVATE_OBSERVATION", presentation.TraceDetails);
    }

    [Fact]
    public void RequestAllowanceIsDistinctFromCumulativeBudgetAndUsesTheRecordedMeasurement()
    {
        var presentation = WorkflowFailureFormatter.Format(new()
        {
            Code = "LLM_BUDGET_EXCEEDED", Message = "Mapping inference stopped.",
            Details = new JsonObject { ["classification"] = "request_input_allowance", ["limit_kind"] = "input_tokens",
                ["measurement"] = "conservative_token_estimate", ["limit"] = 96000, ["current"] = 707090,
                ["dispatch_status"] = "not_started", ["failed_step_id"] = "extract", ["script"] = "PRIVATE_SCRIPT",
                ["observation"] = "PRIVATE_OBSERVATION" }
        });
        Assert.Contains("Request input allowance", presentation.UserMessage);
        Assert.Contains("conservative_token_estimate", presentation.UserMessage);
        Assert.Contains("Limit: 96000", presentation.UserMessage);
        Assert.Contains("Measured request: 707090", presentation.UserMessage);
        Assert.Contains("Step: extract", presentation.UserMessage);
        Assert.Contains("not_started", presentation.UserMessage);
        Assert.DoesNotContain("Runtime execution budget:", presentation.UserMessage);
        Assert.DoesNotContain("UTF-8 bytes", presentation.UserMessage);
        Assert.DoesNotContain("PRIVATE_", presentation.TraceDetails);
        var cumulative = WorkflowFailureFormatter.Format(new() { Code = "LLM_BUDGET_EXCEEDED", Message = "Budget exhausted.",
            Details = new JsonObject { ["classification"] = "budget_exceeded", ["limit_kind"] = "calls", ["limit"] = 2, ["current"] = 3 } });
        Assert.Contains("Runtime execution budget:", cumulative.UserMessage);
        Assert.DoesNotContain("Request input allowance", cumulative.UserMessage);
    }
}
