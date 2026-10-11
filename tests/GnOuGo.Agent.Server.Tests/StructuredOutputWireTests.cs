using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class StructuredOutputWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualProviderPayloadKeepsProjectedDefinitionsAndDoesNotMutateContracts(bool background)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SchemaPortability", "retained-schema-rejection.json")))!;
        var authoritative = fixture["schemas"]![1]!.AsObject(); var original = authoritative.ToJsonString();
        var projected = PlanningContractValidation.ProjectStructuredOutputSchema(authoritative); var wire = projected.ToJsonString();
        var payload = background
            ? ChatRequestBuilder.OpenAiResponsesBackground("model", "prompt", null, "medium", projected, true, 32768)
            : ChatRequestBuilder.OpenAiFull("model", "prompt", structuredOutputSchema: projected, structuredOutputStrict: true, reasoning: "medium", maxOutputTokens: 32768);
        var json = JsonNode.Parse(payload)!;
        var schema = background ? json["text"]!["format"]!["schema"]! : json["response_format"]!["json_schema"]!["schema"]!;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, true));
        Assert.True(JsonNode.DeepEquals(projected, schema));
        Assert.Equal(original, authoritative.ToJsonString()); Assert.Equal(wire, projected.ToJsonString());
    }
}
