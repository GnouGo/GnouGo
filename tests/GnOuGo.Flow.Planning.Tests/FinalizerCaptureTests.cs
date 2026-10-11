using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FinalizerCaptureTests
{
    private static TaskValue Output(string task, string? port = null) => new() { Kind = "output", Source = task, Port = port };
    private static TaskValue Bool(bool value) => new() { Kind = "boolean", Boolean = value };

    [Fact]
    public async Task CapturedArtifactKeepsItsDeclaredOriginAndRejectsSameTypedUntrustedFields()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        var consumer = plan.Root.Tasks[1]; plan.Root.Tasks.RemoveAt(1); plan.Root.Outputs.Clear();
        plan.Root.Always = [new() { Id = "finalize", Kind = "sequence", Objective = "Use only available resources", Body = new()
        { Tasks = [new() { Id = "guard", Kind = "conditional", Objective = "Check resource availability", Condition = new() { Kind = "present", Source = "make" },
            Body = new() { Tasks = [consumer] }, Otherwise = new() }] } }];
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        consumer.Inputs[0].Value.Port = "display";
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING");
    }

    [Theory]
    [InlineData("later")]
    [InlineData("sibling")]
    [InlineData("group")]
    public async Task PresenceCannotCrossUnavailableOrReusableScopeBoundaries(string boundary)
    {
        var producer = new PlanTask { Id = "producer", Kind = "value", Objective = "Observe", Outputs = [new("flag", Bool(true))] };
        var check = new PlanTask { Id = "check", Kind = "value", Objective = "Require an available observation", Requires = new() { Kind = "present", Source = "producer" } };
        var plan = new TaskPlan();
        if (boundary == "later") plan.Root.Tasks = [new() { Id = "early", Kind = "sequence", Objective = "Use a preceding ancestor only", Body = new() { Tasks = [check] } }, producer];
        if (boundary == "sibling") plan.Root.Tasks = [new() { Id = "siblings", Kind = "parallel", Objective = "Keep branches separate", Branches = [new() { Tasks = [producer] }, new() { Tasks = [check] }] }];
        if (boundary == "group")
        {
            plan.Root.Tasks = [producer, new() { Id = "call", Kind = "call", Objective = "Invoke a reusable group", Group = "group" }];
            plan.Groups = [new() { Id = "group", Body = new() { Tasks = [check] } }];
        }
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compiled.Graph); Assert.Contains(compiled.Diagnostics, d => d.Code is "TASK_REFERENCE_UNKNOWN" or "TASK_PRESENCE_SCOPE");
    }

    [Theory]
    [InlineData("success", 1, false)]
    [InlineData("success", 1, true)]
    [InlineData("success", 2, false)]
    [InlineData("success", 2, true)]
    [InlineData("null", 2, false)]
    [InlineData("null", 2, true)]
    [InlineData("producer_failure", 1, false)]
    [InlineData("producer_failure", 1, true)]
    [InlineData("producer_failure", 2, false)]
    [InlineData("producer_failure", 2, true)]
    [InlineData("later_failure", 2, false)]
    [InlineData("later_failure", 2, true)]
    [InlineData("report_failure", 2, false)]
    [InlineData("report_failure", 2, true)]
    [InlineData("unknown", 2, false)]
    [InlineData("unknown", 2, true)]
    public async Task NestedFinalizationPreservesAvailablePayloadWithoutEagerAbsentReads(string mode, int depth, bool current)
    {
        var effects = new List<string>(); var reports = new List<JsonNode>(); var factory = new InMemoryMcpClientFactory();
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        var output = JsonNode.Parse("""{"type":"object","properties":{"observation":{"type":["string","null"]}},"required":["observation"],"additionalProperties":false}""")!;
        var reportInput = JsonNode.Parse("""{"type":"object","properties":{"seen":{"type":"boolean"},"record":{"type":["object","null"],"properties":{"observation":{"type":["string","null"]}},"required":["observation"],"additionalProperties":false}},"required":["seen","record"],"additionalProperties":false}""")!;
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "observe", InputSchema = empty, OutputSchema = output, EffectKind = "none" },
            new() { Name = "report", InputSchema = reportInput, OutputSchema = empty, EffectKind = "none" },
            new() { Name = "release", InputSchema = empty, OutputSchema = empty, EffectKind = "none" }], ToolHandlers = new()
        {
            ["observe"] = _ => { effects.Add("observe"); if (mode == "unknown") throw new WorkflowRuntimeException("RUN_NEEDS_RECONCILIATION", "unknown completion");
                return mode == "producer_failure" ? new() { IsError = true, Content = new JsonObject { ["message"] = "verified failure" } }
                    : new() { Content = new JsonObject { ["observation"] = mode == "null" ? null : "exact observed value" } }; },
            ["report"] = input => { effects.Add("report"); reports.Add(input!.DeepClone()); return mode == "report_failure"
                ? new() { IsError = true, Content = new JsonObject { ["message"] = "cannot preserve" } } : new() { Content = new JsonObject() }; },
            ["release"] = _ => { effects.Add("release"); return new() { Content = new JsonObject() }; }
        } });
        var engine = new WorkflowEngine { McpClientFactory = factory, RunStore = new InMemoryWorkflowRunStore(), Limits = new() { TenantId = "test", RunId = mode + depth } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask); var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        PlanTask Op(string id, string method) => new() { Id = id, Objective = method, Operation = catalog.Capabilities.Single(c => c.Method == method).Id };
        var report = Op("persist", "report"); report.Inputs = [new("seen", Output("collect", "seen")), new("record", Output("collect", "record"))];
        var body = new TaskScope { Tasks = [new() { Id = "collect", Kind = "conditional", Objective = "Retain precisely the available observation",
            Condition = new() { Kind = "present", Source = "producer" },
            Body = new() { Outputs = [new("seen", Bool(true)), new("record", Output("producer"))] },
            Otherwise = new() { Outputs = [new("seen", Bool(false)), new("record", new() { Kind = "null" })] } }, report], Always = [Op("cleanup", "release")] };
        for (var i = 1; i < depth; i++) body = new() { Tasks = [new() { Id = "nested" + i, Kind = "sequence", Objective = "Preserve scoped evidence", Body = body }] };
        var plan = new TaskPlan { Root = new() { Tasks = [Op("producer", "observe"), new() { Id = "later", Kind = "value", Objective = "Perform subsequent verified work", Requires = Bool(mode != "later_failure") }],
            Always = [new() { Id = "finalize", Kind = "sequence", Objective = "Report before cleanup", Body = body }] } };
        var planning = new TestRuntime(engine) { Proposal = new() { Requirements = new() { Summary = "Preserve available observations before release", Inputs = [],
            Outcomes = [new("preserve", "Retain the actual available observation before cleanup")] }, Plan = plan } };
        var reviewed = await PlannerFixture.RunAsync(planning);
        Assert.True(reviewed.Status == PlanningStatus.FinalReview, string.Join("; ", reviewed.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Single(planning.Calls); Assert.Equal(0, reviewed.ReplanAttempts);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, current, current); Assert.Empty(compiled.Diagnostics);
        var findings = PlanningExecutableValidation.Validate(compiled.Graph!, catalog);
        Assert.True(findings.Count == 0, string.Join('\n', findings));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "generated", current);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(mode is "success" or "null", result.Success);
        if (mode == "unknown") { Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error!.Code); Assert.Equal(new[] { "observe" }, effects); Assert.Empty(reports); }
        else
        {
            Assert.True(effects.SequenceEqual(new[] { "observe", "report", "release" }), result.Error?.Message + " " + string.Join(',', effects));
            var saved = Assert.Single(reports); Assert.Equal(mode != "producer_failure", saved["seen"]!.GetValue<bool>());
            if (mode == "producer_failure") Assert.Null(saved["record"]);
            else { Assert.True(saved["record"]!.AsObject().ContainsKey("observation")); Assert.Equal(mode == "null" ? null : "exact observed value", saved["record"]!["observation"]?.GetValue<string>()); }
        }
    }
}
