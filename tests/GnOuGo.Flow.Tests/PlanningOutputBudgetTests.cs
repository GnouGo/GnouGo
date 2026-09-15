using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class PlanningOutputBudgetTests
{
    [Fact]
    public void OrdinaryRequestSerializationAndCeilingRemainUnchanged()
    {
        var request = PlanningGenerationPolicy.Apply(new() { Model = "local", MaxTokens = 16384 }, new());
        Assert.Equal(8192, request.MaxTokens);
        var json = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        Assert.DoesNotContain("outputBudgetEscalation", json);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))),
            PlanningGenerationPolicy.RequestFingerprint(request));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("prompt")]
    [InlineData("schema")]
    [InlineData("model")]
    [InlineData("reasoning")]
    [InlineData("receipt")]
    [InlineData("owner")]
    [InlineData("hash")]
    [InlineData("page")]
    [InlineData("level")]
    [InlineData("ceiling")]
    [InlineData("decision")]
    [InlineData("multiple")]
    public void OnlyExactOwnedSingletonTruncationMayChangeItsCeiling(string change)
    {
        var parent = PlanningGenerationPolicy.Apply(new()
        {
            Model = "model", Reasoning = "low", Prompt = "private", StructuredOutputStrict = true,
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"choice":{"type":"boolean"}},"required":["choice"],"additionalProperties":false}""")
        }, new());
        if (change == "multiple") parent.StructuredOutputSchema!["properties"]!["other"] = new JsonObject { ["type"] = "boolean" };
        var hash = PlanningGenerationPolicy.RequestFingerprint(parent); parent.ClientRequestId = "session:parent:page:" + hash;
        var receipt = new LLMResponse { CompletionStatus = "output_limit" };
        var child = JsonSerializer.Deserialize(JsonSerializer.Serialize(parent, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        child.ClientRequestId = "session:child"; child.MaxTokens = 16384;
        child.OutputBudgetEscalation = new("page", parent.ClientRequestId, hash, PlanningGenerationPolicy.ReceiptFingerprint(receipt), "choice", "canonical", "evidence");
        switch (change)
        {
            case "prompt": child.Prompt += "changed"; break;
            case "schema": child.StructuredOutputSchema!["description"] = "changed"; break;
            case "model": child.Model = "other"; break;
            case "reasoning": child.Reasoning = "medium"; break;
            case "receipt": receipt.CompletionStatus = "completed"; break;
            case "owner": child.ClientRequestId = "foreign:child"; break;
            case "hash": child.OutputBudgetEscalation = child.OutputBudgetEscalation with { ParentRequestHash = "changed" }; break;
            case "page": child.OutputBudgetEscalation = child.OutputBudgetEscalation with { ParentPageId = "foreign" }; break;
            case "level": child.OutputBudgetEscalation = child.OutputBudgetEscalation with { Level = 2 }; break;
            case "ceiling": child.MaxTokens = 32768; break;
            case "decision": child.OutputBudgetEscalation = child.OutputBudgetEscalation with { DecisionId = "foreign" }; break;
        }
        if (change == "valid")
        {
            PlanningGenerationPolicy.ValidateOutputEscalation(child, parent, receipt, "session");
            Assert.Equal(16384, PlanningGenerationPolicy.Apply(child, new()).MaxTokens);
        }
        else Assert.Throws<PlanningConflictException>(() => PlanningGenerationPolicy.ValidateOutputEscalation(child, parent, receipt, "session"));
    }
}
