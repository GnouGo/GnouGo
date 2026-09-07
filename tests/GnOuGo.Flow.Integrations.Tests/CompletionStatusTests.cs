using System.Text.Json.Nodes;
using Xunit;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class CompletionStatusTests
{
    [Theory]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\"}]}", "output_limit")]
    [InlineData("{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}", "output_limit")]
    [InlineData("{\"stop_reason\":\"max_tokens\"}", "output_limit")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\"}]}", null)]
    [InlineData("{\"incomplete_details\":\"unknown\"}", null)]
    public void TransportCompletionReasonsBecomeProviderNeutralMetadata(string response, string? expected)
        => Assert.Equal(expected, RoutingLLMClientAdapter.CompletionStatus(JsonNode.Parse(response)));
}
