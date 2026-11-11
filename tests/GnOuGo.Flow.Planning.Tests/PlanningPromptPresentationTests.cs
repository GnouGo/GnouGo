using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningPromptPresentationTests
{
    [Fact]
    public void RepeatedImportedSchemaShapesFactorWithoutChangingCardinalityOrLiteralData()
    {
        var record = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["href"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = new string('x', 2000) } },
            ["required"] = new JsonArray("href"), ["additionalProperties"] = false };
        var records = new JsonObject { ["type"] = "array", ["items"] = record, ["minItems"] = 0 };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["first"] = records, ["second"] = records.DeepClone() },
            ["required"] = new JsonArray("first", "second"), ["additionalProperties"] = false,
            ["default"] = new JsonObject { ["first"] = records.DeepClone(), ["second"] = records.DeepClone() } };
        var context = new JsonObject { ["interfaces"] = new JsonArray(new JsonObject
            { ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "accepted" }, ["schema"] = schema }) };
        var prompt = "Keep all original obligations.\n" + context.ToJsonString();
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        Assert.True(compact.Length < prompt.Length - 500);
        var shown = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!["interfaces"]![0]!["schema"]!.AsObject();
        Assert.NotNull(shown["$defs"]);
        Assert.True(JsonNode.DeepEquals(schema["default"], shown["default"]));
        var expanded = PlanningSchemaReferences.ExpandLocalReferences(shown); expanded.Remove("$defs");
        Assert.True(JsonNode.DeepEquals(schema, expanded));
        foreach (var json in new[] { "{\"first\":[],\"second\":[]}", "{\"first\":[{\"href\":null}],\"second\":[]}",
            "{\"first\":[{\"href\":\"same\"},{\"href\":\"same\"}],\"second\":[{\"href\":null}]}" })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse(json), shown));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse("{\"first\":[{}],\"second\":[]}"), shown));
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
        Assert.Equal("Keep all original obligations.\n" + context.ToJsonString(), prompt);
    }

    private static JsonObject Context()
    {
        var schema = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = new string('x', 2000),
            ["pattern"] = "^https://", ["default"] = null, ["allOf"] = new JsonArray(new JsonObject { ["maxLength"] = 20000 }) };
        return new() { ["operations"] = new JsonArray(new JsonObject { ["id"] = "operation", ["version"] = "exact-version", ["hash"] = new string('a', 64),
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["url"] = schema.DeepClone() }, ["required"] = new JsonArray("url") } }),
            ["evidence"] = new JsonObject { ["schema:operation@exact-version:/input/properties/url"] = new JsonObject { ["id"] = "operation", ["version"] = "exact-version", ["hash"] = new string('a', 64),
                ["pointer"] = "/input/properties/url", ["schema"] = schema, ["ancestors"] = new JsonArray(new JsonObject { ["pointer"] = "/input", ["constraints"] = new JsonObject { ["required"] = new JsonArray("url", "otherRequired") } }),
                ["referenceClosure"] = new JsonObject { ["$defs/retained"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 } } } },
            ["requirements"] = new JsonArray("Complete original requirement é 😀"), ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = "RETAINED_DIAGNOSTIC", ["message"] = "Exact missing contract constraint" }) };
    }

    [Fact]
    public void SharedSchemaRetainsExactSourceCoordinatesAncestorsClosuresAndDiagnostics()
    {
        var original = Context(); var frozen = original.ToJsonString();
        var prompt = "Original phase instructions.\n" + frozen;
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        Assert.True(compact.Length < prompt.Length - 1000);
        var context = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!.AsObject();
        var row = context["evidence"]!.AsObject().Single().Value!;
        Assert.Null(row["schema"]);
        Assert.Equal("/operations/0/inputSchema/properties/url", row["schemaPresentedAt"]!.ToString());
        row["schema"] = PlanningSchemaReferences.Read(context, row["schemaPresentedAt"]!.ToString())!.DeepClone(); row.AsObject().Remove("schemaPresentedAt");
        context.Remove("schemaPresentation");
        Assert.True(JsonNode.DeepEquals(original, context));
        Assert.Equal(frozen, original.ToJsonString());
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("pointer")]
    [InlineData("schema")]
    public void DifferentOwnerVersionCoordinateOrConstraintCannotShare(string defect)
    {
        var context = Context(); var row = context["evidence"]!.AsObject().Single().Value!;
        if (defect == "schema") row["schema"]!["pattern"] = "^different://";
        else row[defect] = "different";
        var prompt = "Exact instructions.\n" + context.ToJsonString();
        Assert.Equal(prompt, PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt));
    }

    [Fact]
    public void SelectedOutputSharingKeepsRequiredSiblingsAndOriginalReferenceClosure()
    {
        var context = Context(); var row = context["evidence"]!.AsObject().Single().Value!;
        row["pointer"] = "/output/properties/records";
        row["schema"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/retained" }, ["minItems"] = 1 };
        context["operations"]![0]!["selectedOutputs"] = new JsonArray(new JsonObject { ["pointer"] = row["pointer"]!.DeepClone(), ["schema"] = row["schema"]!.DeepClone() });
        var before = context.DeepClone(); var prompt = "Exact selected output.\n" + context.ToJsonString();
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        var after = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!;
        var evidence = after["evidence"]!.AsObject().Single().Value!;
        Assert.Equal("/operations/0/selectedOutputs/0/schema", evidence["schemaPresentedAt"]!.ToString());
        Assert.True(JsonNode.DeepEquals(before["evidence"]!.AsObject().Single().Value!["ancestors"], evidence["ancestors"]));
        Assert.True(JsonNode.DeepEquals(before["evidence"]!.AsObject().Single().Value!["referenceClosure"], evidence["referenceClosure"]));
        Assert.True(JsonNode.DeepEquals(row["schema"], PlanningSchemaReferences.Read(after, evidence["schemaPresentedAt"]!.ToString())));
    }

    [Fact]
    public void NewIssuanceBindsSharedPresentationWhileSavedRequestAndRecoveryIdentityStayExact()
    {
        var state = PlannerFixture.Session(); var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var request = PlanningModelCalls.PrepareRequest(state, "construction_draft", "Keep originals.\n" + Context().ToJsonString(), schema, null, null, out var map);
        Assert.Contains("schemaPresentedAt", request.Prompt, StringComparison.Ordinal);
        var call = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = "construction_draft", Request = request, EvidenceAddresses = map };
        PlanningModelCalls.ValidateEvidenceAddresses(call);
        var frozen = System.Text.Json.JsonSerializer.Serialize(call, PlanningJsonContext.Default.PlanningModelCall);
        var restored = System.Text.Json.JsonSerializer.Deserialize(frozen, PlanningJsonContext.Default.PlanningModelCall)!;
        PlanningModelCalls.ValidateEvidenceAddresses(restored);
        Assert.Equal(frozen, System.Text.Json.JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningModelCall));
        restored.Request.Prompt += "modified";
        Assert.Throws<PlanningConflictException>(() => PlanningModelCalls.ValidateEvidenceAddresses(restored));
    }
}
