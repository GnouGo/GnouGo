using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class PlanningGenerationPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(128001)]
    [InlineData(int.MaxValue)]
    public void DiagnosticInputTargetsDoNotInheritStrictConfigurationRanges(int input)
    {
        var options = new PlanningGenerationOptions("high", input, 65536);
        Assert.Throws<ArgumentException>(() => PlanningGenerationPolicy.Validate(options));
        Assert.Throws<ArgumentException>(() => PlanningGenerationPolicy.Apply(new(), options));
        PlanningGenerationPolicy.Validate(options, advisoryOptimization: true);
        var request = PlanningGenerationPolicy.Apply(new LLMRequest { MaxTokens = 4096 }, options, advisoryOptimization: true);
        Assert.Equal(input, options.MaxInputTokensPerRequest);
        Assert.Equal(4096, request.MaxTokens);
        Assert.Equal("high", request.Reasoning);
        Assert.True(request.RequireOutputTokenLimit);
        Assert.True(request.DisableTransportRetries);
    }

    [Theory]
    [InlineData(0, 8192, "medium")]
    [InlineData(-1, 8192, "medium")]
    [InlineData(16000, 0, "medium")]
    [InlineData(16000, 65537, "medium")]
    [InlineData(16000, 8192, "unsupported")]
    public void DiagnosticInputTargetsPreservePositiveValuesOutputAllocationsAndReasoningValidation(int input, int output, string reasoning)
    {
        var options = new PlanningGenerationOptions(reasoning, input, output);
        Assert.Throws<ArgumentException>(() => PlanningGenerationPolicy.Validate(options));
        Assert.Throws<ArgumentException>(() => PlanningGenerationPolicy.Validate(options, advisoryOptimization: true));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(128000)]
    public void StrictInputBoundaryValuesRemainValid(int input) =>
        PlanningGenerationPolicy.Validate(new("medium", input, 8192));
}
