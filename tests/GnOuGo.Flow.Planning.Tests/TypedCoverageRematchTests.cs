using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning.Capabilities;
using Xunit;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TypedCoverageRematchTests
{
    private static JsonObject Invoke(string name, params object[] args) => (JsonObject)typeof(CapabilityCoverageContext)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    [Theory]
    [InlineData("affected", "retained")]
    [InlineData("à_corriger", "conservé")]
    public void CoverageRepairCannotRewriteUnrelatedMatchesOrConstraints(string affected, string retained)
    {
        JsonObject Match(string id) => new()
        {
            ["status"] = "matched",
            ["reason"] = "Declared producer",
            ["catalog_ids"] = new JsonArray(id),
            ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = "",
            ["conditional_mode"] = ""
        };
        var original = new JsonObject
        {
            ["operation_matches"] = new JsonObject { [affected] = Match("old"), [retained] = Match("unchanged") },
            ["constraint_matches"] = new JsonObject
            {
                ["policy"] = new JsonObject
                {
                    ["status"] = "policy_only",
                    ["denied_catalog_ids"] = new JsonArray(),
                    ["candidate_catalog_ids"] = new JsonArray(),
                    ["reason"] = "Retained locked policy"
                }
            }
        };
        var before = original.ToJsonString(); var scope = new HashSet<string>(StringComparer.Ordinal) { affected };
        var patch = new JsonObject { ["operation_matches"] = new JsonObject { [affected] = Match("replacement") } };
        var merged = Invoke("MergeTypedCoverageRematch", patch, original, scope);
        Assert.Equal("replacement", merged["operation_matches"]![affected]!["catalog_ids"]![0]!.ToString());
        Assert.True(JsonNode.DeepEquals(original["operation_matches"]![retained], merged["operation_matches"]![retained]));
        Assert.True(JsonNode.DeepEquals(original["constraint_matches"], merged["constraint_matches"])); Assert.Equal(before, original.ToJsonString());
        // The recorded full-response pattern tried to rewrite an unaffected constraint
        // as enforced with no denial. It is outside the v2 repair response entirely.
        patch["constraint_matches"] = new JsonObject { ["policy"] = new JsonObject { ["status"] = "enforced", ["denied_catalog_ids"] = new JsonArray() } };
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Invoke("MergeTypedCoverageRematch", patch, original, scope)).InnerException);
        patch.Remove("constraint_matches"); patch["operation_matches"]![retained] = Match("invented");
        Assert.Throws<TargetInvocationException>(() => Invoke("MergeTypedCoverageRematch", patch, original, scope));
        patch["operation_matches"]!.AsObject().Clear();
        Assert.Throws<TargetInvocationException>(() => Invoke("MergeTypedCoverageRematch", patch, original, scope));
        Assert.Equal(before, original.ToJsonString());
    }

    [Fact]
    public void CoverageSchemaExposesOnlyTheAffectedOperationAndKeepsDefinitions()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"operation_matches":{"type":"object","properties":{"a":{"$ref":"#/$defs/entry"},"b":{"$ref":"#/$defs/entry"}},"required":["a","b"],"additionalProperties":false},"constraint_matches":{"type":"object"}},"required":["operation_matches","constraint_matches"],"additionalProperties":false,"$defs":{"entry":{"type":"object"}}}""")!.AsObject();
        var scoped = Invoke("ScopeTypedCoverageRematchSchema", schema, new HashSet<string> { "a" });
        Assert.Single(scoped["properties"]!.AsObject());
        Assert.Equal("a", Assert.Single(scoped["properties"]!["operation_matches"]!["required"]!.AsArray())!.ToString());
        Assert.Single(scoped["properties"]!["operation_matches"]!["properties"]!.AsObject());
        Assert.True(JsonNode.DeepEquals(schema["$defs"], scoped["$defs"])); Assert.Equal(2, schema["properties"]!.AsObject().Count);
    }
}
