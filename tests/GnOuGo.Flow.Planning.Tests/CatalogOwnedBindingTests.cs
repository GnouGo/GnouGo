using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CatalogOwnedBindingTests
{
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskPlan Clone(TaskPlan plan) => JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog, PlanningCapability Capability)> Fixture(bool request)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Policy.RequireExternalConfirmation = false;
        var cap = new PlanningCapability { Id = "opaque_operation", Version = "1", Kind = request ? "tool" : "registered", StepType = request ? "mcp.call" : "set",
            Server = request ? "arbitrary_source" : null, Method = request ? "unrelated_name" : null, EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"selector":{"type":"string","enum":["host"]},"text":{"type":"string"}},"required":["selector","text"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")!.AsObject() };
        if (request) cap.RequestBindings.Add(new("/selector", JsonValue.Create("host")));
        else cap.FixedInput["selector"] = "host";
        catalog.Capabilities.Add(cap);
        return (new() { Root = new() { Tasks = [new() { Id = "consume", Kind = "operation", Operation = cap.Id, Objective = "Process the supplied text",
            Inputs = [new("text", Text("business data"))] }] } }, catalog, cap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredOwnedInputIsOmittedFromIntentAndInjectedDeterministically(bool request)
    {
        var (plan, catalog, cap) = await Fixture(request);
        var before = JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.NotNull(compilation.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(compilation.Graph, catalog));
        PlanningConfirmationGuards.Apply(compilation.Graph, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog);
        Assert.Empty(await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).ValidateAsync(new(yaml, new(), catalog,
            PlanningGraphCompiler.CapabilityBindings(compilation.Graph)), PlannerFixture.Ct));
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(new TaskPlanCompiler().Compile(Clone(plan), catalog).Graph!, catalog));
        Assert.Equal(before, JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog));
        Assert.DoesNotContain(plan.Root.Tasks[0].Inputs, i => i.Name == "selector");
    }

    [Theory]
    [InlineData(false, "host")]
    [InlineData(false, "other")]
    [InlineData(true, "host")]
    [InlineData(true, "other")]
    public async Task IdenticalAndConflictingAssignmentsAreRejectedBeforeLowering(bool request, string value)
    {
        var (plan, catalog, _) = await Fixture(request);
        plan.Root.Tasks[0].Inputs.Add(new("selector", Text(value)));
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_HOST_OWNED" && d.Location == "/tasks/consume/inputs/selector");
        Assert.All(result.Diagnostics, d => Assert.DoesNotContain("host\"", d.Message));
    }

    [Fact]
    public async Task PromptAndIssuedSchemaExcludeOwnedInputsWithoutChangingReceipts()
    {
        var (plan, catalog, cap) = await Fixture(false);
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Requirements = PlannerFixture.Requirements();
        var before = JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog);
        var prompt = HybridWorkflowPlanner.BuildPrompt(state, []);
        Assert.DoesNotContain("\"name\":\"selector\"", prompt);
        var schema = PlanningSchemas.Proposal(state);
        JsonObject Response()
        {
            var wire = PlanningJsonTransport.TaskPlanPrompt(plan)!;
            foreach (var task in wire["root"]!["tasks"]!.AsArray()) task!["requires"] = null;
            return new() { ["discoveryRequests"] = null, ["clarifications"] = null, ["plan"] = wire };
        }
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(), schema));
        plan.Root.Tasks[0].Inputs.Add(new("selector", Text("host")));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(), schema));
        Assert.Equal(before, JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog));
        // Recovered historical index receipts remain intact, but their new presentation uses exact ownership.
        cap.Kind = "agent";
        state.Discovery.Sources = [new("source", "Retained metadata")];
        state.Discovery.Pages = [new("source", null, [new(cap.Id, "source", "operation", "description", cap.StepType, cap.EffectKind, cap.Version, Operation: TaskOperations.Describe(cap))], null)];
        var receipt = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        Assert.DoesNotContain("\"selector\"", HybridWorkflowPlanner.BuildPrompt(state, []));
        Assert.Equal(receipt, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
    }

    [Fact]
    public async Task DiagnosedOwnedBindingCanOnlyBeRemovedByRepair()
    {
        var (plan, catalog, _) = await Fixture(false); plan.Root.Tasks[0].Inputs.Add(new("selector", Text("other")));
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var revised = Clone(plan); revised.Root.Tasks[0].Inputs.RemoveAll(i => i.Name == "selector");
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        revised = Clone(plan); revised.Root.Tasks[0].Inputs.Single(i => i.Name == "selector").Value.Text = "host";
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, scope, catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
        revised = Clone(plan); revised.Root.Tasks[0].Inputs.Clear();
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, scope, catalog), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog, PlanningCapability Capability)> Nested()
    {
        var (plan, catalog, cap) = await Fixture(true);
        cap.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"options":{"type":"object","properties":{"a/b~c":{"type":"string"},"text":{"type":"string"}},"required":["a/b~c","text"],"additionalProperties":false}},"required":["options"],"additionalProperties":false}""")!.AsObject();
        cap.RequestBindings = [new("/options/a~1b~0c", JsonValue.Create("host"))];
        plan.Root.Tasks[0].Inputs = [new("options", new() { Kind = "object", Members = [new("text", Text("business"))] })];
        return (plan, catalog, cap);
    }

    [Fact]
    public async Task NestedAssemblyRetainsEditableFieldsAndEscapedLiteralPropertyNames()
    {
        var (plan, catalog, cap) = await Nested();
        var before = JsonSerializer.Serialize(cap, PlanningJsonContext.Default.PlanningCapability);
        var editable = PlanningCapabilityArguments.Editable(cap);
        Assert.False(editable.Inputs[0].Schema["properties"]!.AsObject().ContainsKey("a/b~c"));
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(result.Graph, catalog);
        Assert.Contains("a/b~c", yaml); Assert.Contains("business", yaml);
        Assert.Empty(await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).ValidateAsync(
            new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(result.Graph)), PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(cap, PlanningJsonContext.Default.PlanningCapability));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("different")]
    [InlineData("reference")]
    public async Task NestedOverridesAndOpaqueAncestorBindingsAreRejected(string mode)
    {
        var (plan, catalog, _) = await Nested(); var task = plan.Root.Tasks[0];
        if (mode == "reference")
        {
            plan.Inputs = [new() { Name = "object", Type = new() { Kind = "any" } }];
            task.Inputs = [new("options", new() { Kind = "input", Source = "object" })];
        }
        else task.Inputs[0].Value.Members.Add(new("a/b~c", Text(mode == "same" ? "host" : "other")));
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_HOST_OWNED" && d.Location == "/tasks/consume/inputs/options");
    }

    [Fact]
    public async Task HostCanSupplyAnEntireRequiredContainerWithoutRequiringAnEmptyInput()
    {
        var (plan, catalog, cap) = await Nested();
        cap.InputSchema["properties"]!["options"]!["required"] = new JsonArray("a/b~c");
        plan.Root.Tasks[0].Inputs.Clear();
        Assert.False(PlanningCapabilityArguments.Editable(cap).Inputs.Single().Required);
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("ancestor")]
    [InlineData("pointer")]
    [InlineData("unknown")]
    [InlineData("type")]
    [InlineData("interpolation")]
    public async Task InvalidCatalogOwnershipFailsClosedWithoutDisclosingBindingValues(string variant)
    {
        var (plan, catalog, cap) = await Nested();
        switch (variant)
        {
            case "duplicate": cap.RequestBindings.Add(cap.RequestBindings[0]); break;
            case "ancestor": cap.RequestBindings.Add(new("/options", new JsonObject())); break;
            case "pointer": cap.RequestBindings = [new("/options/a~3b", JsonValue.Create("secret"))]; break;
            case "unknown": cap.RequestBindings = [new("/missing", JsonValue.Create("secret"))]; break;
            case "type": cap.RequestBindings = [new("/options/a~1b~0c", JsonValue.Create(42))]; break;
            case "interpolation": cap.RequestBindings = [new("/options/a~1b~0c", JsonValue.Create("${secret}"))]; break;
        }
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "CATALOG_BINDING_INVALID");
        Assert.All(result.Diagnostics, d => Assert.DoesNotContain("secret", d.Message));
    }

    [Fact]
    public async Task BindingPathsUseSegmentsAndDoNotConfuseBusinessNamesWithExecutorOptions()
    {
        var (plan, catalog, cap) = await Fixture(true);
        cap.RequestBindings.Clear(); cap.FixedInput["error_policy"] = new JsonObject { ["detect_result_errors"] = false };
        plan.Root.Tasks[0].Inputs.Add(new("selector", Text("host")));
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        cap.RequestBindings.Add(new("/selector", JsonValue.Create("host")));
        cap.InputSchema["properties"]!["selectorExtra"] = new JsonObject { ["type"] = "string" };
        plan.Root.Tasks[0].Inputs.RemoveAll(i => i.Name == "selector");
        plan.Root.Tasks[0].Inputs.Add(new("selectorExtra", Text("unrelated")));
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
    }

    [Fact]
    public async Task NestedRepairRemovesOnlyOwnedMemberAndPreservesItsSiblings()
    {
        var (plan, catalog, _) = await Nested();
        plan.Root.Tasks[0].Inputs[0].Value.Members.Add(new("a/b~c", Text("other")));
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var revised = Clone(plan); revised.Root.Tasks[0].Inputs[0].Value.Members.RemoveAll(m => m.Name == "a/b~c");
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        Assert.Empty(new TaskPlanCompiler().Compile(revised, catalog).Diagnostics);
        revised.Root.Tasks[0].Inputs[0].Value.Members[0].Value.Text = "changed";
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
        revised.Root.Tasks[0].Inputs.Clear(); Assert.NotEmpty(TaskPlanRevisions.Validate(plan, revised, scope, catalog));
    }

    [Fact]
    public async Task ExplicitPortAliasesUseTheirContractPathsForOwnership()
    {
        var (plan, catalog, cap) = await Fixture(false);
        cap.Operation = TaskOperations.Describe(cap);
        cap.Operation.Inputs.Single(p => p.Name == "selector").Name = "businessAlias";
        var result = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(result.Diagnostics);
        plan.Root.Tasks[0].Inputs.Add(new("businessAlias", Text("host")));
        result = new TaskPlanCompiler().Compile(plan, catalog); Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_HOST_OWNED" && d.Location.EndsWith("/businessAlias", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherOperationMayLegitimatelyDeclareTheSameBusinessName()
    {
        var (plan, catalog, cap) = await Fixture(false);
        var other = JsonSerializer.SerializeToNode(cap, PlanningJsonContext.Default.PlanningCapability)!.Deserialize(PlanningJsonContext.Default.PlanningCapability)!;
        other.Id = "other"; other.FixedInput.Clear(); catalog.Capabilities.Add(other);
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Requirements = PlannerFixture.Requirements();
        var schema = PlanningSchemas.Proposal(state);
        plan.Root.Tasks[0].Inputs.Add(new("selector", Text("host")));
        JsonObject Response()
        {
            var wire = PlanningJsonTransport.TaskPlanPrompt(plan)!;
            foreach (var task in wire["root"]!["tasks"]!.AsArray()) task!["requires"] = null;
            return new() { ["discoveryRequests"] = null, ["clarifications"] = null, ["plan"] = wire };
        }
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(), schema));
        plan.Root.Tasks[0].Operation = "other";
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(), schema));
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
    }

    [Fact]
    public async Task FullRequestStillEnforcesConditionalRequirementsAfterHostInjection()
    {
        var (plan, catalog, cap) = await Fixture(true);
        cap.InputSchema["properties"]!["extra"] = new JsonObject { ["type"] = "string" };
        var result = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(result.Diagnostics);
        cap.InputSchema["if"] = JsonNode.Parse("""{"properties":{"selector":{"const":"host"}},"required":["selector"]}""");
        cap.InputSchema["then"] = JsonNode.Parse("""{"required":["extra"]}""");
        var preflight = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(preflight.Graph); Assert.Contains(preflight.Diagnostics, d => d.Code == "TASK_INPUT_TYPE");
        Assert.Contains(PlanningExecutableValidation.Validate(result.Graph!, catalog), d => d.Code == "CONTRACT_UNSATISFIED");
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(result.Graph!, catalog));
    }

    [Fact]
    public async Task IndexOnlySelectionIsRejectedBeforeResolutionOrLowering()
    {
        var (plan, catalog, cap) = await Fixture(true); catalog.Capabilities.Remove(cap);
        // An oversized optional contract requires explicit inspection before selection.
        cap.InputSchema["properties"]!["text"]!["description"] = new string('x', 90000);
        plan.Root.Tasks[0].Inputs.Add(new("selector", Text("host")));
        var source = new ExactCatalog(cap); var runtime = new TestRuntime { Capabilities = source,
            Proposal = new() { Plan = plan } };
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Requirements = PlannerFixture.Requirements();
        state.Discovery.Sources = [new("source", "Metadata")];
        state.Discovery.Pages = [new("source", null, [new(cap.Id, "source", "name", "description", cap.StepType, cap.EffectKind, cap.Version,
            Operation: TaskOperations.Describe(cap))], null)];
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(0, source.Resolutions); Assert.Single(runtime.Calls);
        Assert.DoesNotContain(cap.Id, runtime.Calls[0].StructuredOutputSchema!.ToJsonString());
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Null(state.Graph); Assert.Null(state.Yaml);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeRequestAndNullBindingsAreSuppliedByTheHost(bool wholeRequest)
    {
        var (plan, catalog, cap) = await Fixture(true);
        cap.InputSchema["properties"]!["selector"] = new JsonObject { ["type"] = "null" };
        cap.RequestBindings = [new("/selector", null)];
        if (wholeRequest)
        {
            cap.RequestBindings.Clear();
            cap.FixedInput["request"] = new JsonObject { ["selector"] = null, ["text"] = "host text" };
            plan.Root.Tasks[0].Inputs.Clear();
        }
        var result = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(result.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(result.Graph!, catalog);
        Assert.Empty(await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).ValidateAsync(
            new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(result.Graph!)), PlannerFixture.Ct));
    }

    [Theory]
    [InlineData("first_runner")]
    [InlineData("renamed_runner")]
    public async Task ActualDiscoveryPublishesOnlyEditablePortsAndRetainsExactContract(string name)
    {
        var engine = new WorkflowEngine(); engine.AgentTaskRunners[name] = new MetadataRunner();
        var catalog = new Capabilities.CapabilityDiscovery(engine);
        var source = Assert.Single(await catalog.ListSourcesAsync(PlannerFixture.Ct));
        var page = await catalog.ListAsync(source.Id, null, PlannerFixture.Ct);
        var summary = Assert.Single(page.Capabilities);
        Assert.DoesNotContain(summary.Operation!.Inputs, p => p.Name == "runner");
        var exact = await catalog.ResolveAsync(summary, PlannerFixture.Ct);
        Assert.Equal(name, exact.FixedInput["runner"]!.GetValue<string>());
        Assert.Contains("runner", exact.InputSchema["required"]!.AsArray().Select(p => p!.ToString()));
        Assert.Empty(TaskOperations.Validate(exact));
        var state = PlannerFixture.Session(); state.Catalog = new();
        await PlanningDiscoveryContext.ResolveAsync(state, new TestRuntime(engine) { Capabilities = catalog }, summary, PlannerFixture.Ct);
        Assert.Single(state.Discovery.Resolved);
        Assert.Equal(exact.Version, state.Discovery.Resolved[0].Version);
    }

    [Fact]
    public async Task FixedOperationRepairSchemaKeepsOnlyPermittedOperationsAndTheirEditableNames()
    {
        var (plan, catalog, cap) = await Fixture(false);
        var other = JsonSerializer.SerializeToNode(cap, PlanningJsonContext.Default.PlanningCapability)!.Deserialize(PlanningJsonContext.Default.PlanningCapability)!;
        other.Id = "unrelated"; catalog.Capabilities.Add(other);
        plan.Root.Tasks[0].Inputs.Add(new("selector", Text("other")));
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Requirements = PlannerFixture.Requirements(); state.Plan = plan;
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        var schema = PlanningSchemas.Proposal(state);
        Assert.DoesNotContain("unrelated", schema.ToJsonString());
        var corrected = Clone(plan); corrected.Root.Tasks[0].Inputs.RemoveAll(i => i.Name == "selector");
        var request = new LLMRequest { StructuredOutputSchema = schema };
        var response = TestRuntime.PatchResponse(request, state, corrected);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        Assert.Equal("remove", response["patch"]!["edits"]![0]!["action"]!.ToString());
        corrected.Root.Tasks[0].Operation = other.Id;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(TestRuntime.PatchResponse(request, state, corrected), schema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredRepairAcceptsOnlyRemovalAndPreservesRejectedBaseline(bool changeObjective)
    {
        var (plan, catalog, cap) = await Fixture(false); plan.Root.Tasks[0].Inputs.Add(new("selector", Text("other")));
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Requirements = PlannerFixture.Requirements(); state.Plan = plan; state.ModelCalls = 6;
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        state.Discovery.Resolved.Add(cap);
        var baseline = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var receipts = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var revised = Clone(plan); revised.Root.Tasks[0].Inputs.RemoveAll(i => i.Name == "selector");
        if (changeObjective) revised.Root.Tasks[0].Objective = "Unrelated intent";
        var runtime = new TestRuntime { Proposal = new() { Plan = revised } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(7, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts); Assert.Single(runtime.Calls);
        Assert.Equal(receipts, JsonSerializer.Serialize(result.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        if (changeObjective)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == "PLANNING_RESPONSE_INVALID" && d.Location == "/plan");
            Assert.Equal(baseline, JsonSerializer.Serialize(result.Plan, PlanningJsonContext.Default.TaskPlan));
            Assert.Null(result.Graph); Assert.Null(result.Yaml);
        }
        else
        {
            Assert.Equal(PlanningStatus.FinalReview, result.Status); Assert.Empty(result.Diagnostics);
            Assert.DoesNotContain(result.Plan!.Root.Tasks[0].Inputs, i => i.Name == "selector");
            PlanningArtifactApproval.Verify(result);
        }
    }

    private sealed class MetadataRunner : IAgentTaskRunner
    {
        public Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct) => Task.FromResult(new AgentTaskRunnerContract("Declared agent task", AgentTaskContracts.InputSchema));
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext c, CancellationToken ct) => throw new InvalidOperationException("Discovery only");
        public Task<AgentTaskResult> RunAsync(AgentTaskContext c, CancellationToken ct) => throw new InvalidOperationException("No dispatch");
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext c, CancellationToken ct) => throw new InvalidOperationException("No dispatch");
    }

    private sealed class ExactCatalog(PlanningCapability capability) : ICapabilityCatalog
    {
        internal int Resolutions;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("No new discovery");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => throw new InvalidOperationException("No new pages");
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        { Resolutions++; return Task.FromResult(capability); }
    }
}
