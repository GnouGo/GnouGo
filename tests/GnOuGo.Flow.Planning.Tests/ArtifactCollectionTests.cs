using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArtifactCollectionTests
{
    [Theory]
    [InlineData("renamed", "read_page", "records")]
    [InlineData("autre", "lire_page", "donnees")]
    public async Task CompletedPagesPreserveOriginalRecordsAndDynamicResourceArguments(string server, string method, string field)
    {
        var inputs = JsonNode.Parse("""{"type":"object","properties":{"resource":{"type":"string"}},"required":["resource"],"additionalProperties":false}""")!.AsObject();
        var output = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [field] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray(field), ["additionalProperties"] = false };
        var produced = new McpArtifactContract(1, [new("records", "/" + field, "materialize", "json_array")], []);
        var consumed = new McpArtifactContract(1, [], [new("records", "/" + field, true)]);
        var received = new List<string>(); var resources = new List<string>(); var page = 0;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new() { Tools =
        [new() { Name = method, InputSchema = inputs, OutputSchema = output, ArtifactContract = new(produced, []) },
         new() { Name = "consume", InputSchema = output, ArtifactContract = new(consumed, []) }], ToolHandlers = new()
        {
            [method] = args => { resources.Add(args!["resource"]!.GetValue<string>()); return new() { Content = new JsonObject { [field] = (++page % 2 == 1) ? "[{\"id\":9007199254740993}]" : "[{\"id\":2}]" } }; },
            ["consume"] = args => { received.Add(args![field]!.GetValue<string>()); return new() { Content = new JsonObject() }; }
        } });
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory });
        var request = new PlanningRequest { TenantId = "tenant", Prompt = "Read two pages and consume their original records.", Options = new()
        { ["generator"] = new JsonObject { ["model"] = "fake" }, ["policy"] = new JsonObject { ["allowed_step_types"] = new JsonArray("mcp.call", "loop.sequential") },
          ["capability_preflight"] = new JsonObject { ["mode"] = "explicit", ["requirements"] = new JsonArray(new[] { method, "consume" }.Select(tool => (JsonNode)new JsonObject
          { ["id"] = tool, ["description"] = tool, ["required"] = true, ["alternatives"] = new JsonArray(new JsonObject { ["server"] = server, ["kind"] = "tool", ["method"] = tool }) }).ToArray()) } } };
        var preparation = await runtime.PrepareAsync(request, TestContext.Current.CancellationToken);
        var producer = new PlanningNode { Key = "page", Type = "mcp.call", CapabilityId = preparation.Capabilities.Single(c => c.Method == method).Id, OperationIds = [method], Input = Obj(("request", Obj(("resource", new() { Kind = "input", Source = "resource" })))) };
        var loop = new PlanningNode { Key = "pages", Type = "loop.sequential", Input = Obj(("times", new() { Kind = "number", Number = 2 })), Steps = [producer] };
        var consumer = new PlanningNode { Key = "consumer", Type = "mcp.call", CapabilityId = preparation.Capabilities.Single(c => c.Method == "consume").Id, OperationIds = ["consume"] };
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "resource", Required = true, Schema = new() { Type = "string" } }], Steps = [loop, consumer] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        var binding = Assert.Single(PlanningDataflow.Index(workflow, preparation, graph, consumer.Key).Values, b => b.Value.Kind == "artifact_collection");
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, preparation, graph, producer.Key).Values, b => b.Value.Kind == "artifact_collection");
        Assert.True(PlanningArtifactBindings.Proves(workflow, binding.Value, "records", preparation, graph, []));
        consumer.Input = Obj(("request", Obj((field, binding.Value))));
        Assert.Contains(binding.Id, PlanningArtifactBindings.ArgumentSchema(workflow, consumer, field, preparation, graph)!.ToJsonString());
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        Assert.Contains(ArtifactCollectionExpression.FunctionName, yaml);
        Assert.Empty(await runtime.ValidateAsync(yaml, request, preparation, TestContext.Current.CancellationToken));
        var forged = yaml.Replace("collect_json_arrays(", "String(collect_json_arrays(", StringComparison.Ordinal).Replace("])}", "]))}", StringComparison.Ordinal);
        Assert.NotEqual(yaml, forged);
        Assert.Contains(await runtime.ValidateAsync(forged, request, preparation, TestContext.Current.CancellationToken), d => d.Code == "MCP_ARTIFACT_PROVENANCE_UNPROVEN");
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var resource in new[] { "first", "second" })
        {
            var run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["resource"] = resource }, TestContext.Current.CancellationToken);
            Assert.True(run.Success, run.Error?.Message);
        }
        Assert.Equal(new[] { "first", "first", "second", "second" }, resources);
        Assert.Equal(2, received.Count); Assert.All(received, value => Assert.Equal("[{\"id\":9007199254740993},{\"id\":2}]", value));
        producer.If = new() { Kind = "boolean", Boolean = true };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, preparation, graph, consumer.Key).Values, b => b.Value.Kind == "artifact_collection");
        producer.If = null;
        preparation.Capabilities.Single(c => c.Method == method).ArtifactContract = new(1, [new("records", "/" + field, "materialize")], []);
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, preparation, graph, consumer.Key).Values, b => b.Value.Kind == "artifact_collection");
    }

    [Fact]
    public async Task ChangedCatalogRetryRetainsIntentButRequiresNewBehaviorApproval()
    {
        var state = Session(PlanningStatus.Recovery); state.IntentChecked = true; state.Preparation = Preparation(); state.Graph = Graph();
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = "old"; state.ReviewedGraph = Graph();
        state.ClarificationForms = 1; state.ClarificationQuestions = 3;
        state.Diagnostics = [new("CATALOG_CHANGED", "/catalog", "Metadata changed")];
        var planner = new TypedWorkflowPlanner(); var runtime = new FakeRuntime();
        state = await planner.AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.IntentChecked); Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.Graph); Assert.Null(state.Preparation);
        Assert.Equal(1, state.ClarificationForms); Assert.Equal(3, state.ClarificationQuestions);
        for (var i = 0; i < 4 && state.Status == PlanningStatus.Created; i++) state = await planner.AdvanceAsync(state, new() { Kind = "advance", ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status); Assert.DoesNotContain("intent", runtime.Phases);
    }
}
