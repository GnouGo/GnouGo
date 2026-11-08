using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class EvidenceSelectionWireTests
{
    private static JsonObject Schema() => JsonNode.Parse("""
        {"type":"object","properties":{"evidenceRequests":{"anyOf":[
          {"type":"array","items":{"type":"string","enum":["view_a","view_b"]},"minItems":1,"maxItems":1},
          {"type":"array","items":{"type":"string","enum":["ordinary_a","ordinary_b"]},"minItems":1,"maxItems":3},
          {"type":"null"}]}},"required":["evidenceRequests"],"additionalProperties":false,"$comment":"Keep original annotation."}
        """)!.AsObject();

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"view_a\"]")]
    [InlineData("[\"view_b\"]")]
    [InlineData("[\"view_a\",\"view_b\"]")]
    [InlineData("[\"view_a\",\"ordinary_a\"]")]
    [InlineData("[\"ordinary_a\"]")]
    [InlineData("[\"ordinary_a\",\"ordinary_a\",\"ordinary_b\"]")]
    [InlineData("[\"ordinary_a\",\"ordinary_a\",\"ordinary_b\",\"ordinary_b\"]")]
    [InlineData("[\"unknown\"]")]
    [InlineData("\"view_a\"")]
    public void EncodingKeepsEveryCanonicalChoiceAndCardinalityWithoutMutatingOriginals(string selected)
    {
        var source = Schema(); var before = source.ToJsonString();
        var projected = PlanningPromptPresentation.PresentEvidenceSelectionObjects(source);
        Assert.True(PlanningEvidenceSelectionWire.Issued(projected));
        Assert.Equal(before, source.ToJsonString());
        Assert.True(JsonNode.DeepEquals(source, PlanningEvidenceSelectionWire.CanonicalSchema(projected)));
        Assert.True(JsonNode.DeepEquals(projected, PlanningEvidenceSelectionWire.Project(projected)));
        Assert.Empty(PlanningContractValidation.ValidateSchema(projected, strict: true));
        var response = new JsonObject { ["evidenceRequests"] = JsonNode.Parse(selected) };
        var original = response.ToJsonString();
        var wire = PlanningEvidenceSelectionWire.WireResponse(response, projected);
        var allowed = PlanningContractValidation.ValidateInstance(response, source).Count == 0;
        Assert.Equal(allowed, PlanningContractValidation.ValidateInstance(wire, projected).Count == 0);
        Assert.Equal(original, response.ToJsonString());
        if (allowed) Assert.True(JsonNode.DeepEquals(response, PlanningEvidenceSelectionWire.CanonicalResponse(wire, projected)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"single\":\"unknown\"}")]
    [InlineData("{\"single\":\"ordinary_a\"}")]
    [InlineData("{\"single\":[\"view_a\",\"view_b\"]}")]
    [InlineData("{\"batch\":[\"view_a\"]}")]
    [InlineData("{\"batch\":[]}")]
    [InlineData("{\"single\":\"view_a\",\"batch\":[\"ordinary_a\"]}")]
    [InlineData("{\"single\":\"view_a\",\"extra\":true}")]
    public void UnissuedIdsMixedSelectorsAndWholeViewBatchesRemainForbidden(string selector)
    {
        var projected = PlanningEvidenceSelectionWire.Project(Schema());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["evidenceRequests"] = JsonNode.Parse(selector) }, projected));
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("extra_constraint")]
    [InlineData("literal")]
    [InlineData("boolean")]
    public void UnsupportedOrUnrelatedShapesStayExact(string kind)
    {
        JsonNode source = Schema();
        if (kind == "overlap") source["properties"]!["evidenceRequests"]!["anyOf"]![1]!["items"]!["enum"]!.AsArray().Add("view_a");
        if (kind == "extra_constraint") source["properties"]!["evidenceRequests"]!["anyOf"]![0]!["uniqueItems"] = true;
        if (kind == "literal") source = new JsonObject { ["const"] = source };
        if (kind == "boolean") source = JsonValue.Create(true)!;
        Assert.True(JsonNode.DeepEquals(source, PlanningEvidenceSelectionWire.Project(source)));
    }
}
