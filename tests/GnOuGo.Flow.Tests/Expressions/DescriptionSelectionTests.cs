using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed class DescriptionSelectionTests
{
    // Business expectations are supplied independently of the deterministic
    // adapter. This tests exact reconnection, not a heuristic or an LLM's judgment.
    [Theory]
    [InlineData("features", "feature:2", "feature:1|feature:2")]
    [InlineData("headed", "description:1", "description:1")]
    [InlineData("absent", null, "")]
    [InlineData("controls", null, "")]
    [InlineData("uncertain", null, "")]
    public void DescriptionCasesPreserveTheSelectedCompleteOriginal(string scenario, string? selected, string acceptable)
    {
        var originals = new JsonArray
        {
            new JsonObject { ["reference"] = "control:1", ["text"] = "Report a problem" },
            new JsonObject { ["reference"] = "control:2", ["text"] = "Description" }
        };
        if (scenario == "features")
        {
            originals.Add(new JsonObject { ["reference"] = "feature:1", ["text"] = "  Adjustable height\nFits compact spaces.\t é  " });
            originals.Add(new JsonObject { ["reference"] = "feature:2", ["text"] = "Wide base: stable on uneven ground.\nNo assembly needed." });
        }
        if (scenario == "headed") originals.Add(new JsonObject { ["reference"] = "description:1", ["text"] = "Description: complete original block." });
        if (scenario == "uncertain") originals.Add(new JsonObject { ["reference"] = "other:1", ["text"] = "Features of another, unidentified subject." });
        var offered = originals.DeepClone();
        var before = originals.ToJsonString();
        var context = new JsonObject { ["inputs"] = new JsonObject { ["selected"] = selected, ["offered"] = offered, ["originals"] = originals } };
        const string program = "(()=>{const description=data.inputs.selected===null?(()=>{const absent=[null];return absent;})():(()=>{const offered=m.lookup(data.inputs.offered,[data.inputs.selected],'reference');const original=m.lookup(data.inputs.originals,[data.inputs.selected],'reference');const text=original.map(block=>block.text);return text;})();return description;})()";
        var contracts = JsonNode.Parse("""
          {"description":{"source":"description","order":0,"scope":"block0","origin":"description","schema":{"type":"array","items":{"type":["string","null"]},"minItems":1,"maxItems":1}},
           "absent":{"source":"absent","order":0,"scope":"block1","origin":"absence","schema":{"type":"array","items":{"type":"null"}}},
           "offered":{"source":"offered","order":0,"scope":"block2","origin":"offered","schema":{"type":"array","minItems":1,"maxItems":1}},
           "original":{"source":"original","order":1,"scope":"block2","origin":"original","schema":{"type":"array","minItems":1,"maxItems":1}},
           "text":{"source":"text","order":2,"scope":"block2","origin":"verbatim","schema":{"type":"array","items":{"type":"string"}}}}
          """)!.AsObject();
        var checks = new List<string>();
        var result = new ExpressionEvaluator().EvaluateCheckedExpression(program, context, contracts,
            observeCheck: (_, origin, _) => checks.Add(origin));
        Assert.Equal(selected is null ? new[] { "absence", "description" } : new[] { "offered", "original", "verbatim", "description" }, checks);
        if (selected is null) { Assert.Equal("", acceptable); Assert.Null(result![0]); }
        else
        {
            Assert.Contains(selected, acceptable.Split('|'));
            Assert.Equal(originals.Single(r => r!["reference"]!.ToString() == selected)!["text"]!.ToJsonString(), result![0]!.ToJsonString());
        }
        Assert.Equal(before, originals.ToJsonString());
        if (selected is not null)
        {
            context["inputs"]!["selected"] = "shortened";
            Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator().EvaluateCheckedExpression(program, context, contracts));
        }
    }
}
