using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class EvidenceSelectionWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedDiscoveryEncodingRecoversExactlyAndValidatesBeforeCanonicalization(bool invalid)
    {
        var state = PlannerFixture.Session();
        var schema = JsonNode.Parse(Schema().ToJsonString().Replace("view_a", "group:worker", StringComparison.Ordinal)
            .Replace("view_b", "group:other", StringComparison.Ordinal).Replace("ordinary_a", "requirement:req_0", StringComparison.Ordinal)
            .Replace("ordinary_b", "requirement:req_1", StringComparison.Ordinal))!.AsObject();
        var interrupted = new TestRuntime { Checkpointed = _ => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, interrupted,
            PlanningCompactConstruction.DiscoveryPurpose, "Read an offered source.\n{}", schema, PlannerFixture.Ct));
        Assert.Empty(interrupted.Calls);
        state = PlannerFixture.Clone(state);
        var pending = state.PendingCall!;
        var issued = JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest);
        Assert.True(PlanningEvidenceSelectionWire.Issued(pending.Request.StructuredOutputSchema!));
        var canonical = PlanningModelCalls.CanonicalEvidenceRequest(pending);
        Assert.False(PlanningEvidenceSelectionWire.Issued(canonical.StructuredOutputSchema!));
        Assert.Contains("group:worker", canonical.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        var id = invalid ? "unknown" : pending.EvidenceAddresses!.Addresses.Single(p => p.Value == "group:worker").Key;
        var raw = new JsonObject { ["evidenceRequests"] = new JsonObject { ["single"] = id } };
        var before = raw.ToJsonString();
        var runtime = new TestRuntime { EncodeCanonicalEvidence = false, Respond = (request, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = raw };
        } };
        var call = () => PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose,
            "New context must not replace an issued request.", new JsonObject(), PlannerFixture.Ct);
        if (invalid) await Assert.ThrowsAsync<PlanningResponseException>(call);
        else Assert.Equal("group:worker", (await call())["evidenceRequests"]![0]!.ToString());
        Assert.Equal(before, raw.ToJsonString());
        Assert.Single(runtime.Calls);
        Assert.Equal(1, state.ModelCalls);
        Assert.Null(state.PendingCall);
    }

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
