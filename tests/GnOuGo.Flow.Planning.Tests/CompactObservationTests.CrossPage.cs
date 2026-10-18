using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Planning.Examples.ProductTransformationPlan;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactObservationTests
{
    [Fact]
    public async Task TypedViewsKeepCrossPageRelationshipsAcrossAll1581RecordsWithoutMappingInference()
    {
        var row = Obj(("label", new()), ("group", new() { Nullable = true }), ("reference", new()), ("unused", new()));
        var pages = new JsonArray(Enumerable.Range(0, 53).Select(page => (JsonNode)new JsonObject
        {
            ["records"] = new JsonArray(Enumerable.Range(0, page == 52 ? 21 : 30).Select(index =>
            {
                var match = page == 25 && index == 29 || page == 26 && index == 0;
                return (JsonNode)new JsonObject { ["label"] = match ? "exact\t— café\n漢🙂" : "label/" + page + "/" + index,
                    ["group"] = page == 0 && index == 0 ? null : match ? "shared" : "group/" + page + "/" + index,
                    ["reference"] = "original/" + page + "/" + index + "/" + new string('r', 500), ["unused"] = new string('x', 1000) };
            }).ToArray())
        }).ToArray());
        var original = pages.ToJsonString();
        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "pages", Type = new() { Kind = "array", Items = Obj(("records", new() { Kind = "array", Items = row })) } }],
            Root = new()
            {
                Tasks = [new() { Id = "page_views", Kind = "foreach", Objective = "Project every record without treating pagination as independent business groups",
                    MaxItems = 100, Items = new() { Kind = "input", Source = "pages" }, Body = new()
                    {
                        Tasks = [new() { Id = "records", Kind = "foreach", Objective = "Retain exact comparison fields", MaxItems = 200,
                            Items = new() { Kind = "field", Port = "records", Items = [new() { Kind = "item" }] }, Body = new()
                            { Outputs = [new("facts", new() { Kind = "object", Members = [
                                new("label", new() { Kind = "field", Port = "label", Items = [new() { Kind = "item" }] }),
                                new("group", new() { Kind = "field", Port = "group", Items = [new() { Kind = "item" }] })] })] } }],
                        Outputs = [new("facts", Ref("records", "facts"))]
                    } },
                    new() { Id = "compare", Kind = "transform", Mode = "interpret", Objective = "Return labels belonging to a group with multiple records across the complete observation, preserving order and repetitions",
                        Inputs = [new("facts", new() { Kind = "flatten", Items = [Ref("page_views", "facts")] })],
                        ResultType = Obj(("labels", new() { Kind = "array", Items = new() })) }],
                Outputs = [new("labels", Ref("compare", "labels")), new("originals", new() { Kind = "input", Source = "pages" })]
            }
        };
        var client = new CrossPageModel();
        var engine = new WorkflowEngine { LLMClient = client, LlmDefaults = new() { Model = "deterministic" },
            Limits = new() { MaxMappingInputTokens = 96000 } };
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(
            new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var request = new PlanningRequest { Options = new() { ["compilation_profile"] = TaskPlanCompiler.CompactProfile } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "cross_page", true, true, true);
        Assert.DoesNotContain("mapping.dynamic", yaml);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["pages"] = pages.DeepClone() }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "exact\t— café\n漢🙂", "exact\t— café\n漢🙂" }, result.Outputs!["labels"]!.AsArray().Select(v => v!.ToString()));
        Assert.Equal(original, result.Outputs["originals"]!.ToJsonString());
        Assert.Equal(original, pages.ToJsonString());
        Assert.Equal(1, client.Calls);
        output.WriteLine($"Complete pages=53, records=1581, global decisions=1, mapping inference=0, request estimate={client.Estimate}, original identities unchanged.");
    }

    private sealed class CrossPageModel : ILLMClient
    {
        internal int Calls, Estimate;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Assert.Equal(1, Calls);
            Estimate = PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject());
            Assert.InRange(Estimate, 1, 96000);
            Assert.DoesNotContain("original/", request.Prompt); Assert.DoesNotContain(new string('x', 100), request.Prompt);
            var facts = Business(request)["facts"]!.AsArray(); Assert.Equal(1581, facts.Count);
            Assert.Null(facts[0]!["group"]);
            var duplicateGroups = facts.Where(f => f!["group"] is not null).GroupBy(f => f!["group"]!.ToString())
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["labels"] = new JsonArray(facts
                .Where(f => f!["group"] is not null && duplicateGroups.Contains(f["group"]!.ToString()))
                .Select(f => f!["label"]!.DeepClone()).ToArray()) } });
        }
    }
}
