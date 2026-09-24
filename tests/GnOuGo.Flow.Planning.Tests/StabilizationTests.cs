using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

// Sanitized reproductions of retained schema-9 failures. No benchmark identities,
// provider names, private requests or generated responses are required.
public sealed class StabilizationTests
{
    [Theory]
    [InlineData("raise_on_error")]
    [InlineData("detect_result_errors")]
    [InlineData("preserve_optional_nulls")]
    [InlineData("timeout_ms")]
    public async Task AdvertisedDeterministicMcpOptionsSurviveValidation(string option)
    {
        var (graph, catalog) = await McpGraph();
        graph.Workflows[0].Steps[0].Input.Members.Add(new(option,
            option == "timeout_ms" ? PlanningCorpus.Num(1000) : new() { Kind = "boolean", Boolean = false }));
        Assert.Empty(PlanningGeneratedGraph.Validate(graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        Assert.Contains(option + ":", new PlanningGraphCompiler().Compile(graph, catalog));
        await ValidateRuntime(graph, catalog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectErrorDetectionCannotOverrideTheLockedNestedPolicy(bool option)
    {
        var (graph, catalog) = await McpGraph();
        catalog.Capabilities[0].FixedInput["error_policy"] = new JsonObject { ["detect_result_errors"] = true };
        graph.Workflows[0].Steps[0].Input.Members.Add(new("detect_result_errors", new() { Kind = "boolean", Boolean = option }));
        Assert.Equal(!option, PlanningExecutableValidation.Validate(graph, catalog).Any(d => d.Code == "CAPABILITY_BINDING_OVERRIDE"));
        if (option) await ValidateRuntime(graph, catalog);
        else Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, catalog));
    }

    [Theory]
    [InlineData("data.steps.prepare != null")]
    [InlineData("data.steps.prepare != null && data.steps.prepare.response != null && data.steps.prepare.response.value != null")]
    public async Task NullableResultGuardsDoNotConflictWithDeclaredObjectContracts(string expression)
    {
        var (graph, catalog) = await McpGraph();
        graph.Workflows[0].Finally.Add(new() { Key = "cleanup", If = new() { Kind = "expression", Text = expression },
            Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "prepare", "value"))) });
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        await ValidateRuntime(graph, catalog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedPresenceCompilesForStepAndBranchGuards(bool branch)
    {
        var (graph, catalog) = await McpGraph();
        var guard = new PlanningValue { Kind = "present", Source = "prepare" };
        var consumer = new PlanningNode { Key = "consume", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "prepare", "value"))) };
        if (branch) graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Type = "switch", Expr = guard, Cases = [new("true", null, [consumer])] });
        else { consumer.If = guard; graph.Workflows[0].Finally.Add(consumer); }
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        Assert.Contains("!= null", new PlanningGraphCompiler().Compile(graph, catalog));
        await ValidateRuntime(graph, catalog);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("path")]
    [InlineData("channel")]
    [InlineData("self")]
    [InlineData("sibling_branch")]
    public async Task PresenceCannotInventAProducerOrCrossControlScopes(string variant)
    {
        var (graph, catalog) = await McpGraph();
        var guard = new PlanningValue { Kind = "present", Source = "prepare" };
        var consumer = new PlanningNode { Key = "consume", If = guard, Input = PlanningCorpus.Obj() };
        if (variant == "missing") guard.Source = "absent";
        if (variant == "path") guard.Path = ["value"];
        if (variant == "channel") guard.ResultChannel = "structured";
        if (variant == "self") guard.Source = "consume";
        if (variant == "sibling_branch")
        {
            var producer = graph.Workflows[0].Steps[0];
            graph.Workflows[0].Steps = [new() { Key = "parallel", Type = "parallel", Branches = [new([producer]), new([consumer])] }];
        }
        else graph.Workflows[0].Finally.Add(consumer);
        Assert.NotEmpty(PlanningExecutableValidation.Validate(graph, catalog));
    }

    [Fact]
    public async Task PresenceDoesNotEstablishOpaquePayloadsOrAvailabilityInTheFalseBranch()
    {
        var (graph, catalog) = await McpGraph();
        var consume = new PlanningNode { Key = "consume", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "prepare", "value"))) };
        var cleanup = new PlanningNode { Key = "cleanup", Type = "switch", Expr = new() { Kind = "present", Source = "prepare" }, Cases = [new("false", null, [consume])] };
        graph.Workflows[0].Finally.Add(cleanup);
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "BINDING_UNAVAILABLE");
        cleanup.Cases[0] = new("true", null, [consume]);
        catalog.Capabilities[0].OutputSchema.Clear();
        Assert.NotEmpty(PlanningExecutableValidation.Validate(graph, catalog));
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("failure", true)]
    [InlineData("absent", false)]
    [InlineData("cancelled", true)]
    public async Task CleanupPresenceExecutesSafelyWithRenamedCapabilities(string mode, bool reverse)
    {
        var (graph, catalog) = await McpGraph(); var workflow = graph.Workflows[0];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        var effects = new List<string>(); var factory = new InMemoryMcpClientFactory();
        var name = reverse ? "renamed" : "operation"; var producer = workflow.Steps[0]; producer.Key = name;
        workflow.Steps.Add(new() { Key = "work", Type = "mcp.call", CapabilityId = "work", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) });
        workflow.Steps.Add(new() { Key = "after_work", Input = PlanningCorpus.Obj(("finished", new() { Kind = "boolean", Boolean = true })) });
        var cleanup = new PlanningNode { Key = "dispose", Type = "mcp.call", CapabilityId = "dispose", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", name, "value"))))) };
        workflow.Finally.Add(new() { Key = "branch", Type = "switch", Expr = new() { Kind = "present", Source = name }, Cases = [new("true", null, [cleanup])] });
        foreach (var method in new[] { "work", "dispose" })
            catalog.Capabilities.Add(new() { Id = method, StepType = "mcp.call", Server = "source", Kind = "tool", Method = method, EffectKind = "read",
                InputSchema = method == "dispose" ? catalog.Capabilities[0].OutputSchema.DeepClone().AsObject() : catalog.Capabilities[0].InputSchema.DeepClone().AsObject(),
                OutputSchema = catalog.Capabilities[0].OutputSchema.DeepClone().AsObject() });
        var tools = catalog.Capabilities.Select(c => new McpToolInfo { Name = c.Method!, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema }).ToList();
        for (var i = 0; i < 10; i++) tools.Add(new() { Name = "unrelated_" + i, InputSchema = new JsonObject { ["type"] = "object" } });
        if (reverse) tools.Reverse();
        factory.RegisterServer("source", new() { Tools = tools, ToolHandlers = new()
        {
            ["acquire"] = _ => { if (mode != "absent") effects.Add("acquire"); return new() { IsError = mode == "absent", Content = new JsonObject { ["value"] = "resource" } }; },
            ["work"] = _ => { if (mode == "cancelled") cancellation.Cancel(); return new() { IsError = mode == "failure", Content = new JsonObject { ["value"] = "work" } }; },
            ["dispose"] = request => { Assert.Equal("resource", request!["value"]!.ToString()); effects.Add("dispose"); return new() { Content = new JsonObject { ["value"] = "disposed" } }; }
        } });
        await ValidateRuntime(graph, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows["main"], null, cancellation.Token);
        Assert.Equal(mode == "success", result.Success);
        Assert.Equal(mode == "absent" ? Array.Empty<string>() : ["acquire", "dispose"], effects);
    }

    [Fact]
    public async Task PresenceRepairPreservesUnrelatedStagesAndRejectsLockedOptionChanges()
    {
        var (graph, catalog) = await McpGraph();
        graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "prepare", "value"))) });
        var findings = PlanningExecutableValidation.Validate(graph, catalog);
        var scope = PlanningGraphRevisions.Scope(graph, findings);
        Assert.Equal(["main/cleanup"], scope);
        var before = PlannerFixture.Clone(new() { Graph = graph }).Graph!;
        graph.Workflows[0].Finally[0].If = new() { Kind = "present", Source = "prepare" };
        Assert.Empty(PlanningGraphRevisions.Validate(before, graph, scope));
        await ValidateRuntime(graph, catalog);
        catalog.Capabilities[0].FixedInput["raise_on_error"] = true;
        graph.Workflows[0].Steps[0].Input.Members.Add(new("raise_on_error", new() { Kind = "boolean", Boolean = false }));
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, catalog));
    }

    private static async Task ValidateRuntime(PlanningGraph graph, PlanningCatalog catalog)
    {
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Empty(await new TestRuntime().Actual.ValidateAsync(new(yaml, PlannerFixture.Session().Request, catalog,
            PlanningGraphCompiler.CapabilityBindings(graph)), PlannerFixture.Ct));
    }

    internal static async Task<(PlanningGraph Graph, PlanningCatalog Catalog)> McpGraph()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        catalog.Policy.RequireExternalConfirmation = false;
        catalog.Capabilities.Add(new() { Id = "operation", StepType = "mcp.call", Server = "source", Method = "acquire", Kind = "tool", EffectKind = "read",
            InputSchema = new() { ["type"] = "object", ["additionalProperties"] = false },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject() });
        return (new() { Workflows = [new() { Steps = [new() { Key = "prepare", Type = "mcp.call", CapabilityId = "operation", Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj())) }] }] }, catalog);
    }

}
