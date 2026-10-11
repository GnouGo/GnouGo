using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactObservationTests
{
    [Theory]
    [InlineData("bundles", "candidates", 52, 1474, false)]
    [InlineData("observations", "decisionRows", 6, 263, false)]
    [InlineData("sources", "matches", 53, 1504, false)]
    [InlineData("bundles", "candidates", 52, 1474, true)]
    public async Task ExplicitNarrowContractAndFlattenKeepAllSourcesAndBoundTheGlobalRequest(string input, string port, int pageCount, int recordCount, bool absent)
    {
        var plan = Plan(false, input, port);
        var row = plan.Root.Tasks[0].ResultType!.Fields[0].Type.Items!;
        var record = ProductTransformationPlan.Obj(("kind", new()), ("label", new()), ("group", new()), ("reference", new()), ("unrelated", new()));
        plan.Inputs[0].Type.Items = ProductTransformationPlan.Obj(("records", new() { Kind = "array", Items = record }));
        plan.Root.Tasks[0].ResultType!.Fields[0].Type.Items = new() { Kind = "array", Items = row };
        plan.Root.Tasks[0].Objective = "For this immediate comparison consumer retain only observed candidate label, group and exact reference. Emit one candidate array per complete page, empty only after checking absence; all other observations stay at source.";
        plan.Root.Tasks[1].Inputs[0] = new(port, FlattenCompilationTests.Flatten(ProductTransformationPlan.Ref("compact", port)));
        // A separate later consumer still receives the original observations; it
        // is not part of the global inference input or a reconstructed snapshot.
        plan.Root.Tasks.Add(new() { Id = "later", Kind = "value", Objective = "Retain complete original observations for later actions", DependsOn = ["compare"],
            Outputs = [new("original", new() { Kind = "input", Source = input })] });
        plan.Root.Outputs.Add(new("original", ProductTransformationPlan.Ref("later", "original")));
        var pages = new JsonArray(); var count = 0; var expectedReferences = new List<string>();
        for (var page = 0; page < pageCount; page++)
        {
            var records = new JsonArray();
            for (var i = 0; i < recordCount / pageCount + (page < recordCount % pageCount ? 1 : 0); i++)
            {
                var candidate = !absent && page == pageCount / 2 && i is 7 or 8;
                var reference = "observed/" + page + "/" + i + "/" + new string('r', 500);
                if (candidate) expectedReferences.Add(reference);
                records.Add(new JsonObject { ["kind"] = candidate ? "candidate" : "note", ["label"] = candidate ? "middle_required" : "unneeded-" + count++,
                    ["group"] = candidate ? "target" : "other", ["reference"] = reference, ["unrelated"] = new string('z', 1000) });
            }
            pages.Add(new JsonObject { ["records"] = records });
        }
        Assert.Equal(recordCount, pages.Sum(p => p!["records"]!.AsArray().Count));
        var values = new JsonObject { [input] = pages, ["complete"] = true }; var original = values.ToJsonString();
        var model = new Model(input, port, nested: true, limit: 96000, flatten: true);
        var result = await Execute(plan, values, model, adaptive: true);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(pages, result.Outputs!["original"]));
        Assert.Equal(original, values.ToJsonString()); Assert.Single(model.Mapping);
        Assert.DoesNotContain("middle_required", model.Mapping[0].Prompt); // Outside sampled complete pages.
        var request = Assert.Single(model.Interpretation); var business = Business(request);
        Assert.Equal(new[] { port, "group" }, business.AsObject().Select(p => p.Key));
        var candidates = business[port]!.AsArray(); Assert.Equal(absent ? 0 : 2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(new[] { "label", "group", "reference" }, c!.AsObject().Select(p => p.Key)));
        Assert.Equal(expectedReferences, candidates.Select(c => c!["reference"]!.ToString()));
        Assert.DoesNotContain("unrelated", request.Prompt); Assert.DoesNotContain("unneeded-", request.Prompt);
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.True(bytes + 4096 < 12000); // A narrow view fits even the existing smaller host allowance.
        Assert.Equal(absent ? [] : new[] { "middle_required", "middle_required" }, result.Outputs["labels"]!.AsArray().Select(v => v!.ToString()));
        output.WriteLine($"pages={pageCount}; records={recordCount}; processed={pageCount}; mapping_calls={model.Mapping.Count}; global_calls={model.Interpretation.Count}; serialized_global_bytes={bytes}; estimate={Model.Estimate(request)}");

        // Flattening a broad contract cannot make it small or authorize dropping
        // data: the same full-request admission still rejects it before inference.
        var broad = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        broad.Root.Tasks.RemoveAt(0);
        broad.Root.Tasks[0].Inputs = [new(port, new() { Kind = "input", Source = input })];
        broad.Root.Tasks.RemoveAt(1); broad.Root.Outputs.RemoveAt(1);
        var rejected = new Model(input, port, limit: 96000);
        var failure = await Execute(broad, values.DeepClone().AsObject(), rejected);
        Assert.False(failure.Success); Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Error!.Code);
        Assert.Empty(rejected.Interpretation); Assert.Empty(rejected.Mapping);
    }
}
