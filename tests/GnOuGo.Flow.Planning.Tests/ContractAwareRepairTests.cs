using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContractAwareRepairTests
{
    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog)> Fixture(bool badDomain = false)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "write", Version = "1", Kind = "tool", StepType = "mcp.call", Server = "arbitrary", Method = "send",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"},"position":{"type":"number"},"side":{"type":"string","enum":["A","B"]},"other":{"type":"string"},"permission":{"type":"string","enum":["deny","limited","all"],"default":"deny"}},"required":["text"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject() } });
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "interpret", Kind = "transform", Objective = "Interpret the supplied observations", Inputs = [new("raw", new() { Kind = "string", Text = "observations" })],
                ResultType = new() { Kind = "object", Fields = [
                    new() { Name = "position", Type = new() { Kind = "integer", Nullable = true } },
                    new() { Name = "side", Type = new() { Kind = "string", Nullable = true, Enum = badDomain ? ["A", "C"] : ["A", "B"] } },
                    new() { Name = "unrelated", Type = new() { Kind = "string" } }] } },
            new() { Id = "consume", Kind = "operation", Operation = "write", Objective = "Publish the observations", Inputs = [
                new("text", new() { Kind = "string", Text = "message" }),
                new("position", new() { Kind = "output", Source = "interpret", Port = "position" }),
                new("side", new() { Kind = "output", Source = "interpret", Port = "side" }),
                new("other", new() { Kind = "string", Text = "unchanged" }),
                new("permission", new() { Kind = "string", Text = "deny" })] }
        ] } };
        return (plan, catalog);
    }

    [Fact]
    public async Task DiagnosedOptionalBindingsCanBeRemovedWithoutChangingTheirProducer()
    {
        var (plan, catalog) = await Fixture(); var baseline = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        Assert.Contains(findings, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/consume/inputs/position");
        var revised = Clone(plan); revised.Root.Tasks[1].Inputs.RemoveAll(i => i.Name is "position" or "side");
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, TaskPlanRevisions.Scope(plan, findings), catalog));
        var result = new TaskPlanCompiler().Compile(revised, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        PlanningConfirmationGuards.Apply(result.Graph, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        Assert.Equal(baseline, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullableProducerRepairExposesOnlyIncompatibleConstraintLeaves(bool badDomain)
    {
        var (plan, catalog) = await Fixture(badDomain);
        var failed = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(failed.Graph);
        var scope = TaskPlanRevisions.Scope(plan, failed.Diagnostics);
        Assert.Contains("/tasks/interpret/resultType/fields/position/type/nullable", scope);
        Assert.Contains("/tasks/interpret/resultType/fields/side/type/nullable", scope);
        Assert.Equal(badDomain, scope.Contains("/tasks/interpret/resultType/fields/side/type/enum"));
        Assert.Equal(badDomain ? 5 : 4, scope.Count);
        var revised = Clone(plan);
        revised.Root.Tasks[0].ResultType!.Fields[0].Type.Nullable = false;
        revised.Root.Tasks[0].ResultType!.Fields[1].Type.Nullable = false;
        if (badDomain) revised.Root.Tasks[0].ResultType!.Fields[1].Type.Enum = ["A", "B"];
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        Assert.Empty(new TaskPlanCompiler().Compile(revised, catalog).Diagnostics);
        revised.Root.Tasks[0].ResultType!.Fields[2].Type.Nullable = true;
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, scope, catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Theory]
    [InlineData("required")]
    [InlineData("unrelated")]
    [InlineData("ordering")]
    [InlineData("permission")]
    [InlineData("operation")]
    [InlineData("objective")]
    [InlineData("producer-fields")]
    [InlineData("replace-all")]
    public async Task OptionalRemovalCannotHideUnrelatedEdits(string mutation)
    {
        var (plan, catalog) = await Fixture(); var baseline = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var revised = Clone(plan); var consumer = revised.Root.Tasks[1];
        consumer.Inputs.RemoveAll(i => i.Name is "position" or "side");
        switch (mutation)
        {
            case "required": consumer.Inputs.RemoveAll(i => i.Name == "text"); break;
            case "unrelated": consumer.Inputs.RemoveAll(i => i.Name == "other"); break;
            case "ordering": consumer.Inputs.Reverse(); break;
            case "permission": consumer.Inputs.Single(i => i.Name == "permission").Value.Text = "all"; break;
            case "operation": consumer.Operation = "different"; break;
            case "objective": consumer.Objective = "Changed intent"; break;
            case "producer-fields": revised.Root.Tasks[0].ResultType!.Fields.RemoveAt(0); break;
            case "replace-all": consumer.Inputs.Clear(); break;
        }
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, scope, catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Equal(baseline, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Fact]
    public async Task OptionalNullDiagnosticExplainsOmissionAndKeepsTheEnum()
    {
        var (plan, catalog) = await Fixture();
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        var side = Assert.Single(findings, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/consume/inputs/side");
        Assert.Contains("omit", side.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", side.Message); Assert.Contains("[\"A\",\"B\"]", side.Message);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("missing-contract")]
    [InlineData("conflicting-contract")]
    [InlineData("mapping-requiredness")]
    [InlineData("duplicate-binding")]
    [InlineData("ambiguous-name")]
    public async Task DiagnosedRemovalStillRequiresUnambiguousAuthoritativeOptionality(string variant)
    {
        var (plan, catalog) = await Fixture(); var capability = catalog.Capabilities.Single(c => c.Id == "write");
        var name = "position";
        switch (variant)
        {
            case "required": capability.InputSchema["required"]!.AsArray().Add(name); break;
            case "missing-contract": catalog.Capabilities.Remove(capability); break;
            case "conflicting-contract": catalog.Capabilities.Add(capability); break;
            case "mapping-requiredness":
                capability.Operation = TaskOperations.Describe(capability);
                capability.InputSchema["required"]!.AsArray().Add(name); break;
            case "duplicate-binding": plan.Root.Tasks[1].Inputs.Add(new(name, new() { Kind = "null" })); break;
            case "ambiguous-name":
                name = "position/type";
                plan.Root.Tasks[1].Inputs[1] = new(name, plan.Root.Tasks[1].Inputs[1].Value);
                capability.InputSchema["properties"]![name] = capability.InputSchema["properties"]!["position"]!.DeepClone();
                capability.InputSchema["properties"]!.AsObject().Remove("position"); break;
        }
        var revised = Clone(plan); revised.Root.Tasks[1].Inputs.RemoveAll(i => i.Name == name);
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, ["/tasks/consume/inputs/" + name], catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task FullRequestValidationRejectsOmissionWhenAnotherArgumentRequiresIt()
    {
        var (plan, catalog) = await Fixture(); var cap = catalog.Capabilities.Single(c => c.Id == "write");
        cap.InputSchema["if"] = JsonNode.Parse("""{"properties":{"other":{"const":"unchanged"}},"required":["other"]}""");
        cap.InputSchema["then"] = JsonNode.Parse("""{"required":["position"]}""");
        var revised = Clone(plan); revised.Root.Tasks[1].Inputs.RemoveAll(i => i.Name is "position" or "side");
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        var compiled = new TaskPlanCompiler().Compile(revised, catalog); Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph!, catalog));
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var errors = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).ValidateAsync(
            new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compiled.Graph!)), PlannerFixture.Ct);
        Assert.Contains(errors, d => d.Code == "MCP_REQUEST_SCHEMA_INVALID" && d.Message.Contains("missing required property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NullableNestedObjectsAndArraysExposeOnlyTheirOwnAndAffectedChildConstraints()
    {
        var (plan, catalog) = await Fixture(); var producer = plan.Root.Tasks[0]; var consumer = plan.Root.Tasks[1];
        var record = new TaskType { Kind = "object", Nullable = true, Fields = producer.ResultType!.Fields };
        producer.ResultType.Fields = [new() { Name = "records", Type = new() { Kind = "array", Nullable = true, Items = record } }];
        var cap = catalog.Capabilities.Single(c => c.Id == "write");
        cap.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"records":{"type":"array","items":{"type":"object","properties":{"position":{"type":"number"},"side":{"type":"string","enum":["A","B"]}},"required":["position","side"]}}},"required":["records"]}""")!.AsObject();
        consumer.Inputs = [new("records", new() { Kind = "output", Source = producer.Id, Port = "records" })];
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        const string path = "/tasks/interpret/resultType/fields/records/type";
        Assert.Equal(new[] { "/tasks/consume/inputs/records", path + "/items/fields/position/type/nullable",
            path + "/items/fields/side/type/nullable", path + "/items/nullable", path + "/nullable" }, scope);
        var revised = Clone(plan); var array = revised.Root.Tasks[0].ResultType!.Fields[0].Type;
        array.Nullable = false; array.Items!.Nullable = false;
        array.Items.Fields[0].Type.Nullable = false; array.Items.Fields[1].Type.Nullable = false;
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        Assert.Empty(new TaskPlanCompiler().Compile(revised, catalog).Diagnostics);
    }

    [Fact]
    public async Task DependencyDiagnosticIdentifiesTheAncestorAndActualChildScope()
    {
        var (plan, catalog) = await Fixture(); var consumer = plan.Root.Tasks[1]; consumer.DependsOn = ["interpret"];
        plan.Root.Tasks[1] = new() { Id = "nested", Kind = "sequence", Objective = "Use captured observations", Body = new() { Tasks = [consumer] } };
        var diagnostic = Assert.Single(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_DEPENDENCY_UNKNOWN");
        Assert.Equal("/tasks/consume/dependsOn", diagnostic.Location);
        Assert.Contains("interpret", diagnostic.Message); Assert.Contains("/tasks/nested/body", diagnostic.Message);
    }

    [Fact]
    public async Task FreshRepairUsesTheLastCallWithoutChangingLimitsOrGrantingPermission()
    {
        var (plan, catalog) = await Fixture(); var repaired = Clone(plan);
        repaired.Root.Tasks[1].Inputs.RemoveAll(i => i.Name is "position" or "side");
        var state = PlannerFixture.Session(); state.Plan = plan; state.Catalog = catalog;
        state.Requirements = PlannerFixture.Requirements(); state.ModelCalls = 7;
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        var runtime = new TestRuntime { Proposal = new() { Plan = repaired } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Empty(state.Diagnostics);
        Assert.Equal(8, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Single(runtime.Calls);
        Assert.Null(state.ApprovedHash); PlanningArtifactApproval.Verify(state);
        Assert.Equal("deny", state.Plan!.Root.Tasks[1].Inputs.Single(i => i.Name == "permission").Value.Text);
        Assert.DoesNotContain(state.Plan.Root.Tasks[1].Inputs, i => i.Name is "position" or "side");
    }

    [Fact]
    public async Task RenamedOperationsAndReorderedDistractorsKeepTheSameRemovalPermissions()
    {
        var (plan, catalog) = await Fixture(); var cap = catalog.Capabilities.Single(c => c.Id == "write");
        cap.Id = "different"; cap.Server = "other"; cap.Method = "unrelated";
        var consumer = plan.Root.Tasks[1]; consumer.Id = "renamed"; consumer.Operation = cap.Id;
        for (var i = 0; i < 10; i++) catalog.Capabilities.Add(new() { Id = "unused" + i, Version = "1", Kind = "tool", StepType = "mcp.call", Server = "other", Method = "unused" });
        catalog.Capabilities.Reverse();
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var revised = Clone(plan); revised.Root.Tasks[1].Inputs.RemoveAll(i => i.Name is "position" or "side");
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        Assert.Empty(new TaskPlanCompiler().Compile(revised, catalog).Diagnostics);
    }

    [Fact]
    public async Task InvalidLiteralRetainsTheDeclaredEnumAndDefaultWithoutAutomaticReplacement()
    {
        var (plan, catalog) = await Fixture(); var input = plan.Root.Tasks[1].Inputs.Single(i => i.Name == "permission");
        input.Value.Text = "default";
        var finding = Assert.Single(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics,
            d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/consume/inputs/permission");
        Assert.Contains("enum [\"deny\",\"limited\",\"all\"]", finding.Message);
        Assert.Contains("default \"deny\"", finding.Message); Assert.Equal("default", input.Value.Text);
    }

    [Fact]
    public async Task EnumRepairsStillRespectTheCompleteConsumerStringContract()
    {
        var (plan, catalog) = await Fixture();
        plan.Root.Tasks[0].ResultType!.Fields[1].Type.Enum = ["A", "BB"];
        var schema = catalog.Capabilities.Single(c => c.Id == "write").InputSchema["properties"]!["side"]!;
        schema["enum"] = new JsonArray("A", "BB"); schema["minLength"] = 2;
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        Assert.Contains("/tasks/interpret/resultType/fields/side/type/enum", scope);
        var repaired = Clone(plan); repaired.Root.Tasks[0].ResultType!.Fields[0].Type.Nullable = false;
        repaired.Root.Tasks[0].ResultType!.Fields[1].Type.Nullable = false;
        repaired.Root.Tasks[0].ResultType!.Fields[1].Type.Enum = ["BB"];
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope, catalog));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, catalog).Diagnostics);
    }
}
