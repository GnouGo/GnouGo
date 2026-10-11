using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class DynamicMappingCollectionTests
{
    [Theory]
    [InlineData("{\"type\":\"null\"}", true)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", true)]
    [InlineData("{\"const\":null}", true)]
    [InlineData("{\"enum\":[\"known\",null]}", true)]
    [InlineData("{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}", true)]
    [InlineData("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}", true)]
    [InlineData("{\"allOf\":[{\"type\":[\"string\",\"null\"]},{\"enum\":[null]}]}", true)]
    [InlineData("{\"$defs\":{\"fact\":{\"type\":[\"string\",\"null\"]}},\"$ref\":\"#/$defs/fact\"}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"default\":null}", false)]
    [InlineData("{\"type\":\"string\"}", false)]
    [InlineData("{\"type\":[\"string\",\"null\"],\"enum\":[\"known\"]}", false)]
    [InlineData("{\"type\":[\"string\",\"null\"],\"const\":\"known\"}", false)]
    [InlineData("{\"allOf\":[{\"type\":[\"string\",\"null\"]},{\"type\":\"string\"}]}", false)]
    [InlineData("{\"oneOf\":[{\"type\":[\"string\",\"null\"]},{\"type\":\"null\"}]}", false)]
    [InlineData("{\"$ref\":\"#/$defs/unknown\",\"type\":\"null\"}", false)]
    [InlineData("{\"$ref\":\"#\"}", false)]
    public void LiteralNullNeedsExplicitCompatibleTarget(string schema, bool allowed)
    {
        var target = JsonNode.Parse(schema)!.AsObject();
        if (allowed) Assert.Null(new JintSandbox().ExecuteMapping("null", new JsonObject(), Ct, target));
        else
        {
            var error = Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping("null", new JsonObject(), Ct, target));
            Assert.Equal("program", error.Details!["mapping_failure_kind"]!.ToString());
            Assert.Contains("result $ ", error.Message);
        }
    }

    [Theory]
    [InlineData("({nested:{facts:[null]},copy:source.present})", "{\"type\":\"object\",\"properties\":{\"nested\":{\"type\":\"object\",\"properties\":{\"facts\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"null\"]}}}}}}", true)]
    [InlineData("({'a/b~c':null})", "{\"type\":\"object\",\"properties\":{\"a/b~c\":{\"type\":[\"string\",\"null\"]}}}", true)]
    [InlineData("({known:null})", "{\"type\":[\"object\",\"null\"],\"properties\":{\"known\":{\"type\":\"string\"}}}", false)]
    [InlineData("({known:source.present,other:null})", "{\"type\":\"object\",\"properties\":{\"known\":{\"type\":[\"string\",\"null\"]},\"other\":{\"type\":\"string\"}}}", false)]
    [InlineData("({fact:null})", "{\"$defs\":{\"row\":{\"type\":\"object\",\"properties\":{\"fact\":{\"$ref\":\"#/$defs/leaf\"}}},\"leaf\":{\"type\":[\"string\",\"null\"]}},\"$ref\":\"#/$defs/row\"}", true)]
    [InlineData("[null,source.present]", "{\"type\":\"array\",\"prefixItems\":[{\"type\":\"null\"},{\"type\":\"string\"}]}", true)]
    [InlineData("[source.present,null]", "{\"type\":\"array\",\"prefixItems\":[{\"type\":[\"string\",\"null\"]},{\"type\":\"string\"}]}", false)]
    public void LiteralNullPermissionIsScopedToTheExactResultPath(string script, string schema, bool allowed)
    {
        var source = new JsonObject { ["present"] = "observed" };
        var target = JsonNode.Parse(schema)!.AsObject();
        if (allowed) Assert.NotNull(new JintSandbox().ExecuteMapping(script, source, Ct, target));
        else Assert.Equal("program", Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(script, source, Ct, target)).Details!["mapping_failure_kind"]!.ToString());
    }

    [Theory]
    [InlineData("nullable", true)]
    [InlineData("unconstrained", false)]
    public void InactiveNullableAlternativeCannotAuthorizeAnotherBranch(string kind, bool allowed)
    {
        var target = JsonNode.Parse("""{"anyOf":[{"type":"object","properties":{"kind":{"const":"nullable"},"fact":{"type":["string","null"]}},"required":["kind","fact"]},{"type":"object","properties":{"kind":{"const":"unconstrained"},"fact":{}},"required":["kind","fact"]}]}""")!.AsObject();
        var source = new JsonObject { ["kind"] = kind };
        if (allowed) Assert.Null(new JintSandbox().ExecuteMapping("({kind:source.kind,fact:null})", source, Ct, target)!["fact"]);
        else Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping("({kind:source.kind,fact:null})", source, Ct, target));
    }

    [Theory]
    [InlineData("'invented'")]
    [InlineData("17")]
    [InlineData("false")]
    [InlineData("m.test(source.text,'known')")]
    public void NullableTargetsStillRejectEveryInventedNonNullScalar(string script)
    {
        var target = JsonNode.Parse("""{"type":["string","number","boolean","null"]}""")!.AsObject();
        Assert.Equal("program", Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(script,
            new JsonObject { ["text"] = "known" }, Ct, target)).Details!["mapping_failure_kind"]!.ToString());
    }

    [Fact]
    public async Task NullablePageFactsUseOneGenericCandidateForAll610RecordsAndWarmCache()
    {
        var pages = new JsonArray(); var expected = new JsonArray();
        for (var page = 0; page < 15; page++)
        {
            var records = new JsonArray(); var facts = new JsonArray();
            for (var index = 0; index < (page == 14 ? 36 : 41); index++)
            {
                var label = "observed-" + page + "-" + index;
                records.Add(new JsonObject { ["kind"] = index % 2 == 0 ? "primary" : "secondary", ["text"] = label,
                    ["href"] = "https://unrelated.invalid/" + page + "/" + index });
                facts.Add(new JsonObject { ["primary"] = index % 2 == 0 ? label : null,
                    ["secondary"] = index % 2 == 0 ? null : label, ["optional"] = null });
            }
            pages.Add(new JsonObject { ["records"] = records }); expected.Add(facts);
        }
        const string script = "item.records.map(r=>({primary:m.test(r.kind,'^primary$')?r.text:null,secondary:m.test(r.kind,'^secondary$')?r.text:null,optional:null}))";
        var doc = AdaptiveDocument(); var schema = doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!;
        schema["items"] = JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"primary":{"type":["string","null"]},"secondary":{"type":["string","null"]},"optional":{"type":["string","null"]}},"required":["primary","secondary","optional"],"additionalProperties":false}}""");
        var model = new Model(script) { Allowance = 96000 }; var cache = new Store(); var journal = new InMemoryWorkflowRunStore();
        WorkflowEngine Engine()
        {
            var configured = AdaptiveEngine(model, cache, calls: 1);
            configured.Limits.MaxMappingInputTokens = 96000; // The retained host's existing allowance.
            return configured;
        }
        var engine = Engine(); engine.RunStore = journal; engine.Limits.RunId = "nullable-pages";
        var run = await AdaptiveRun(engine, pages, doc);
        Assert.True(run.Success, run.Error?.Message); Assert.True(JsonNode.DeepEquals(expected, Result(run)));
        Assert.Single(model.Requests); Assert.Contains("Literal null is allowed only", model.Requests[0].Prompt);
        var saved = (await journal.ReadAsync("tenant", "nullable-pages", Ct))!;
        var controls = saved.Invocations["/workflow/main/step/map"].Control;
        Assert.Equal("generation", controls["mapping_adaptive_0"]!["phase"]!.ToString());
        Assert.Equal(15, controls["mapping_adaptive_0"]!["indices"]!.AsArray().Count);
        Assert.False(controls.ContainsKey("mapping_adaptive_1"));
        var warm = Engine(); warm.LLMClient = null;
        run = await AdaptiveRun(warm, pages, doc);
        Assert.True(run.Success, run.Error?.Message); Assert.True(JsonNode.DeepEquals(expected, Result(run))); Assert.Single(model.Requests);
        var recovered = await new WorkflowEngine { RunStore = journal }.ResumeAsync("tenant", "nullable-pages", saved.Revision,
            new WorkflowCompiler().Compile(doc).Workflows["main"], Ct);
        Assert.True(recovered.Success); Assert.Single(model.Requests);
        Assert.All(cache.Values.Values, artifact => Assert.Equal(JintSandbox.MappingProfileVersion, artifact.ProfileVersion));
        foreach (var key in cache.Values.Keys.ToArray()) cache.Values[key] = cache.Values[key] with { ProfileVersion = JintSandbox.MappingProfileVersion - 1 };
        run = await AdaptiveRun(Engine(), pages, doc);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(2, model.Requests.Count);
    }
}
