using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionValidationTests(ITestOutputHelper output)
{
    [Fact]
    public void OpenAlternativesValidateTheirConcreteBindingsBeforeMatchingClosedInterfaces()
    {
        var plan = Alternatives();
        var open = Open("/root", "/tasks/decision/otherwise");
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), open).Diagnostics);
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root")).Diagnostics,
            d => d.Code == "TASK_BRANCH_OUTPUTS" && d.Location == "/tasks/decision/otherwise/outputs/result");

        plan.Root.Tasks[0].Otherwise!.Outputs = [new("result", Reference("missing"))];
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), open).Diagnostics,
            d => d.Code == "TASK_REFERENCE_UNKNOWN");

        plan.Root.Tasks[0].Otherwise!.Outputs = [new("result", Number(2))];
        plan.Root.Outputs = [new("result", Reference("decision", "result"))];
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open()).Diagnostics);
        Assert.Empty(new TaskPlanCompiler().Compile(plan, new(), Request()).Diagnostics);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("present")]
    [InlineData("dependency")]
    public void UnfinishedContainerCannotProvideDataOrCompletion(string kind)
    {
        var plan = Alternatives();
        var consumer = Value("consumer", kind == "present" ? new() { Kind = "present", Source = "decision" } : Reference("decision", "result"));
        if (kind == "dependency") { consumer.Outputs = []; consumer.DependsOn = ["decision"]; }
        plan.Root.Tasks.Add(consumer);
        var result = TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root", "/tasks/decision/otherwise"));
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_SCOPE_OPEN");
        Assert.Empty(result.Interfaces);
    }

    [Fact]
    public void FrontierReturnsOnlyRequestedVisibleSourcesAndSeparatesFinalizers()
    {
        var plan = Alternatives();
        plan.Root.Tasks.Insert(0, Value("accepted", Number(7)));
        plan.Root.Always = [Value("cleanup", Number(9))];
        var requests = new Dictionary<string, IReadOnlyList<TaskValue>>
        {
            ["/root"] = [Reference("accepted"), Reference("decision", "result"), Reference("cleanup"), Reference("not_found")],
            ["/root/always"] = [Reference("cleanup")]
        };
        var result = TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root", "/tasks/decision/otherwise"), requests);
        Assert.Empty(result.Diagnostics);
        var visible = Assert.Single(result.Interfaces["/root"]);
        Assert.Equal("accepted", visible.Value.Source);
        Assert.Equal("number", visible.Schema["type"]!.ToString());
        Assert.Equal("cleanup", Assert.Single(result.Interfaces["/root/always"]).Value.Source);
        Assert.DoesNotContain(result.Interfaces.Values.SelectMany(v => v), s => s.Value.Source == "branch_value");
    }

    [Fact]
    public void GroupCallsCannotExposeUnfinishedBodiesAndClosedParentsCannotHideOpenChildren()
    {
        var plan = new TaskPlan
        {
            Groups = [new() { Id = "shared", Body = new() { Tasks = [Value("shared_value", Number(1))] } }],
            Root = new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "shared", Objective = "Invoke the shared work" }] }
        };
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root", "/groups/shared/body")).Diagnostics,
            d => d.Code == "TASK_SCOPE_OPEN" && d.Location == "/tasks/invoke/group");
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(Alternatives(), new(), Request(), Open("/tasks/decision/otherwise")).Diagnostics,
            d => d.Code == "TASK_CONSTRUCTION_SCOPE" && d.Location == "/root");
    }

    [Fact]
    public void OpenScopesRetainAncestorCapturesWithoutAllowingSiblingReads()
    {
        var left = new TaskScope { Tasks = [Value("left_value", Reference("seed"))] };
        var right = new TaskScope { Tasks = [Value("right_value", Reference("seed"))] };
        var plan = new TaskPlan { Root = new() { Tasks = [Value("seed", Number(42)), new()
        {
            Id = "parallel_work", Kind = "parallel", Objective = "Perform independent work", Branches = [left, right]
        }] } };
        var open = Open("/root", "/tasks/parallel_work/branches/0", "/tasks/parallel_work/branches/1");
        var result = TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), open,
            new Dictionary<string, IReadOnlyList<TaskValue>> { ["/tasks/parallel_work/branches/1"] = [Reference("seed"), Reference("right_value"), Reference("left_value")] });
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "seed", "right_value" }, result.Interfaces["/tasks/parallel_work/branches/1"].Select(s => s.Value.Source));
        right.Tasks[0].Outputs = [new("value", Reference("left_value"))];
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), open).Diagnostics, d => d.Code == "TASK_REFERENCE_UNKNOWN");
    }

    [Fact]
    public void ConstructionRetainsAssertionAndIdentityValidation()
    {
        var plan = new TaskPlan { Inputs = [new() { Name = "approved", Type = new() { Kind = "boolean" } }] };
        var source = new TaskValue { Kind = "input", Source = "approved" };
        plan.Root.Tasks = [Value("first", Number(1)), Value("second", Number(2))];
        plan.Root.Tasks[0].Requires = source;
        plan.Root.Tasks[1].Requires = new() { Kind = "predicate", Predicate = "not", Items = [source] };
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root")).Diagnostics,
            d => d.Code == "TASK_GUARD_CONTRADICTION");
        plan.Groups = [new() { Id = "duplicate" }, new() { Id = "duplicate" }];
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root")).Diagnostics,
            d => d.Code == "TASK_IDENTITY_INVALID");
    }

    [Fact]
    public void InterfaceRequestsRejectExcessInsteadOfSilentlyTruncating()
    {
        var requests = new Dictionary<string, IReadOnlyList<TaskValue>> { ["/root"] = Enumerable.Range(0, 129).Select(_ => Number(1)).ToArray() };
        var result = TaskPlanCompiler.ValidateConstruction(new(), new(), Request(), Open("/root"), requests);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INTERFACE_LIMIT");
        Assert.Empty(result.Interfaces);
    }

    [Fact]
    public void ConstructionValidationAndRequestedInterfacesDoNotMutateThePlan()
    {
        var plan = Alternatives();
        var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(plan, new(), Request(), Open("/root", "/tasks/decision/otherwise"),
            new Dictionary<string, IReadOnlyList<TaskValue>> { ["/tasks/decision/body"] = [Reference("branch_value")] }).Diagnostics);
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }




    [Fact]
    public async Task LargePolicyCompilesOneHundredAndOneShallowConditionalSiblingTasks()
    {
        var observed = new List<double>();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"],"additionalProperties":false}""")!;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("measurements", new()
        {
            Tools = [new() { Name = "observe", EffectKind = "read", InputSchema = schema, OutputSchema = schema.DeepClone() }],
            ToolHandlers = new() { ["observe"] = args =>
            { observed.Add(args!["value"]!.Deserialize<double>()); return new() { Content = args.DeepClone() }; } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        catalog.Policy.RequireExternalConfirmation = false;
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "observe")).Id;
        TaskScope Observe(string id, int index) => new()
        {
            Tasks = [new() { Id = id, Objective = "Read the selected indexed measurement", Operation = operation, Inputs = [new("value", Number(index))] }],
            Outputs = [new("result", Reference(id))]
        };
        var plan = new TaskPlan();
        for (var i = 0; i < 101; i++) plan.Root.Tasks.Add(new()
        {
            Id = "branch_" + i, Kind = "conditional", Objective = "Return the declared alternative", Condition = new() { Kind = "boolean", Boolean = true },
            Body = Observe("yes_" + i, i), Otherwise = Observe("no_" + i, -i)
        });
        plan.Root.Outputs = [new("result", Reference("branch_100", "result"))];
        var request = Request(); request.Options["compilation_profile"] = "compact-bindings-v15";
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compilation.Diagnostics);
        Assert.True(compilation.Graph!.Workflows.Count > 100);
        Assert.Contains(PlanningExecutableValidation.Validate(compilation.Graph, catalog), d => d.Code == "WORKFLOW_IDENTITIES_INVALID");
        catalog.Policy.MaxStepsTotal = 5_000; catalog.Policy.MaxStepsPerWorkflow = 5_000; catalog.Policy.MaxWorkflows = 1_024;
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog, "shallow", true, true, true, true, true, true, true, true, true, true, true, true, true);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        Assert.Empty(await runtime.ValidateAsync(new(yaml, request, catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph)), PlannerFixture.Ct));
        Assert.True(document.Workflows.Count > 100);
        Assert.Empty(observed);
    }

    [Fact]
    public void FiveHundredPureTasksRetainTheExistingV15FusionLimitAndFailFinalAdmission()
    {
        var plan = new TaskPlan();
        for (var i = 0; i < 500; i++)
        {
            var task = Value("work_" + i, i == 0 ? Number(1) : new()
            {
                Kind = "arithmetic", Text = "add", Items = [Reference("work_" + (i - 1)), Number(1)]
            });
            if (i >= 7) task.DependsOn = ["work_" + (i - 7)];
            plan.Root.Tasks.Add(task);
        }
        plan.Root.Outputs = [new("count", Reference("work_499")), new("earlier", Reference("work_249"))];
        var catalog = Catalog();
        catalog.Policy.MaxStepsTotal = 5_000; catalog.Policy.MaxStepsPerWorkflow = 5_000; catalog.Policy.MaxWorkflows = 1_024;
        var request = Request(); request.Options["compilation_profile"] = "compact-bindings-v15";
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, catalog));
        // Retain the original failed 500-pure-task composition: v15 fuses it into
        // one program beyond the existing runtime allowance. Do not change
        // lowering, increase runtime limits or insert artificial operation breaks.
        var error = Assert.Throws<WorkflowCompilationException>(() => new PlanningGraphCompiler().Compile(compilation.Graph!, catalog, "large", true, true, true, true, true, true, true, true, true, true, true, true, true));
        var finding = Assert.Single(error.Errors);
        Assert.Equal("expression_contracts", finding.Field);
        Assert.Contains("contains 98268 characters; the runtime limit is 65536", finding.Message);
        output.WriteLine(finding.WorkflowName + "/" + finding.StepId + ": " + finding.Message);
    }

    [Fact]
    public async Task FiveHundredOperationTasksWithBroadCrossDependenciesCompileAndExecuteThroughV15()
    {
        var observed = new List<(int Index, double First, double Second)>();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"index":{"type":"number"},"first":{"type":"number"},"second":{"type":"number"}},"required":["index","first","second"],"additionalProperties":false}""");
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("measurements", new()
        {
            Tools = [new() { Name = "observe", EffectKind = "read", InputSchema = schema,
                OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"],"additionalProperties":false}""") }],
            ToolHandlers = new() { ["observe"] = args =>
            {
                var index = (int)args!["index"]!.Deserialize<double>();
                observed.Add((index, args["first"]!.Deserialize<double>(), args["second"]!.Deserialize<double>()));
                return new() { Content = new JsonObject { ["value"] = 1000d + index } };
            } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        catalog.Policy.RequireExternalConfirmation = false;
        catalog.Policy.MaxStepsTotal = 5_000; catalog.Policy.MaxStepsPerWorkflow = 5_000; catalog.Policy.MaxWorkflows = 1_024;
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "observe")).Id;
        var plan = new TaskPlan();
        for (var i = 0; i < 500; i++) plan.Root.Tasks.Add(new()
        {
            Id = "observe_" + i, Objective = "Observe the indexed measurement with its two prior observations", Operation = operation,
            Inputs = [new("index", Number(i)), new("first", i < 20 ? Number(-1) : Reference("observe_" + (i - 20))),
                new("second", i < 21 ? Number(-1) : Reference("observe_" + (i - 21)))],
            DependsOn = i < 37 ? [] : ["observe_" + (i - 37)]
        });
        plan.Root.Outputs = [new("last", Reference("observe_499")), new("earlier", Reference("observe_249"))];
        var request = Request(); request.Options["compilation_profile"] = "compact-bindings-v15";
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog, "large", true, true, true, true, true, true, true, true, true, true, true, true, true);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, request, catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1499d, result.Outputs!["last"]!.GetValue<double>());
        Assert.Equal(1249d, result.Outputs["earlier"]!.GetValue<double>());
        Assert.Equal(500, observed.Count);
        Assert.Equal(500, observed.Select(o => o.Index).Distinct().Count());
        foreach (var (index, first, second) in observed)
        {
            Assert.Equal(index < 20 ? -1d : 1000d + index - 20, first);
            Assert.Equal(index < 21 ? -1d : 1000d + index - 21, second);
        }
    }

    private static PlanningRequest Request() => new() { Options = new() { ["planning_validation_profile"] = "explicit-assertions-v1", ["planning_diagnostics_version"] = "1" } };
    private static HashSet<string> Open(params string[] paths) => new(paths, StringComparer.Ordinal);
    private static TaskValue Number(double value) => new() { Kind = "number", Number = value };
    private static TaskValue Reference(string task, string port = "value") => new() { Kind = "output", Source = task, Port = port };
    private static PlanTask Value(string id, TaskValue value) => new() { Id = id, Kind = "value", Objective = "Preserve the declared value", Outputs = [new("value", value)] };
    private static TaskPlan Alternatives() => new()
    {
        Root = new() { Tasks = [new()
        {
            Id = "decision", Kind = "conditional", Objective = "Select the applicable result", Condition = new() { Kind = "boolean", Boolean = true },
            Body = new() { Tasks = [Value("branch_value", Number(1))], Outputs = [new("result", Reference("branch_value"))] }, Otherwise = new()
        }] }
    };
    private static PlanningCatalog Catalog() => new() { AllowedStepTypes = ["set", "workflow.call", "switch", "parallel", "loop.sequential", "loop.parallel"] };
    private static PlanningGraph Graph(int workflows, int nodes) => new()
    {
        Workflows = Enumerable.Range(0, workflows).Select(w => new PlanningWorkflow
        {
            Key = w == 0 ? "main" : "scope_" + w,
            Steps = Enumerable.Range(0, nodes).Select(n => new PlanningNode { Key = "node_" + n, Input = new() { Kind = "object" } }).ToList()
        }).ToList()
    };
}
