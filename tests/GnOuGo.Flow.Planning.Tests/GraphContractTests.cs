using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphContractTests
{
    [Fact]
    public void SchemaReferencesCannotCarryConflictingInlineConstraints()
    {
        var catalog = new PlanningCatalog { Capabilities = [new() { Id = "selected", OutputSchema = new() { ["type"] = "number" } }] };
        var root = PlanningSchemas.Proposal(new() { Catalog = catalog });
        var schema = root["$defs"]!["schema"]!.DeepClone().AsObject();
        schema["$defs"] = root["$defs"]!.DeepClone();
        var reference = new JsonObject { ["capabilityId"] = "selected", ["schemaPointer"] = "/output" };
        Assert.Empty(PlanningContractValidation.ValidateInstance(reference, schema));
        var decoded = JsonSerializer.Deserialize(reference, PlanningJsonContext.Default.PlanningSchema)!;
        Assert.Equal("number", PlanningSchemaReferences.Resolve(decoded, catalog)["type"]!.ToString());
        reference["type"] = "any";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(reference, schema));
    }

    [Fact]
    public void GeneratedControlContractsCannotOverrideTheirChildren()
    {
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps[0].Type = "sequence";
        graph.Workflows[0].Steps[0].OutputSchema = new() { Type = "number" };
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, new()),
            d => d.Code == "GENERATED_SCHEMA_OVERRIDE_DENIED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedMcpStagesCannotIntroduceImplicitInference(bool throughInput)
    {
        var stage = new PlanningNode { Key = "read", Type = "mcp.call", CapabilityId = "selected",
            Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) };
        if (throughInput) stage.Input.Members.Add(new("structured_output", PlanningCorpus.Obj()));
        else stage.StructuredOutput = new(new() { Type = "object" });
        var graph = new PlanningGraph { Workflows = [new() { Steps = [stage] }] };
        var catalog = new PlanningCatalog { Capabilities = [new() { Id = "selected", StepType = "mcp.call" }] };
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, catalog), d => d.Code == "GENERATED_INFERENCE_DENIED");
        stage.StructuredOutput = null;
        stage.Input.Members.RemoveAll(m => m.Name == "structured_output");
        Assert.Empty(PlanningGeneratedGraph.Validate(graph, catalog));
    }

    [Theory]
    [InlineData("data.steps.greet!=null", true)]
    [InlineData("null != data.steps['greet'] && true", true)]
    [InlineData("data.steps.greet != null || true", false)]
    [InlineData("data.steps.greet !== null", false)]
    [InlineData("data.steps.other != null", false)]
    public async Task FinalizerBranchInheritsOnlyProvenPresenceConditions(string condition, bool available)
    {
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Type = "switch", Cases =
            [new(null, new() { Kind = "expression", Text = condition },
                [new() { Key = "consume", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "greet", "message"))) }])] });
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var findings = PlanningDataflow.Validate(graph, catalog).ToArray();
        Assert.Equal(available, !findings.Any(d => d.Code == "BINDING_UNAVAILABLE"));
        graph.Workflows[0].Finally[0].Cases.Clear();
        graph.Workflows[0].Finally[0].Default.Add(new() { Key = "unguarded", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "greet", "message"))) });
        Assert.Contains(PlanningDataflow.Validate(graph, catalog), d => d.Code == "BINDING_UNAVAILABLE");
    }

    [Theory]
    [InlineData("local")]
    [InlineData("collections")]
    public async Task RegisteredTransformsCompileWithoutScriptInference(string name)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), TestContext.Current.CancellationToken);
        var graph = PlanningCorpus.Graph(name, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        new PlanningGraphCompiler().Compile(graph, catalog);
    }
}
