using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class StructuredContinuationTests
{

    [Theory]
    [InlineData("records", "value", false)]
    [InlineData("éléments", "valeur", false)]
    [InlineData("records", "value", true)]
    public void NestedAlternativePathsRequireTheFieldInEveryPossibleResult(string container, string field, bool missing)
    {
        var prep = Preparation(); var graph = Graph(); var workflow = graph.Workflows[0];
        JsonObject Object(string name) => new() { ["type"] = "object", ["properties"] = new JsonObject { [name] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray(name) };
        prep.Capabilities.Add(new()
        {
            Id = "source",
            StepType = "mcp.call",
            OutputSchema = new()
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [container] = new JsonObject
                    { ["anyOf"] = new JsonArray(Object(field), Object(missing ? "absent" : field)) }
                },
                ["required"] = new JsonArray(container)
            }
        });
        workflow.Steps.Insert(0, new() { Key = "source", Type = "mcp.call", CapabilityId = "source" });
        var reference = new PlanningValue { Kind = "output", Source = "source", Path = [container, field] };
        if (missing) Assert.Throws<InvalidOperationException>(() => PlanningGraphValidation.ResolveValueContract(graph, workflow, reference, prep));
        else Assert.Equal(2, PlanningGraphValidation.ResolveValueContract(graph, workflow, reference, prep)["anyOf"]!.AsArray().Count);
    }
}
