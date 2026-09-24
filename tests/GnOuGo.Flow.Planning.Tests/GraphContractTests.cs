using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphContractTests
{
    [Fact]
    public void ModelContractExcludesExecutorPlumbingAndArbitraryExpressions()
    {
        var root = PlanningSchemas.Proposal(new() { Catalog = new() });
        var contract = root.ToJsonString();
        foreach (var forbidden in new[] { "schemaPointer", "capabilityId", "structuredOutput", "workflow.call", "mcp.call", "expression", "projection", "graph" })
            Assert.DoesNotContain("\"" + forbidden + "\"", contract);
        var value = root["$defs"]!["value"]!.DeepClone().AsObject(); value["$defs"] = root["$defs"]!.DeepClone();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["kind"] = "expression", ["text"] = "data.steps.secret" }, value));
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
    [InlineData("prompt")]
    [InlineData("methods")]
    [InlineData("request_template")]
    public void GeneratedMcpStagesCannotChooseTargetsOrTransformArgumentsImplicitly(string field)
    {
        var graph = new PlanningGraph { Workflows = [new() { Steps = [new() { Key = "read", Type = "mcp.call", CapabilityId = "selected",
            Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj()), (field, PlanningCorpus.Text("unapproved mode"))) }] }] };
        var catalog = new PlanningCatalog { Capabilities = [new() { Id = "selected", StepType = "mcp.call" }] };
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, catalog), d => d.Code == "GENERATED_MCP_MODE_DENIED");
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
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task FinalizerSwitchPresenceProofFollowsRuntimeCasePrecedence(string? caseValue, bool available)
    {
        var graph = PlannerFixture.Greeting();
        var guard = new PlanningValue { Kind = "expression", Text = "data.steps.greet != null" };
        graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Type = "switch", Expr = guard, Cases =
            [new(caseValue, guard, [new() { Key = "consume", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "greet", "message"))) }])] });
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        Assert.Equal(available, !PlanningDataflow.Validate(graph, catalog).Any(d => d.Code == "BINDING_UNAVAILABLE"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionRejectsMissingFieldsAndOpaqueAlternatives(bool opaque)
    {
        var runtime = new PlanningCorpus.Runtime("conditional", new WorkflowEngine { McpClientFactory = new PlanningBenchmarkCases.Environment("conditional").Factory() });
        var request = new PlanningRequest();
        var catalog = await runtime.DiscoverAsync(request, PlannerFixture.Ct);
        var source = Assert.Single(await runtime.Capabilities.ListSourcesAsync(PlannerFixture.Ct));
        var page = await runtime.Capabilities.ListAsync(source.Id, null, PlannerFixture.Ct);
        foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, PlannerFixture.Ct));
        var graph = PlanningCorpus.Graph("conditional", catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var projection = graph.Workflows[0].Steps.Single(n => n.Type == "value.project");
        var paths = projection.Input.Members.Single(m => m.Name == "paths").Value;
        if (opaque) catalog.Capabilities.Single(c => c.Method == "read").OutputSchema.Clear();
        else paths.Items[0].Items.RemoveAt(1); // read.value is absent; read.response.value is declared.
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == (opaque ? "OUTPUT_REFERENCE_INVALID" : "PROJECTION_CONTRACT_INVALID"));
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
