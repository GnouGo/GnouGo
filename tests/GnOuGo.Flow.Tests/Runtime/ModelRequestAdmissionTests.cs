using System.Text.Json.Nodes;
using Xunit;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class ModelRequestAdmissionTests
{
    [Fact]
    public async Task TrustedAdvisoryModeWarnsForEstimatesWithoutChangingWireAllocationOrCapabilities()
    {
        var request = Request(); request.Prompt = new string('x', 9000);
        await ModelRequestAdmission.ValidateAsync(request, new Resolver(true, 10), 1, "judge",
            TestContext.Current.CancellationToken, advisoryTokens: true);
        Assert.Equal(32768, request.MaxTokens);
        Assert.Equal("medium", request.Reasoning);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ModelRequestAdmission.ValidateAsync(request,
            new Resolver(false, 10), 1, "judge", TestContext.Current.CancellationToken, advisoryTokens: true));
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ModelRequestAdmission.ValidateAsync(request,
            new Resolver(true, null), 1, "judge", TestContext.Current.CancellationToken, advisoryTokens: true));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public async Task OutputReservationLeavingNoInputIsIncompatibleEvenWithAdvisoryThresholds(bool advisory, int remaining)
    {
        var request = Request();
        var ex = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ModelRequestAdmission.ValidateAsync(request,
            new Resolver(true, remaining), 32000, "judge", TestContext.Current.CancellationToken, advisoryTokens: advisory));
        Assert.Equal("MODEL_REQUEST_INCOMPATIBLE", ex.Code);
        Assert.Equal("not_started", ex.Details!["dispatch_status"]!.ToString());
        Assert.Equal(32768, request.MaxTokens);
    }

    [Theory]
    [InlineData(null, 10000, "LLM_BUDGET_UNVERIFIABLE")]
    [InlineData(false, 10000, "MODEL_REQUEST_INCOMPATIBLE")]
    [InlineData(true, null, "LLM_BUDGET_UNVERIFIABLE")]
    [InlineData(true, 10, "LLM_BUDGET_EXCEEDED")]
    public async Task CapabilityAndCompleteRequestAdmissionFailBeforeDispatch(bool? structured, int? allowance, string code)
    {
        var resolver = new Resolver(structured, allowance);
        var request = Request(); request.StructuredOutputSchema!["description"] = new string('x', 3000);
        var ex = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ModelRequestAdmission.ValidateAsync(request, resolver, 32000, "judge", TestContext.Current.CancellationToken));
        Assert.Equal(code, ex.Code); Assert.Equal("not_started", ex.Details!["dispatch_status"]!.ToString());
        Assert.DoesNotContain("secret evidence", ex.Message + ex.Details.ToJsonString());
    }

    [Fact]
    public async Task BothHostAndModelCeilingsApplyWithoutMutatingRequest()
    {
        var request = Request(); var before = request.Prompt;
        await ModelRequestAdmission.ValidateAsync(request, new Resolver(true, 32000), 32000, "tasks", TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ModelRequestAdmission.ValidateAsync(request, new Resolver(true, 32000), 1, "tasks", TestContext.Current.CancellationToken));
        Assert.Equal(1, ex.Details!["limit"]!.GetValue<int>());
        Assert.Equal("estimated_tokens", ex.Details["measurement"]!.ToString());
        Assert.Equal(before, request.Prompt); Assert.Equal(32768, request.MaxTokens);
    }

    [Fact]
    public async Task UnsupportedReasoningDoesNotSilentlyDisappear()
    {
        var request = Request(); request.Reasoning = "high";
        var ex = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ModelRequestAdmission.ValidateAsync(request, new Resolver(true, 32000), 32000, "replan", TestContext.Current.CancellationToken));
        Assert.Equal("MODEL_REQUEST_INCOMPATIBLE", ex.Code);
        Assert.Equal("high", request.Reasoning);
    }

    private static LLMRequest Request() => new() { Prompt = "secret evidence", MaxTokens = 32768, StructuredOutputStrict = true,
        StructuredOutputSchema = new JsonObject { ["type"] = "object" }, Reasoning = "medium" };
    private sealed class Resolver(bool? structured, int? allowance) : ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult(allowance);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult(structured);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
    }
}
