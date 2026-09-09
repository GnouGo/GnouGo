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
    [InlineData("offset", "SCENARIO_EXECUTION_FAILED")]
    [InlineData("position/with~escapes.and.dots", "SCENARIO_EXECUTION_FAILED")]
    public async Task RuntimeArgumentFindingRepairsTheCalculationAndPreservesOtherArguments(string field, string code)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0]; var preparation = Preparation();
        workflow.Outputs.Clear();
        var inputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
        { [field] = new JsonObject { ["type"] = "number" }, ["other"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray(field, "other") };
        preparation.Capabilities.Add(new() { Id = "arbitrary", StepType = "mcp.call", InputSchema = inputSchema,
            Server = "renamed", Method = "collect", Kind = "tool" });
        node.Type = "mcp.call"; node.CapabilityId = "arbitrary"; node.OutputSchema = null;
        node.Input = Obj(("server", Str("renamed")), ("method", Str("collect")), ("request", Obj(
            (field, new() { Kind = "compute", Text = "Number(value.missing)", Members = [new("value", Obj(("actual", new() { Kind = "number", Number = 12 })))] }),
            ("other", Str("keep this argument")))));
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
        var executableFinding = new PlanningDiagnostic(code, "workflow:main/step:n_" + PlanningGraphCompiler.Fingerprint(node.Key)[..16] + "/input/request/" + PlanningSchemaReferences.Escape(field), "INPUT_VALIDATION: expected number");
        var mapped = PlanningExecutableValidation.MapRuntimeDiagnostic(executableFinding, graph);
        Assert.Equal("/workflows/0/steps/0/input/request/" + PlanningSchemaReferences.Escape(field), mapped.Location);
        unit.Diagnostics = [mapped with { Location = TypedWorkflowPlanner.PlanningLocation(mapped.Location, graph) }];
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        var coordinate = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.Equal("nodes/greeting/arguments/" + PlanningSchemaReferences.Escape(field), coordinate);
        var before = unit.Candidate.ToJsonString();
        var replacement = new JsonObject { ["kind"] = "compute", ["text"] = "Number(value.actual)",
            ["members"] = unit.Candidate["nodes"]![node.Key]!["arguments"]![field]!["members"]!.DeepClone() };
        var patched = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [coordinate] = replacement }, ["remove"] = new JsonArray() });
        Assert.Equal(before, unit.Candidate.ToJsonString());
        Assert.True(JsonNode.DeepEquals(unit.Candidate["nodes"]![node.Key]!["arguments"]!["other"], patched["nodes"]![node.Key]!["arguments"]!["other"]));
        var repaired = PlanningConstruction.Apply(graph, unit, patched, preparation);
        var document = WorkflowParser.Parse(new PlanningGraphCompiler().Compile(repaired, preparation));
        var factory = new InMemoryMcpClientFactory(); var calls = 0;
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = "collect", InputSchema = inputSchema }], ToolHandlers = new()
            { ["collect"] = request => { calls++; Assert.Equal(12, request![field]!.GetValue<int>()); return new() { Content = new JsonObject() }; } } });
        var compiled = new WorkflowCompiler().Compile(document);
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(1, calls);
        // A coarse diagnostic still must not lock the computation while exposing only its dependencies.
        unit.Diagnostics = [new(code, "/workflows/0/steps/0", "An executable argument is invalid.")];
        var coarse = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation).Context(unit.Candidate);
        Assert.Contains(coordinate, coarse.Select(p => p.Key));
        Assert.DoesNotContain(coarse.Select(p => p.Key), key => key.StartsWith(coordinate + "/members/", StringComparison.Ordinal));
    }

    [Fact]
    public void RepairPromptDropsUnrelatedConsumersWhenTheFieldGroupNarrows()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var graph = Graph(); var workflow = graph.Workflows[0]; var preparation = Preparation();
        state.Graph = graph; state.Preparation = preparation;
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = "return `Example: ```text````;" }));
        workflow.Steps.Add(new() { Key = "unrelated", Type = "set", Purpose = "Unrelated consumer purpose must stay outside this repair", Input = Obj(("message", Str("retained"))), OutputSchema = workflow.Steps[0].OutputSchema });
        var unit = new PlanningConstructionUnit { Key = "group", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = ["greeting", "unrelated"], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
        unit.Diagnostics = PlanningExecutableValidation.Validate(graph, preparation).ToList();
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        var prompt = TypedWorkflowPlanner.UnitRepairPrompt(state, workflow, unit, preparation, patch);
        Assert.DoesNotContain(workflow.Steps[1].Purpose, prompt);
        Assert.Contains("nodes/greeting", prompt);
        Assert.Equal(2, unit.NodeKeys.Count);
    }

    [Theory]
    [InlineData("node.with.dots", "field/with~escapes")]
    [InlineData("noeud.avec.points", "champ/avec~echappements")]
    public void SchemaFailureRepairsOnlyTheInvalidConditionAndPreservesTheGeneratedInput(string key, string field)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0]; var preparation = Preparation();
        node.Key = key; workflow.Outputs.Clear(); node.Input = Obj((field, Str("unchanged")));
        node.OutputSchema = new() { Type = "object", Properties = [new() { Name = field, Required = true, Schema = new() { Type = "string" } }] };
        node.OnError = [new(Str("error"), "stop", null, null)];
        var unit = new PlanningConstructionUnit { Key = "unit/with~escapes", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        unit.Diagnostics = PlanningConstruction.ShapeFindings(unit.Candidate, schema, unit);
        Assert.Equal("/units/unit~1with~0escapes/candidate/nodes/" + key + "/onError/0/if", Assert.Single(unit.Diagnostics).Location);
        node.Input = Obj(); node.OnError = [];
        var patch = PlanningUnitPatches.Create(graph, unit, schema, preparation);
        var coordinate = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.Equal("nodes/" + key + "/onError/0/if", coordinate);
        var fixedCandidate = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [coordinate] = null }, ["remove"] = new JsonArray() });
        Assert.True(JsonNode.DeepEquals(unit.Candidate["nodes"]![key]!["values"], fixedCandidate["nodes"]![key]!["values"]));
        Assert.Equal("stop", fixedCandidate["nodes"]![key]!["onError"]![0]!["action"]!.ToString());
        Assert.Empty(PlanningConstruction.ShapeFindings(fixedCandidate, schema, unit));
    }

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
        Assert.Contains("nodes/greeting/values/" + nested + "/members/0/value", context.Select(p => p.Key));
        Assert.False(TypedWorkflowPlanner.ComputedContractContext(workflow, preparation, context).ContainsKey("nodes/greeting/values/" + nested + "/members/0/value"));
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
