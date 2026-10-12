using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class DynamicMappingCollectionTests
{
    [Theory]
    [InlineData("""m.test(item.text,'\S')""")]
    [InlineData("""m.text(item.text,'(\d+)')""")]
    [InlineData("""m.texts(item.text,'(a\.)')""")]
    public void LostRegexEscapesAreProgramFailuresWithSourceLocations(string script)
    {
        var error = Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(script));
        Assert.Equal("program", error.Details!["mapping_failure_kind"]!.ToString());
        Assert.Equal(script.IndexOf('\\'), error.Details["script_start"]!.GetValue<int>());
        Assert.Equal(2, error.Details["script_length"]!.GetValue<int>());
        Assert.DoesNotContain(script, error.Message);
        // Historical compiler-owned syntax still means exactly what JS said.
        JintSandbox.ValidateMapping(script, learned: false);
    }

    [Theory]
    [InlineData("""m.test(item.text,'\\S')""")]
    [InlineData("""m.test(item.text,'\\\\')""")]
    [InlineData("""m.test(item.text,'\u0041\x42\t\n\/')""")]
    [InlineData("""m.test(item.text,"quote\"and'quote")""")]
    public void DeliberateRegexStringEscapesRemainSupported(string script) => JintSandbox.ValidateMapping(script);

    [Fact]
    public async Task LostEscapeRepairsGloballyBeforeItemsAndPreservesIssuedReceiptRecovery()
    {
        const string wrong = """m.text(item.text,'(\S+)')""";
        const string correct = """m.text(item.text,'(\\S+)')""";
        var model = new Model(wrong, correct);
        var store = new InMemoryWorkflowRunStore();
        var engine = AdaptiveEngine(model, calls: 2); engine.RunStore = store; engine.Limits.RunId = "regex-repair";
        var telemetry = new WorkflowTelemetryTests.RecordingTelemetry(); engine.Telemetry = telemetry;
        var result = await AdaptiveRun(engine, JsonNode.Parse("""[{"text":" First "},{"text":"Second","other":true}]""")!.AsArray());
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "First", "Second" }, Result(result).Select(v => v!.ToString()));
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("A locator string does not prove DOM ancestry", model.Requests[0].Prompt);
        Assert.Contains("m.test(item.text,'\\\\S')", model.Requests[0].Prompt);
        var attributes = telemetry.StepSpans.Single(s => s.Name == "map").Attributes;
        Assert.Equal(1, attributes["gnougo.mapping.program_repairs"]);
        Assert.Equal(0, attributes["gnougo.mapping.specializations"]);
        var saved = (await store.ReadAsync("tenant", "regex-repair", Ct))!;
        Assert.Equal("program_repair", saved.Invocations["/workflow/main/step/map"].Control["mapping_adaptive_1"]!["phase"]!.ToString());
        var recovered = await new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "regex-repair", saved.Revision,
            new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(AdaptiveDocument()).Workflows["main"], Ct);
        Assert.True(recovered.Success, recovered.Error?.Message); Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AllEmptyCollectionsAreObservableWithoutChangingSuccessOrWarmCache(bool adaptive, bool observedEmpty)
    {
        var doc = adaptive ? AdaptiveDocument() : Document();
        doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"] =
            JsonNode.Parse("""{"type":"array","items":{"type":"string"}}""");
        var pages = observedEmpty ? JsonNode.Parse("""[{"values":[]},{"values":[]}]""")!.AsArray()
            : JsonNode.Parse("""[{"values":["private original"]},{"values":["second"]}]""")!.AsArray();
        var model = new Model("item.values.filter(value=>m.test(value,'^absent$'))"); var cache = new Store();
        for (var run = 0; run < 2; run++)
        {
            var telemetry = new WorkflowTelemetryTests.RecordingTelemetry();
            var engine = AdaptiveEngine(model, cache, calls: 1); engine.Telemetry = telemetry;
            if (run == 1) engine.LLMClient = null;
            var result = await AdaptiveRun(engine, pages, doc);
            Assert.True(result.Success, result.Error?.Message); Assert.Single(model.Requests);
            var attributes = telemetry.StepSpans.Single(s => s.Name == "map").Attributes;
            Assert.Equal("all_empty_collection_result", attributes["gnougo.mapping.diagnostic"]);
            Assert.Equal(2, attributes["gnougo.mapping.empty_item_results"]);
            Assert.Equal(0L, attributes["gnougo.mapping.result_elements"]);
            Assert.Equal("binding", attributes["gnougo.mapping.consumer_binding"]);
            Assert.DoesNotContain("private original", string.Join(" ", attributes.Values));
            Assert.Equal(run == 1, attributes["gnougo.mapping.cache_hit"]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public void EmptyOrUnknownSelectedIdentitiesNeverReconnect(string selected)
    {
        var source = new JsonObject { ["records"] = new JsonArray(new JsonObject { ["key"] = "opaque:1", ["label"] = "exact" }), ["ids"] = new JsonArray(selected) };
        Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping("m.lookup(source.records,source.ids,'key')", source, Ct));
        source["records"]!.AsArray().Add(new JsonObject { ["key"] = selected, ["label"] = "not a valid blank identity" });
        if (string.IsNullOrWhiteSpace(selected))
            Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping("m.lookup(source.records,source.ids,'key')", source, Ct));
    }

    [Fact]
    public async Task CompactCandidatesPreserveAllPagesWithoutLocatorOrUrlShapeGuesses()
    {
        var pages = new JsonArray(); var expected = new JsonArray();
        var recordIndex = 0;
        for (var page = 0; page < 54; page++)
        {
            var records = new JsonArray(); var candidates = new JsonArray();
            for (var offset = 0; offset < (page < 28 ? 29 : 28); offset++, recordIndex++)
            {
                var key = "snapshot:original:" + recordIndex;
                var text = "  Exact observed label " + recordIndex + "\n";
                var record = new JsonObject { ["kind"] = offset % 3 == 0 ? "heading" : "link", ["reference"] = key, ["text"] = text,
                    ["selector"] = offset % 3 == 0 ? "h2" : "[data-opaque='" + recordIndex + "']",
                    ["href"] = recordIndex % 2 == 0 ? "https://example.invalid/redirect?destination=" + recordIndex : "https://example.invalid/" + recordIndex };
                records.Add(record);
                if (offset % 3 != 0) candidates.Add(new JsonObject { ["key"] = key, ["label"] = text });
            }
            pages.Add(new JsonObject { ["records"] = records }); expected.Add(candidates);
        }
        Assert.Equal(1540, recordIndex);
        var doc = AdaptiveDocument();
        doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"] =
            JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"},"label":{"type":"string"}},"required":["key","label"],"additionalProperties":false}}""");
        var model = new Model("item.records.filter(record=>m.test(record.kind,'^link$')).map(record=>({key:record.reference,label:record.text}))") { Allowance = 96000 };
        var cache = new Store(); var engine = AdaptiveEngine(model, cache, calls: 1); engine.Limits.MaxMappingInputTokens = 96000;
        var result = await AdaptiveRun(engine, pages, doc);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(expected, Result(result)));
        Assert.Single(model.Requests);
        var warm = AdaptiveEngine(model, cache, calls: 1); warm.LLMClient = null;
        result = await AdaptiveRun(warm, pages, doc);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(expected, Result(result))); Assert.Single(model.Requests);
    }
}
