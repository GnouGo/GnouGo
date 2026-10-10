using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class DynamicMappingCollectionTests
{
    [Fact]
    public void SourceGroundingDoesNotMakeShortenedReferencesEquivalentIdentities()
    {
        var sources = JsonNode.Parse("""{"pages":[{"records":[{"reference":"generation:record:12","text":""},{"reference":"generation:record:13","text":"Observed label"}]},{"records":[]},{"records":[]},{"records":[]}]}""")!.AsObject();
        var target = JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"},"label":{"type":"string"}},"required":["key","label"],"additionalProperties":false}}""")!.AsObject();
        var sandbox = new JintSandbox();
        foreach (var copy in new[] { "m.text(record.reference,':(record:.+)$')", "record.reference" })
        {
            var rows = sandbox.ExecuteMappingItems("item.records.map(record=>({key:" + copy + ",label:record.text}))", sources, "pages", target, Ct);
            Assert.Equal(4, rows.Count);
            var offered = rows[0]!.DeepClone();
            var selected = new JsonArray(rows[0]![1]!["key"]!.DeepClone(), rows[0]![1]!["key"]!.DeepClone());
            var checkedIds = sandbox.ExecuteMapping("m.lookup(source.records,source.ids,'key')", new JsonObject { ["records"] = offered, ["ids"] = selected.DeepClone() }, Ct);
            Assert.Equal(2, checkedIds!.AsArray().Count);
            var lookupInput = new JsonObject { ["records"] = sources["pages"]![0]!["records"]!.DeepClone(), ["ids"] = selected.DeepClone() };
            if (copy != "record.reference")
                Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => sandbox.ExecuteMapping("m.lookup(source.records,source.ids,'reference')", lookupInput, Ct)).Code);
            else
            {
                var original = sandbox.ExecuteMapping("m.lookup(source.records,source.ids,'reference')", lookupInput, Ct)!.AsArray();
                Assert.Equal(2, original.Count);
                Assert.All(original, value => Assert.True(JsonNode.DeepEquals(sources["pages"]![0]!["records"]![1], value)));
            }
        }
    }
}
