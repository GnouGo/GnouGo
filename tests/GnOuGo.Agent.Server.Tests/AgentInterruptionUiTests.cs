using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AgentInterruptionUiTests : BunitContext
{
    [Fact]
    public void RetainedReceiptPreservesBudgetRefusalWithoutInventingCessationEvidence()
    {
        var original = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentInterruption", "retained-execution.json")))!;
        var adapter = original["adapter"]!;
        Assert.Equal("needs_reconciliation", adapter["status"]!.ToString());
        Assert.Equal(4, adapter["modelCalls"]!.GetValue<int>());
        Assert.Equal(167402, adapter["chargedTokens"]!.GetValue<int>());
        var result = JsonSerializer.Deserialize(adapter["result"], AgentTaskJsonContext.Default.AgentTaskResult)!;
        Assert.Equal("reserved_upper_bound", result.Usage.Metering); Assert.Null(result.Failure);
        Assert.Contains("budget is exhausted", result.Message);
        var invocation = Assert.Single(original["invocations"]!.AsArray(), i => i!["stepType"]!.ToString() == "agent.run")!;
        Assert.False(invocation["externalCompletionObserved"]!.GetValue<bool>()); Assert.Null(invocation["observation"]);
        Assert.Equal(200000, invocation["budget"]!["max_total_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunInspectionShowsSafeBudgetCauseAndCannotDispatchOrLeakAnotherTenant(bool otherTenant)
    {
        var store = new InMemoryWorkflowRunStore(); var ct = Xunit.TestContext.Current.CancellationToken;
        var result = new AgentTaskResult("needs_reconciliation", null, [], [], new(4, 167402, 324299) { Metering = "reserved_upper_bound" })
        { Failure = new() { Code = "AGENT_BUDGET_EXHAUSTED", Message = "The next request exceeds the approved allowance." }, Message = "PRIVATE_ASSISTANT_CONTENT" };
        var run = new WorkflowRun { TenantId = "owner", RunId = "interrupted", Limits = new() { TenantId = "owner", RunId = "interrupted" }, Status = WorkflowRunStatus.NeedsReconciliation };
        run.Invocations["/workflow/main/step/child/workflow/nested/step/agent"] = new()
        {
            Id = "/workflow/main/step/child/workflow/nested/step/agent", StepType = "agent.run", Recovery = StepRecovery.External,
            Status = "needs_reconciliation", DispatchedAt = DateTimeOffset.UtcNow,
            Observation = JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult),
            ResolvedInput = JsonNode.Parse("""{"objective":"Inspect fixture","budget":{"max_model_calls":20,"max_total_tokens":200000}}""")
        };
        await store.CreateAsync(run, ct);
        // Read-only rendering has no runtime factory, inference or effect dependencies available.
        Services.AddSingleton(new WorkflowRunService(store, null!, null!, null!, null!, null!, null!,
            Options.Create(new OpenTelemetrySettings { TenantId = otherTenant ? "other" : "owner" }), NullLogger<WorkflowRunService>.Instance));
        var cut = Render<RunsPage>(p => p.Add(c => c.RunId, "interrupted"));
        cut.WaitForAssertion(() =>
        {
            if (otherTenant) { Assert.Contains("No execution was found", cut.Markup); Assert.DoesNotContain("AGENT_BUDGET_EXHAUSTED", cut.Markup); return; }
            Assert.Contains("AGENT_BUDGET_EXHAUSTED", cut.Markup);
            Assert.Contains("Charged token reservations: 167402 / 200000", cut.Markup);
            Assert.Contains("Agent calls: 4 / 20", cut.Markup);
            Assert.Contains("not measured usage", cut.Markup);
            Assert.Contains("External cessation: not verified", cut.Markup);
            Assert.Contains("Execution and cleanup wait for reconciliation", cut.Markup);
            Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Resume this revision").HasAttribute("disabled"));
            Assert.Contains("Inspect agent receipt", cut.Markup); Assert.Contains("Record confirmed stop", cut.Markup);
        });
        Assert.DoesNotContain("PRIVATE_ASSISTANT_CONTENT", cut.Markup);
        Assert.Equal(run.Revision, (await store.ReadAsync("owner", "interrupted", ct))!.Revision);
        await DisposeComponentsAsync();
    }
}
