using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningPromptPresentationTests
{
    [Theory]
    [InlineData("construction_draft")]
    [InlineData("replan")]
    public void FreshDraftAndCorrectionExposeTheRuntimeContextBoundary(string purpose)
    {
        var state = PlannerFixture.Session();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var before = PlanningConstruction.Hash(state.Plan);
        var request = PlanningModelCalls.PrepareRequest(state, purpose, "Original obligations.\n{}",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null);
        Assert.Contains("planning context and policy IDs are not runtime facts", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Bind needed rules explicitly", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, PlanningConstruction.Hash(state.Plan));
        Assert.Contains("Original obligations.", request.Prompt, StringComparison.Ordinal);
        Assert.Empty(state.Construction!.Checkpoints);
    }

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
    public void DiscoveryUnionBranchesShareExactAncestorsThroughIssuedEvidenceAddresses()
    {
        var evidence = new JsonObject();
        var constraints = new JsonArray(new JsonObject { ["pointer"] = "/output", ["constraints"] = new JsonObject
            { ["oneOf"] = new JsonArray(new JsonObject { ["description"] = new string('x', 11_000) }) } });
        for (var index = 0; index < 9; index++)
        {
            var pointer = "/output/oneOf/" + index;
            var ancestors = constraints.DeepClone();
            if (index == 8) ancestors[0]!["constraints"]!["oneOf"]![0]!["description"] = "A different constraint";
            var id = index == 7 ? "other" : "read";
            evidence["schema:" + id + "@v1:" + pointer] = new JsonObject { ["id"] = id, ["version"] = "v1",
                ["hash"] = new string('a', 64), ["pointer"] = pointer, ["schema"] = new JsonObject { ["const"] = index },
                ["ancestors"] = ancestors, ["referenceClosure"] = new JsonObject() };
        }
        var prompt = "Inspect the original branches.\n" + new JsonObject { ["evidence"] = evidence }.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_discovery", prompt,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null, out var map);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        Assert.True(request.Prompt.Length < prompt.Length - 60_000);
        foreach (var (address, node) in shown["evidence"]!.AsObject())
        {
            var row = node!.AsObject();
            var expected = evidence[map!.Addresses[address]]!;
            var actual = row["ancestors"] ?? PlanningSchemaReferences.Read(shown, row["ancestorsPresentedAt"]!.ToString());
            Assert.Equal(expected["ancestors"]!.ToJsonString(), actual!.ToJsonString());
            Assert.Equal(expected["schema"]!.ToJsonString(), row["schema"]!.ToJsonString());
            if (expected["id"]!.ToString() == "other" || expected["pointer"]!.ToString().EndsWith("/8", StringComparison.Ordinal))
                Assert.Null(row["ancestorsPresentedAt"]);
        }
    }

    [Fact]
    public void DiscoveredBranchUsesItsExactOriginalSchemaInsideTheDisplayedAncestor()
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = new string('x', 2000) };
        var row = new JsonObject { ["id"] = "read", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["pointer"] = "/output/oneOf/1", ["schema"] = schema.DeepClone(),
            ["ancestors"] = new JsonArray(new JsonObject { ["pointer"] = "/output", ["constraints"] = new JsonObject
                { ["oneOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, schema.DeepClone()) } }) };
        var other = row.DeepClone(); other["schema"]!["description"] = "Different original constraint.";
        var context = new JsonObject { ["evidence"] = new JsonObject { ["schema:read@v1:/output/oneOf/1"] = row,
            ["schema:read@v1:/output/oneOf/0"] = other } };
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_discovery",
            "Inspect the returned branches.\n" + context.ToJsonString(), new JsonObject { ["type"] = "object" }, null, null);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var evidence = shown["evidence"]!.AsObject().Select(p => p.Value!).ToArray();
        Assert.Null(evidence[0]["schema"]);
        Assert.Equal(schema.ToJsonString(), PlanningSchemaReferences.Read(shown, evidence[0]["schemaPresentedAt"]!.ToString())!.ToJsonString());
        Assert.Equal(other["schema"]!.ToJsonString(), evidence[1]["schema"]!.ToJsonString());
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
