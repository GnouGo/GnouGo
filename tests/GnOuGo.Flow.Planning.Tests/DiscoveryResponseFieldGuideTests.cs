using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiscoveryResponseFieldGuideTests
{
    [Fact]
    public void GuideDerivesBranchFieldsWithoutCopyingDirectoriesConstraintsOrRecursiveGrammar()
    {
        const string prompt = "Choose an issued action.\n{\"original\":\"Keep this context.\"}";
        var schema = JsonNode.Parse("""
            {"type":"object","required":["binding","evidenceRequests"],"properties":{
              "binding":{"anyOf":[{"$ref":"#/$defs/binding"},{"type":"null"}]},
              "evidenceRequests":{"type":"array","items":{"type":"string","enum":["issued-secret-directory"]}}},
             "$defs":{"binding":{"type":"object","required":["kind","operationIds","closeScopes"],"properties":{
               "kind":{"type":"string","enum":["operation","close"]},"operationIds":{"type":"array","items":{"type":"string"}},
               "closeScopes":{"type":["array","null"],"items":{"type":"string"}},"recursive":{"$ref":"#/$defs/binding"}}}}}
            """)!;
        var original = schema.ToJsonString();
        var result = PlanningPromptPresentation.PresentDiscoveryResponseFields(prompt, schema);
        Assert.Contains("response: {binding,evidenceRequests}", result, StringComparison.Ordinal);
        Assert.Contains("response.binding: {kind,operationIds,closeScopes}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("issued-secret-directory", result, StringComparison.Ordinal);
        Assert.DoesNotContain("$defs", result, StringComparison.Ordinal);
        Assert.DoesNotContain("response.binding.recursive", result, StringComparison.Ordinal);
        Assert.EndsWith("\n{\"original\":\"Keep this context.\"}", result, StringComparison.Ordinal);
        Assert.Equal(original, schema.ToJsonString());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result) - System.Text.Encoding.UTF8.GetByteCount(prompt), 1, 800);
    }

    [Fact]
    public void DistinctObjectBranchesRemainAlternativesRatherThanInventingAMergedRequiredObject()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","required":["action"],"properties":{"action":{"oneOf":[
              {"type":"object","required":["first"],"properties":{"first":{"type":"string"}}},
              {"type":"object","required":["second"],"properties":{"second":{"type":"boolean"}}}]}}}
            """)!;
        var guide = PlanningPromptPresentation.PresentDiscoveryResponseFields("Choose a legal action.", schema);
        Assert.Contains("response.action: {first} | {second}", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("{first,second}", guide, StringComparison.Ordinal);
    }
}
