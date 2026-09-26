using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstrainedBindingTests
{
    internal static (TaskPlan Plan, PlanningCatalog Catalog) Retained()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConstrainedBindings");
        return (JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "plan.json")), PlanningJsonContext.Default.TaskPlan)!,
            JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "catalog.json")), PlanningJsonContext.Default.PlanningCatalog)!);
    }

    internal static TaskPlan Repair(TaskPlan baseline)
    {
        var json = JsonSerializer.SerializeToNode(baseline, PlanningJsonContext.Default.TaskPlan)!;
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["id"]?.ToString() is "comment_fields" or "make_github_feedback")
                    foreach (var field in obj["resultType"]!["fields"]!.AsArray())
                        if (field!["name"]!.ToString() is "side" or "startSide") field["type"]!["enum"] = new JsonArray("LEFT", "RIGHT");
                        else if (field["name"]!.ToString() == "event") field["type"]!["enum"] = new JsonArray("APPROVE", "REQUEST_CHANGES");
                if (obj["id"]?.ToString() == "cleanup_clone_directory")
                {
                    var binding = obj["inputs"]!.AsArray().Single(n => n!["name"]!.ToString() == "parametersJson")!;
                    binding["value"] = new JsonObject { ["kind"] = "json", ["items"] = new JsonArray(binding["value"]!.DeepClone()) };
                }
                foreach (var child in obj.Select(p => p.Value).ToArray()) Visit(child);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(json);
        return json.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
    }

    [Fact]
    public void RetainedContractsReproduceAllFourConsumerErrorsBeforeLowering()
    {
        var (plan, catalog) = Retained();
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Equal(new[] { "/tasks/add_diff_comment/inputs/side", "/tasks/add_diff_comment/inputs/startSide",
            "/tasks/cleanup_clone_directory/inputs/parametersJson", "/tasks/submit_review_decision/inputs/event" },
            result.Diagnostics.Where(d => d.Code == "TASK_INPUT_TYPE").Select(d => d.Location));
    }

    [Fact]
    public void ExplicitConstraintsAndJsonEncodingCompileWithoutChangingOperations()
    {
        var (plan, catalog) = Retained();
        var repaired = Repair(plan);
        var result = new TaskPlanCompiler().Compile(repaired, catalog);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(result.Graph, catalog);
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(new TaskPlanCompiler().Compile(repaired, catalog).Graph!, catalog));
    }

    [Fact]
    public void RepairIncludesOnlyTheRelatedProducerConstraintSlots()
    {
        var (plan, catalog) = Retained();
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        var scope = TaskPlanRevisions.Scope(plan, findings);
        Assert.Contains("/tasks/comment_fields/resultType/fields/side/type/enum", scope);
        Assert.Contains("/tasks/comment_fields/resultType/fields/startSide/type/enum", scope);
        Assert.Contains("/tasks/make_github_feedback/resultType/fields/event/type/enum", scope);
        Assert.Equal(7, scope.Count);
        Assert.Empty(TaskPlanRevisions.Validate(plan, Repair(plan), scope));
    }

    [Fact]
    public void ConstrainedBindingsDoNotDependOnTaskOperationOrSourceNames()
    {
        var (baseline, catalog) = Retained(); var plan = Repair(baseline);
        var names = TaskPlanRevisions.Tasks(plan).ToDictionary(t => t.Id, t => "renamed_" + t.Id, StringComparer.Ordinal);
        var operations = catalog.Capabilities.Select((c, i) => (c.Id, NewId: "operation-" + i)).ToDictionary(p => p.Id, p => p.NewId, StringComparer.Ordinal);
        foreach (var task in TaskPlanRevisions.Tasks(plan))
        {
            task.Id = names[task.Id]; task.DependsOn = task.DependsOn.Select(n => names[n]).ToList();
            if (task.Operation is not null) task.Operation = operations[task.Operation];
            foreach (var value in TaskPlanCompiler.Values(task))
                if (value.Kind is "output" or "present" && value.Source is not null) value.Source = names[value.Source];
        }
        foreach (var capability in catalog.Capabilities) { capability.Id = operations[capability.Id]; capability.Server = "other-source"; capability.Method = "renamed_" + capability.Method; }
        for (var i = 0; i < 20; i++) catalog.Capabilities.Add(new() { Id = "distractor-" + i, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "other-source", Method = "unused" + i });
        catalog.Capabilities.Reverse();
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph!, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog));
    }

    [Fact]
    public void NestedConsumerRetainsTheAncestorProducerConstraintLocation()
    {
        var (plan, catalog) = Retained(); var submit = plan.Root.Tasks[^1]; submit.DependsOn.Clear();
        plan.Root.Tasks[^1] = new() { Id = "nested", Kind = "sequence", Objective = "Consume the result in a nested scope", Body = new() { Tasks = [submit] } };
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        Assert.Contains(findings, d => d.Code == "TASK_TRANSFORM_CONSTRAINT" && d.Location == "/tasks/make_github_feedback/resultType/fields/event/type/enum");
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("unrelated_type")]
    [InlineData("nullable")]
    [InlineData("operation")]
    [InlineData("order")]
    public void UnrelatedRepairChangesAreRejected(string mutation)
    {
        var (plan, catalog) = Retained(); var baseline = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var repaired = Repair(plan); var producer = TaskPlanRevisions.Tasks(repaired).Single(t => t.Id == "comment_fields");
        switch (mutation)
        {
            case "objective": producer.Objective = "Different intent"; break;
            case "unrelated_type": producer.ResultType!.Fields.Single(f => f.Name == "body").Type.Enum = ["fixed"]; break;
            case "nullable": producer.ResultType!.Fields.Single(f => f.Name == "side").Type.Nullable = true; break;
            case "operation": repaired.Root.Always[0].Operation = "other"; break;
            case "order": repaired.Root.Tasks.Reverse(); break;
        }
        Assert.Contains(TaskPlanRevisions.Validate(plan, repaired, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Equal(baseline, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData("APPROVE", "LEFT", "workflows/review/é東京", true)]
    [InlineData("REQUEST_CHANGES", "RIGHT", "workflows/review/a\"b\\c\n{{value}}${data.secret}", true)]
    [InlineData("COMMENT", "RIGHT", "workflows/review", false)]
    [InlineData("APPROVE", "wrong", "workflows/review", false)]
    public async Task RuntimeChecksConstrainedResultsAndEncodesTheExactCleanupValue(string decision, string side, string path, bool succeeds)
    {
        var (baseline, catalog) = Retained(); var plan = Repair(baseline);
        var fixture = new Execution(decision, side, path); var factory = fixture.Factory(catalog);
        var result = await Execute(plan, catalog, fixture, factory);
        Assert.Equal(succeeds, result.Success);
        Assert.Equal(path, fixture.CleanupPath);
        Assert.Equal("cleanup", fixture.Effects[^1]);
        if (succeeds)
        {
            Assert.Equal(new[] { "comment:" + side, "review:" + decision, "cleanup" }, fixture.Effects);
            Assert.Equal(3, fixture.Calls); // JSON encoding adds no inference.
        }
        else Assert.DoesNotContain(fixture.Effects, e => e.StartsWith("review:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("absent")]
    [InlineData("refuse")]
    public async Task JsonCleanupRetainsFailureCancellationAndPermissionSemantics(string mode)
    {
        var (baseline, catalog) = Retained(); var fixture = new Execution("APPROVE", "RIGHT", "workflows/review");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        fixture.Mode = mode; fixture.Cancellation = cancellation;
        if (mode == "refuse") catalog.Policy.RequireExternalConfirmation = true;
        var result = await Execute(Repair(baseline), catalog, fixture, fixture.Factory(catalog), mode != "refuse", cancellation.Token);
        Assert.False(result.Success);
        if (mode is "absent" or "refuse") { Assert.Null(fixture.CleanupPath); Assert.Empty(fixture.Effects); }
        else Assert.Equal("workflows/review", fixture.CleanupPath);
        Assert.DoesNotContain(fixture.Effects, e => e.StartsWith("review:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("wrong_kind")]
    [InlineData("too_many")]
    public void InvalidEnumDeclarationsFailBeforeLowering(string defect)
    {
        var (baseline, catalog) = Retained(); var plan = Repair(baseline);
        var type = plan.Root.Tasks.Single(t => t.Id == "make_github_feedback").ResultType!.Fields.Single(f => f.Name == "event").Type;
        switch (defect)
        {
            case "empty": type.Enum = []; break;
            case "duplicate": type.Enum = ["A", "A"]; break;
            case "null": type.Enum = [null!]; break;
            case "wrong_kind": type.Kind = "integer"; break;
            case "too_many": type.Enum = Enumerable.Range(0, 257).Select(i => i.ToString()).ToList(); break;
        }
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_TRANSFORM_TYPE" && d.Location.EndsWith("/event/type/enum", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumNullabilityAndStrictWireContractRemainExplicit()
    {
        var type = new TaskType { Kind = "string", Enum = ["LEFT", "RIGHT"], Nullable = true };
        var schema = TaskPlanCompiler.TypeSchema(type);
        Assert.Empty(PlanningContractValidation.ValidateInstance(null, schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create("LEFT"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create("left"), schema));
        type.Nullable = false;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(null, TaskPlanCompiler.TypeSchema(type)));
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        var response = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(response, strict: true));
        var wire = TestRuntime.Response(new() { StructuredOutputSchema = response }, new() { Plan = Repair(Retained().Plan) }).Json!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, response));
        var restored = wire.Deserialize(PlanningJsonContext.Default.PlanningProposal)!.Plan!;
        Assert.Equal(new[] { "APPROVE", "REQUEST_CHANGES" }, restored.Root.Tasks.Single(t => t.Id == "make_github_feedback").ResultType!.Fields.Single(f => f.Name == "event").Type.Enum);
    }

    [Fact]
    public void ApprovalCoversEnumDomainsAndJsonEncoding()
    {
        var (baseline, catalog) = Retained(); var plan = Repair(baseline);
        var graph = new TaskPlanCompiler().Compile(plan, catalog).Graph!;
        var state = PlannerFixture.Session(); state.Plan = plan; state.Catalog = catalog; state.Graph = graph;
        state.Requirements = PlannerFixture.Requirements(); state.Yaml = new PlanningGraphCompiler().Compile(graph, catalog, state.Request.Name);
        PlanningArtifactApproval.Verify(state);
        var hash = PlanningArtifactApproval.Hash(state);
        plan.Root.Tasks.Single(t => t.Id == "make_github_feedback").ResultType!.Fields.Single(f => f.Name == "event").Type.Enum = ["APPROVE"];
        Assert.NotEqual(hash, PlanningArtifactApproval.Hash(state));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
        state.Plan = Repair(baseline); state.Plan.Root.Always[0].Inputs.Single(i => i.Name == "parametersJson").Value.Items[0].Members.Add(new("other", new() { Kind = "string", Text = "added" }));
        Assert.NotEqual(hash, PlanningArtifactApproval.Hash(state));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    private static async Task<GnOuGo.Flow.Core.Models.RunResult> Execute(TaskPlan plan, PlanningCatalog catalog, Execution fixture,
        InMemoryMcpClientFactory factory, bool confirm = true, CancellationToken? cancellation = null)
    {
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        PlanningConfirmationGuards.Apply(compilation.Graph!, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = fixture, LlmDefaults = new() { Model = "mock" }, HumanInputProvider = new PlanningCorpus.Human(confirm) };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["pullRequestUrl"] = "https://example.test/example/project/pull/17", ["reviewInstructions"] = "Check the observed diff." }, cancellation ?? PlannerFixture.Ct);
    }

    private sealed class Execution(string decision, string side, string path) : ILLMClient
    {
        internal List<string> Effects = [];
        internal int Calls;
        internal string? CleanupPath;
        internal string? Mode;
        internal CancellationTokenSource? Cancellation;
        internal InMemoryMcpClientFactory Factory(PlanningCatalog catalog)
        {
            var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
            foreach (var c in catalog.Capabilities)
            {
                server.Tools.Add(new() { Name = c.Method!, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema, EffectKind = c.EffectKind });
                server.ToolHandlers[c.Method!] = input =>
                {
                    if (input!["parametersJson"] is { } parameters)
                    {
                        CleanupPath = JsonNode.Parse(parameters.GetValue<string>())!["path"]!.GetValue<string>(); Effects.Add("cleanup");
                        return new() { Content = JsonNode.Parse("""{"commandName":"rm","shell":null,"workingDirectory":null,"exitCode":0,"success":true,"timedOut":false,"stdout":null,"stderr":null,"outputTruncated":false,"startedAtUtc":null,"finishedAtUtc":null,"durationMs":0}""") };
                    }
                    if (input["event"] is { } action) Effects.Add("review:" + action);
                    else Effects.Add("comment:" + input["side"]);
                    return new() { Content = new JsonObject() };
                };
            }
            factory.RegisterServer("retained-source", server); return factory;
        }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            var properties = request.StructuredOutputSchema!["properties"]!;
            if (properties["owner"] is not null)
            {
                if (Mode == "absent") throw new IOException("Producer unavailable");
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["owner"] = "example", ["repo"] = "project", ["pullNumber"] = 17,
                    ["remoteUrl"] = "https://example.test/example/project", ["pullRef"] = "refs/pull/17/head", ["targetDirectory"] = path } });
            }
            if (Mode == "failure") throw new IOException("Interpretation failed");
            if (Mode == "cancelled") { Cancellation!.Cancel(); ct.ThrowIfCancellationRequested(); }
            var comment = new JsonObject { ["path"] = "source.cs", ["body"] = "Check this change", ["line"] = 3, ["startLine"] = 2, ["side"] = side, ["startSide"] = side };
            return Task.FromResult(new LLMResponse { Json = properties["event"] is null ? comment : new JsonObject
                { ["event"] = decision, ["body"] = "Review summary", ["decision"] = decision, ["summary"] = "Checked changes", ["comments"] = new JsonArray(comment) } });
        }
    }
}
