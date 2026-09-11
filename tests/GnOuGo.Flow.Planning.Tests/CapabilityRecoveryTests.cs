using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Inspect the supplied resource and execute available checks.")]
    [InlineData("Inspecter la ressource fournie et exécuter les contrôles disponibles.")]
    public async Task MixedCapabilityFindings_PauseWithActualDetails_AndRetryRetainsAnswers(string prompt)
    {
        var state = new PlanningSnapshot { Request = new() { TenantId = "tenant", Prompt = prompt }, Intent = new() { Checked = true } };
        state.Intent.Answers.Add(new("Publication choice", new JsonObject { ["choice"] = "retain_confirmation" })); state.Intent.Forms = 1; state.Intent.Questions = 1;
        var runtime = new TypedPlannerTests.FakeRuntime { OnPrepare = _ => throw Failure() };
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = 0 }, runtime, Ct);
        Assert.Equal(PlanningStatus.Recovery, result.Status);
        Assert.Equal(PlanningPhase.Capabilities, result.CurrentPhase);
        Assert.Null(result.Outcome);
        Assert.Null(result.Intent.Question);
        Assert.Null(result.Graph);
        Assert.Null(result.Preparation);
        Assert.Equal(4, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, d => d.Code == "conditional_decision_source_unavailable" && d.Message.Contains("observe_content") && d.Message.Contains("runtime observation"));
        Assert.Contains(result.Diagnostics, d => !d.Required && d.Location.Contains("contract_issues"));
        Assert.All(result.Diagnostics, d => Assert.Equal(PlanningPhase.Capabilities, d.ValidationStage));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("PRIVATE_TRANSPORT_PAYLOAD"));
        Assert.Empty(runtime.Requests);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        restored.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var waiting = await new TypedWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, Ct);
        Assert.Equal(restored.Revision, waiting.Revision);
        Assert.Equal(1, runtime.PreparationCalls);
        var retry = await new TypedWorkflowPlanner().AdvanceAsync(waiting, new() { Kind = "retry", ExpectedRevision = waiting.Revision }, runtime, Ct);
        Assert.Empty(retry.Diagnostics);
        Assert.Equal(4, Assert.Single(retry.Intent.History).Diagnostics.Count);
        Assert.Single(retry.Intent.Answers);
        Assert.True(retry.Intent.Checked);
        Assert.True(retry.HumanWaitMilliseconds >= 3_600_000);
        Assert.Equal(1, retry.Intent.Forms);
        Assert.Equal(1, runtime.PreparationCalls);
        await Assert.ThrowsAsync<PlanningConflictException>(() => new TypedWorkflowPlanner().AdvanceAsync(retry,
            new() { Kind = "edit_intent", ExpectedRevision = waiting.Revision, Text = "stale" }, runtime, Ct));
    }

    [Fact]
    public async Task ConfirmedUnavailableCapability_RemainsUnsupported()
    {
        var state = new PlanningSnapshot { Request = new() { TenantId = "tenant", Prompt = "Requested operation" }, Intent = new() { Checked = true } };
        var runtime = new TypedPlannerTests.FakeRuntime { OnPrepare = _ => throw new WorkflowRuntimeException(ErrorCodes.CapabilityPreflightUnavailable, "No declared producer exists.") };
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Unsupported, result.Status);
        Assert.Equal("unsupported", result.Outcome);
        Assert.Equal("No declared producer exists.", Assert.Single(result.Diagnostics).Message);
    }

    internal static WorkflowRuntimeException Failure() => new(ErrorCodes.CapabilityPreflightInferenceFailed,
        "Capability matching remained ambiguous or invalid after one bounded repair attempt.", details: new JsonObject
        {
            ["raw"] = "PRIVATE_TRANSPORT_PAYLOAD",
            ["matching_issues"] = new JsonArray(
                new JsonObject { ["operation_id"] = "execute_checks", ["status"] = "ambiguous", ["reason"] = "The runtime resource type is unresolved." },
                new JsonObject
                {
                    ["operation_id"] = "execute_when_present",
                    ["status"] = "contract_gap",
                    ["reason_code"] = "conditional_decision_source_unavailable",
                    ["reason"] = "The resource path supplies no content observation.",
                    ["decision_operation_id"] = "observe_content",
                    ["hint"] = "Declare the missing runtime observation."
                }),
            ["contract_issues"] = new JsonArray(new JsonObject { ["code"] = "CAPABILITY_CONTRACT_NOTE", ["reason"] = "Declared output contract requires review.", ["required"] = false })
        });
}
