using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningFreshDefaultsTests
{
    [Fact]
    public void FreshRequestsUseSharedLargerAllowances()
    {
        var request = new PlanningRequest();
        Assert.Equal(64, request.MaxModelCalls);
        Assert.Equal(4, request.MaxReplanAttempts);
        Assert.Equal(32_000, request.Generation.MaxInputTokensPerRequest);
        Assert.Equal(32_768, request.Generation.MaxOutputTokens);
        Assert.Null(request.Options["business_judge"]);
    }

    [Fact]
    public void HistoricalMissingFieldsKeepOriginalAllowances()
    {
        var request = JsonSerializer.Deserialize("{}", PlanningJsonContext.Default.PlanningRequest)!;
        Assert.Equal(8, request.MaxModelCalls);
        Assert.Equal(2, request.MaxReplanAttempts);
        Assert.Equal(12_000, request.Generation.MaxInputTokensPerRequest);
        Assert.Equal(8_192, request.Generation.MaxOutputTokens);
        Assert.Empty(request.Options);
    }

    [Theory]
    [InlineData("{}", 12000, 8192)]
    [InlineData("{\"maxInputTokensPerRequest\":25000}", 25000, 8192)]
    public void HistoricalGenerationPreservesMissingAndExplicitFields(string json, int input, int output)
    {
        var request = JsonSerializer.Deserialize("{\"generation\":" + json + "}", PlanningJsonContext.Default.PlanningRequest)!;
        Assert.Equal(input, request.Generation.MaxInputTokensPerRequest);
        Assert.Equal(output, request.Generation.MaxOutputTokens);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.PlanningRequest), PlanningJsonContext.Default.PlanningRequest)!;
        Assert.Equal(input, restored.Generation.MaxInputTokensPerRequest);
        Assert.Equal(output, restored.Generation.MaxOutputTokens);
    }
}
