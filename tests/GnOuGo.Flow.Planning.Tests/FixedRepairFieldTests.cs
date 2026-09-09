using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FixedRepairFieldTests
{
    [Theory]
    [InlineData("error")]
    [InlineData("*")]
    [InlineData("erreur")]
    public void ErrorHandlerGenerationRequiresBooleanPredicatesAndRetainsExplicitUnconditionalHandlers(string label)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0]; var preparation = Preparation();
        var unit = new PlanningConstructionUnit { Key = "predicate-unit", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        node.OnError = [new(Str(label), "stop", null, null)];
        JsonObject Candidate() => PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(Candidate(), schema, unit));
        node.OnError = [new(null, "stop", null, null)];
        Assert.Empty(PlanningConstruction.ShapeFindings(Candidate(), schema, unit));
        node.OnError = [new(new() { Kind = "boolean", Boolean = true }, "stop", null, null)];
        Assert.Empty(PlanningConstruction.ShapeFindings(Candidate(), schema, unit));
        node.OnError = [new(new() { Kind = "compute", Text = "value.length > 0", Members = [new("value", Str(label))] }, "stop", null, null)];
        Assert.Empty(PlanningConstruction.ShapeFindings(Candidate(), schema, unit));
    }

    [Theory]
    [InlineData("summary", "details", "preview")]
    [InlineData("resume", "details", "apercu")]
    public async Task DuplicateLoweringFindingDoesNotRedirectSyntaxRepairToAnUnrelatedField(string unchanged, string broken, string nested)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0]; var preparation = Preparation();
        workflow.Outputs.Clear();
        PlanningValue Invalid() => new() { Kind = "compute", Text = "return `Example: ```text````;" };
        node.Input = Obj((unchanged, Str("retain this value")), (broken, Invalid()),
            (nested, new() { Kind = "compute", Text = "return value;", Members = [new("value", Invalid())] }));
        node.OutputSchema = new() { Type = "object", Properties = new[] { unchanged, broken, nested }.Select(name => new PlanningPort
            { Name = name, Required = true, Schema = new() { Type = "string" } }).ToList() };
        node.OnError = [new(new() { Kind = "string", Text = "error" }, "stop", null, null)];
        var unit = new PlanningConstructionUnit { Key = "syntax-unit", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
        unit.Diagnostics = PlanningExecutableValidation.Validate(graph, preparation).ToList();
        Assert.Equal(2, unit.Diagnostics.Count(d => d.Code == "COMPUTATION_BINDING_INVALID"));
        Assert.Contains(unit.Diagnostics, d => d.Code == "VALUE_LOWERING_INVALID");
        var original = unit.Candidate.ToJsonString();
        // A failing candidate has not been published into the retained graph.
        node.Input = Obj(); node.OnError = [];
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        var context = patch.Context(unit.Candidate);
        Assert.DoesNotContain(context.Select(p => p.Key), key => key.EndsWith("/" + unchanged, StringComparison.Ordinal));
        Assert.Equal(3, context.Count);
        Assert.Contains(patch.Narrow().Context(unit.Candidate).Select(p => p.Key), key => key.Contains("/" + broken, StringComparison.Ordinal));
        var changes = new JsonObject();
        foreach (var key in context.Select(p => p.Key))
            changes[key] = key.EndsWith("/if", StringComparison.Ordinal) ? null : new JsonObject { ["kind"] = "compute", ["text"] = "return 'Example: ```text```';", ["members"] = new JsonArray() };
        var candidate = patch.Apply(unit.Candidate, new() { ["changes"] = changes, ["remove"] = new JsonArray() });
        Assert.Equal(original, unit.Candidate.ToJsonString());
        var repaired = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        Assert.Empty(PlanningExecutableValidation.Validate(repaired, preparation));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(repaired, preparation)));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
    }

    [Theory]
    [InlineData("project", "context")]
    [InlineData("repertoire", "contexte")]
    public void ParentFindingCannotNarrowRepairToAnAlreadyCorrectLockedBinding(string locked, string editable)
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input = Obj((locked, Str("fixed")), (editable, Str("old")));
        var fixedValue = new JsonObject { ["kind"] = "binding", ["reference"] = "original-producer" };
        var values = new JsonObject { [locked] = fixedValue, [editable] = new JsonObject { ["kind"] = "string", ["text"] = "old" } };
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"],
            Candidate = new() { ["nodes"] = new JsonObject { ["greeting"] = new JsonObject { ["values"] = values } } },
            Diagnostics = [new("OPERATION_INPUT_BINDING_MISSING", "/workflows/0/steps/0/input", "A required observation is missing.")] };
        var fieldSchemas = new JsonObject { [locked] = PlanningDataflow.BindingSchema(["original-producer"]),
            [editable] = Object(new() { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("string") }, ["text"] = new JsonObject { ["type"] = "string" } }) };
        var schema = Object(new() { ["nodes"] = Object(new() { ["greeting"] = Object(new() { ["values"] = Object(fieldSchemas) }) }) }); schema["$defs"] = new JsonObject();
        var original = unit.Candidate.ToJsonString();
        var patch = PlanningUnitPatches.Create(graph, unit, schema);
        var coordinate = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.EndsWith("/" + editable, coordinate); Assert.Same(patch, patch.Narrow());
        var repaired = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [coordinate] = new JsonObject { ["kind"] = "string", ["text"] = "updated context" } }, ["remove"] = new JsonArray() });
        Assert.True(JsonNode.DeepEquals(fixedValue, repaired["nodes"]!["greeting"]!["values"]![locked])); Assert.Equal(original, unit.Candidate.ToJsonString());
        // With no editable fields, there is no possible model repair to dispatch.
        fieldSchemas.Remove(editable); schema["properties"]!["nodes"]!["properties"]!["greeting"]!["properties"]!["values"]!["required"] = new JsonArray(locked);
        values.Remove(editable);
        Assert.True(PlanningUnitPatches.Create(graph, unit, schema).IsEmpty);
        // A wrong value under the same fixed schema remains repairable.
        fixedValue["reference"] = "wrong-producer";
        Assert.Single(PlanningUnitPatches.Create(graph, unit, schema).Context(unit.Candidate));
    }

    private static JsonObject Object(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false };
}
