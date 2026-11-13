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


}
