using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CommitmentPromptContractTests
{
    [Fact]
    public async Task IssuedFullProposalExplainsNamesAndItsExampleCompilesWithoutChangingAcceptance()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        var original = PlanningSchemas.FullProposal(state, compact: false);
        var annotations = PlanningSchemas.Compact(original.DeepClone().AsObject(), compositionHints: true);
        var historical = PlanningSchemas.Compact(original.DeepClone().AsObject());
        var completed = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, completed.Status); Assert.Null(completed.ApprovedHash);
        var issued = Assert.Single(runtime.Calls);
        var description = PlanningSchemaReferences.Walk(issued.StructuredOutputSchema!.AsObject(), "", 0)
            .Select(e => e.Schema["description"]?.ToString()).Single(s => s?.Contains("Non-executable obligations", StringComparison.Ordinal) == true)!;
        Assert.Contains("result.port: its exact output port", description);
        Assert.Contains("NAME in requirements.outputs", description); Assert.Contains("result:null", description);
        Assert.Contains("operation-only", description);
        Assert.DoesNotContain("Non-executable obligations", historical.ToJsonString());
        foreach (var schema in new[] { annotations, historical })
        {
            var response = TestRuntime.Response(new() { StructuredOutputSchema = schema }, runtime.Proposal);
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json, schema));
            response.Json!["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["output"] = "workspace/report.xlsx";
            // These annotations explain a semantic rule; they do not narrow JSON Schema.
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json, schema));
            response.Json["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["port"] = 42;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response.Json, schema));
        }
        Assert.InRange(PlanningJsonTransport.EstimateInputTokens(issued.Prompt, issued.StructuredOutputSchema.AsObject()), 1, state.Request.Generation.MaxInputTokensPerRequest);
        foreach (var schema in new[] { annotations, historical })
            foreach (var (_, node) in PlanningSchemaReferences.Walk(schema, "", 0)) node.Remove("description");
        Assert.True(JsonNode.DeepEquals(annotations, historical));
    }

    [Fact]
    public void SharedPresentationPreservesEveryConstraintAndLiteralAndKeepsExistingReferenceBases()
    {
        var repeated = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"},"nullable":{"type":["number","null"]}},"required":["label","nullable"],"additionalProperties":false,"default":{"type":"string","required":["literal"]},"description":"Keep this explanatory contract annotation"}""")!;
        var existing = JsonNode.Parse("""{"$defs":{"s0":{"type":"string","enum":["opaque"]}},"$ref":"#/$defs/s0"}""")!;
        var context = new JsonObject { ["operations"] = new JsonArray(new JsonObject
        {
            ["id"] = "arbitrary_operation", ["inputs"] = new JsonArray(new JsonObject { ["name"] = "first", ["type"] = repeated.DeepClone() },
                new JsonObject { ["name"] = "second", ["type"] = repeated.DeepClone() }),
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "result", ["type"] = repeated.DeepClone() },
                new JsonObject { ["name"] = "scoped", ["type"] = existing.DeepClone() })
        }) };
        var before = context.DeepClone(); var bytes = context.ToJsonString().Length;
        PlanningPrompt.ShareOperationSchemas(context);
        Assert.True(context.ToJsonString().Length < bytes);
        Assert.NotEmpty(context["schemas"]!.AsObject());
        var operation = context["operations"]![0]!;
        Assert.True(JsonNode.DeepEquals(existing, operation["outputs"]![1]!["type"]));
        foreach (var key in new[] { "inputs", "outputs" })
            for (var i = 0; i < operation[key]!.AsArray().Count; i++)
                Assert.True(JsonNode.DeepEquals(before["operations"]![0]![key]![i]!["type"], Expand(operation[key]![i]!["type"]!, context)));
    }

    internal static JsonNode Expand(JsonNode schema, JsonNode context)
    {
        var result = schema.DeepClone();
        if (result is not JsonObject obj) return result;
        foreach (var (_, node) in PlanningSchemaReferences.Walk(obj, "", 0).ToArray())
            if (node["$ref"]?.ToString() is { } reference && reference.StartsWith("#/schemas/", StringComparison.Ordinal))
            {
                var replacement = context["schemas"]![reference[10..]]!.DeepClone();
                if (node.Parent is JsonObject parent) parent[parent.Single(p => ReferenceEquals(p.Value, node)).Key] = replacement;
                else if (node.Parent is JsonArray array) array[array.IndexOf(node)] = replacement;
                else result = replacement;
            }
        return result;
    }
}
