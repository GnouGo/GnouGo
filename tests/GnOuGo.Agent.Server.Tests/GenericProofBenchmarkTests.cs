using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GenericProofBenchmarkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DiagnosticsAreOptInAndDoNotExpandTheHistoricalCorpus()
    {
        Assert.Equal(new[] { "local", "read_transform", "nullable_defaults", "conditional", "collections",
            "protected_cleanup", "review_french", "review_distractors" }, PlanningBenchmarkCases.Names);
        Assert.Equal(PlanningBenchmarkCases.Names, PlanningCorpus.Names);
        foreach (var name in PlanningBenchmarkCases.ProofCases)
        {
            Assert.DoesNotContain(name, PlanningBenchmarkCases.Names);
            Assert.Equal(new[] { name }, PlanningBenchmarkMeasurements.Select(name));
            Assert.NotEmpty(PlanningBenchmarkCases.Prompt(name));
            Assert.NotEqual(PlanningBenchmarkCases.Inputs(name, "nominal").ToJsonString(),
                PlanningBenchmarkCases.Inputs(name, "alternate").ToJsonString());
            Assert.Equal("generic-proof-artifact-v2", SyntheticArtifactOracle.VersionFor(name));
        }
        Assert.Equal("synthetic-artifact-v1", SyntheticArtifactOracle.VersionFor("collections"));
    }

    [Theory]
    [InlineData("nominal", 20, 11)]
    [InlineData("alternate", -4, -1)]
    public async Task SharedIntermediateFeedsTwoActualRuntimeConsumers(string variant, double doubled, double shifted)
    {
        var plan = Pure();
        Assert.Empty(SyntheticArtifactOracle.Validate(PlanningBenchmarkCases.TaskProofCase, plan));
        var engine = new WorkflowEngine();
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(new(), Ct);
        var result = await Execute(plan, catalog, engine, PlanningBenchmarkCases.Inputs(PlanningBenchmarkCases.TaskProofCase, variant));
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(doubled, result.Outputs!["doubled"]!.GetValue<double>());
        Assert.Equal(shifted, result.Outputs["shifted"]!.GetValue<double>());
        Assert.True(PlanningBenchmarkCases.ProofOutputsCorrect(PlanningBenchmarkCases.TaskProofCase, result.Outputs, variant));
    }

    [Theory]
    [InlineData("literal_returns")]
    [InlineData("inlined_shared_value")]
    [InlineData("single_consumer")]
    [InlineData("unused_shared_value")]
    [InlineData("presence_only")]
    public void CorrectNominalConstantsOrUnusedDemonstrationsDoNotProveTheRequestedDataFlow(string defect)
    {
        var plan = Pure();
        if (defect == "literal_returns")
            plan.Root.Outputs = [new("doubled", Number(20)), new("shifted", Number(11))];
        else if (defect == "inlined_shared_value")
        {
            plan.Root.Tasks[1].Outputs = [new("value", Arithmetic("multiply", SumInputs(), Number(2)))];
            plan.Root.Tasks[2].Outputs = [new("value", Arithmetic("add", SumInputs(), Number(1)))];
        }
        else if (defect == "single_consumer")
            plan.Root.Outputs[1] = new("shifted", Output("double_consumer"));
        else if (defect == "presence_only")
        {
            plan.Root.Tasks[1].Requires = new() { Kind = "present", Source = "shared" };
            plan.Root.Tasks[2].Requires = new() { Kind = "present", Source = "shared" };
            plan.Root.Tasks[1].Outputs = [new("value", Number(20))];
            plan.Root.Tasks[2].Outputs = [new("value", Number(11))];
        }
        else
        {
            plan.Root.Tasks[1].Outputs = [new("value", Number(20))];
            plan.Root.Tasks[2].Outputs = [new("value", Number(11))];
        }
        Assert.Contains("missing_shared_intermediate_consumers", SyntheticArtifactOracle.Validate(PlanningBenchmarkCases.TaskProofCase, plan));
    }

    [Fact]
    public async Task StructuralContributionDoesNotSubstituteForTheIndependentNumericOracle()
    {
        var plan = Pure();
        plan.Root.Tasks[1].Outputs = [new("value", Arithmetic("multiply", Output("shared"), Number(3)))];
        Assert.Empty(SyntheticArtifactOracle.Validate(PlanningBenchmarkCases.TaskProofCase, plan));
        var engine = new WorkflowEngine();
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(new(), Ct);
        var result = await Execute(plan, catalog, engine, PlanningBenchmarkCases.Inputs(PlanningBenchmarkCases.TaskProofCase, "nominal"));
        Assert.True(result.Success);
        Assert.False(PlanningBenchmarkCases.ProofOutputsCorrect(PlanningBenchmarkCases.TaskProofCase, result.Outputs, "nominal"));
    }

    [Theory]
    [InlineData("nominal", 7, 15)]
    [InlineData("alternate", -3, 1)]
    public async Task ShippedReadAndReusableCallsExecuteAgainstBothFrozenInputs(string variant, double left, double right)
    {
        var root = Directory.CreateTempSubdirectory("gnougo-proof-runtime-").FullName;
        await using var sample = new LocalExecutionCases(root, CmdExecutable());
        var inputs = sample.Prepare(PlanningBenchmarkCases.ReadGroupProofCase, variant);
        var before = sample.Snapshot();
        var engine = new WorkflowEngine { McpClientFactory = sample.Factory, HumanInputProvider = new PlanningCorpus.Human() };
        var catalog = await Discover(engine, requireExternalConfirmation: true);
        Assert.Empty(sample.Calls);
        Assert.True(JsonNode.DeepEquals(before, sample.Snapshot()));
        var capability = Assert.Single(catalog.Capabilities, c => c.Server == "filesystem" && c.Method == "cmd_run");
        var plan = ReadGroup(TaskOperations.Describe(capability).Id);
        Assert.Empty(SyntheticArtifactOracle.Validate(PlanningBenchmarkCases.ReadGroupProofCase, plan, catalog));
        var client = await sample.Factory.GetClientAsync("filesystem", Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => client.CallToolAsync("cmd_run",
            new JsonObject { ["commandName"] = "cat_file", ["parameters"] = new JsonObject { ["path"] = "seed.txt" } }, Ct));
        Assert.Empty(sample.Calls);
        Assert.True(JsonNode.DeepEquals(before, sample.Snapshot()));
        var reviewed = ReviewRead(plan, catalog, root);
        var authority = reviewed.Authority();
        var approved = await new HybridWorkflowPlanner().AdvanceAsync(reviewed.State, authority.Command(),
            new DelegatedTestReviewAuthority.Runtime(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask), authority), Ct);
        Assert.Equal(PlanningStatus.Approved, approved.Status);
        PlanningArtifactApproval.Verify(approved, authority.Receipt);
        sample.BindProofAuthority(authority);
        var confirmation = new ReviewedConfirmation(authority);
        engine.HumanInputProvider = confirmation;
        engine.Limits = new() { TenantId = approved.Request.TenantId, RunId = approved.Request.SessionId };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(approved.Yaml!));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], inputs, Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(confirmation.Requests);
        Assert.Equal(left, result.Outputs!["leftResult"]!.GetValue<double>());
        Assert.Equal(right, result.Outputs["rightResult"]!.GetValue<double>());
        var observation = sample.Observe(PlanningBenchmarkCases.ReadGroupProofCase, variant, result, null);
        Assert.True(observation["correct"]!.GetValue<bool>(), observation.ToJsonString());
        Assert.Empty(observation["safety_violations"]!.AsArray());
        var call = Assert.Single(sample.Calls);
        Assert.Equal("cat_file", call["command"]!.ToString());
        Assert.Equal("seed.txt", call["path"]!.ToString());
        Assert.True(JsonNode.DeepEquals(before, sample.Snapshot()));
    }

    [Theory]
    [InlineData("missing_read", "missing_exact_sequential_seed_read")]
    [InlineData("extra_read", "missing_exact_sequential_seed_read")]
    [InlineData("late_read", "missing_exact_sequential_seed_read")]
    [InlineData("conditional_read", "missing_exact_sequential_seed_read")]
    [InlineData("wrong_path", "missing_exact_sequential_seed_read")]
    [InlineData("write_command", "missing_exact_sequential_seed_read")]
    [InlineData("read_dependency", "opaque_read_consumed")]
    [InlineData("present_value", "opaque_read_consumed")]
    [InlineData("present_guard", "opaque_read_consumed")]
    [InlineData("nested_present", "opaque_read_consumed")]
    [InlineData("present_condition", "opaque_read_consumed")]
    [InlineData("unused_group", "missing_contributing_parameterized_calls")]
    [InlineData("same_call_twice", "missing_contributing_parameterized_calls")]
    [InlineData("inference", "unavailable_runtime_dependency")]
    [InlineData("wrong_contract", "unverified_seed_read_contract")]
    public void ReadCompositionOracleRejectsUnrequestedEffectsAndNoncontributingGroups(string defect, string finding)
    {
        var catalog = ReadCatalog();
        var plan = ReadGroup("seed_operation");
        var read = plan.Root.Tasks[0];
        switch (defect)
        {
            case "missing_read": plan.Root.Tasks.RemoveAt(0); break;
            case "extra_read": plan.Root.Tasks.Add(Read("second_read", "seed_operation")); break;
            case "late_read": plan.Root.Tasks.RemoveAt(0); plan.Root.Tasks.Add(read); break;
            case "conditional_read": read.Requires = new() { Kind = "boolean", Boolean = true }; break;
            case "wrong_path": read.Inputs[1].Value.Members[0] = new("path", Text("alpha.txt")); break;
            case "write_command": read.Inputs[0] = new("commandName", Text("write_file")); break;
            case "read_dependency": plan.Root.Tasks[1].Inputs[0] = new("operand", Output("audit_read", "content")); break;
            case "present_value": plan.Root.Tasks[1].Inputs[0] = new("operand", new() { Kind = "present", Source = "audit_read" }); break;
            case "present_guard": plan.Root.Tasks[1].Requires = new() { Kind = "present", Source = "audit_read" }; break;
            case "present_condition": plan.Root.Tasks[1].Condition = new() { Kind = "present", Source = "audit_read" }; break;
            case "nested_present": plan.Root.Tasks[1].Inputs[0] = new("operand", new() { Kind = "object", Members =
                [new("nested", new() { Kind = "array", Items = [new() { Kind = "present", Source = "audit_read" }] })] }); break;
            case "unused_group": plan.Root.Outputs = [new("leftResult", Number(7)), new("rightResult", Number(15))]; break;
            case "same_call_twice": plan.Root.Tasks[4].Outputs = [new("value", Arithmetic("add", Output("left_call"), Number(1)))]; break;
            case "inference": plan.Root.Tasks.Add(new() { Id = "unrequested_inference", Kind = "transform", Objective = "Infer an extra result." }); break;
            case "wrong_contract": catalog.Capabilities[0].Server = "another-server"; break;
        }
        Assert.Contains(finding, SyntheticArtifactOracle.Validate(PlanningBenchmarkCases.ReadGroupProofCase, plan, catalog));
    }

    [Theory]
    [InlineData("no_read")]
    [InlineData("extra_read")]
    [InlineData("wrong_path")]
    [InlineData("changed_seed")]
    [InlineData("extra_file")]
    public async Task RuntimeOracleRequiresTheReadTraceAndPreservedFilesystem(string defect)
    {
        var root = Directory.CreateTempSubdirectory("gnougo-proof-observation-").FullName;
        await using var sample = new LocalExecutionCases(root, "/unused-no-process-started");
        sample.Prepare(PlanningBenchmarkCases.ReadGroupProofCase, "nominal");
        var result = new RunResult { Success = true, Outputs = new JsonObject { ["leftResult"] = 7, ["rightResult"] = 15 } };
        sample.Calls.Add(new() { ["tool"] = "cmd_run", ["command"] = "cat_file", ["path"] = "seed.txt",
            ["succeeded"] = true, ["stdout"] = File.ReadAllText(Path.Combine(root, "seed.txt")) });
        Assert.True(sample.Observe(PlanningBenchmarkCases.ReadGroupProofCase, "nominal", result, null)["correct"]!.GetValue<bool>());
        if (defect == "no_read") sample.Calls.Clear();
        if (defect == "extra_read") sample.Calls.Add(new() { ["tool"] = "cmd_run", ["command"] = "cat_file", ["path"] = "seed.txt" });
        if (defect == "wrong_path") sample.Calls[0]["path"] = "alpha.txt";
        if (defect == "changed_seed") File.WriteAllText(Path.Combine(root, "seed.txt"), "changed by the workflow");
        if (defect == "extra_file") File.WriteAllText(Path.Combine(root, "unexpected.txt"), "unrequested effect");
        Assert.False(sample.Observe(PlanningBenchmarkCases.ReadGroupProofCase, "nominal", result, null)["correct"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExactReadAuthorityCannotBeForgedOrTransferredToAnotherArtifactOrWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("gnougo-proof-authority-").FullName;
        await using var sample = new LocalExecutionCases(root, CmdExecutable());
        sample.Prepare(PlanningBenchmarkCases.ReadGroupProofCase, "nominal");
        var engine = new WorkflowEngine { McpClientFactory = sample.Factory };
        var catalog = await Discover(engine);
        var operation = TaskOperations.Describe(Assert.Single(catalog.Capabilities, c => c.Server == "filesystem" && c.Method == "cmd_run")).Id;
        foreach (var defect in new[] { "authorization", "scenario", "source", "workspace", "request_name", "task", "requirement", "obligation", "evidence",
            "receipt_origin", "receipt_tenant", "receipt_session", "receipt_revision", "receipt_artifact", "receipt_requirements", "duplicate_requirements" })
        {
            var fixture = ReviewRead(ReadGroup(operation), catalog, root);
            switch (defect)
            {
                case "authorization": fixture = fixture with { Authorization = fixture.Authorization with { UserAuthorization = "" } }; break;
                case "scenario": fixture = fixture with { Authorization = fixture.Authorization with { Scenario = "synthetic" } }; break;
                case "source": fixture = fixture with { Authorization = fixture.Authorization with { SourceRevision = "unreviewed-source" } }; break;
                case "workspace": fixture = fixture with { Authorization = fixture.Authorization with { WorkspaceRoot = Path.Combine(root, "other") } }; break;
                case "request_name": fixture.State.Request.Name = PlanningBenchmarkCases.TaskProofCase; break;
                case "task": fixture.State.Plan!.Root.Tasks[1].Objective = "Changed reviewed task"; break;
                case "requirement": fixture.Evidence.Requirements[0] = fixture.Evidence.Requirements[0] with { RequirementHash = new('0', 64) }; break;
                case "obligation": fixture.Evidence.Requirements[0].Obligations.Remove("single_seed_read"); break;
                case "evidence": fixture = fixture with { Receipt = fixture.Receipt with { EvidenceHash = new('0', 64) } }; break;
                case "receipt_origin": fixture = fixture with { Receipt = fixture.Receipt with { Origin = "human" } }; break;
                case "receipt_tenant": fixture = fixture with { Receipt = fixture.Receipt with { TenantId = "another-tenant" } }; break;
                case "receipt_session": fixture = fixture with { Receipt = fixture.Receipt with { SessionId = "another-session" } }; break;
                case "receipt_revision": fixture = fixture with { Receipt = fixture.Receipt with { ReviewedRevision = 2 } }; break;
                case "receipt_artifact": fixture = fixture with { Receipt = fixture.Receipt with { ArtifactHash = new('0', 64) } }; break;
                case "receipt_requirements": fixture = fixture with { Receipt = fixture.Receipt with { ReviewedRequirementIds = ["another-requirement"] } }; break;
                case "duplicate_requirements": fixture = fixture with { Receipt = fixture.Receipt with { ReviewedRequirementIds = ["read_and_compute", "read_and_compute"] } }; break;
            }
            fixture = fixture with { Receipt = fixture.Receipt with { AuthorizationHash = AuthorizationHash(fixture.Authorization) } };
            if (defect is "requirement" or "obligation") fixture = fixture with { Receipt = fixture.Receipt with { EvidenceHash = EvidenceHash(fixture.Evidence) } };
            var snapshot = JsonSerializer.Serialize(fixture.State, PlanningJsonContext.Default.PlanningSession);
            Assert.Throws<PlanningConflictException>(() => fixture.Authority().Validate(fixture.State));
            Assert.Equal(snapshot, JsonSerializer.Serialize(fixture.State, PlanningJsonContext.Default.PlanningSession));
        }
        var valid = ReviewRead(ReadGroup(operation), catalog, root);
        var authority = valid.Authority();
        authority.Validate(valid.State);
        sample.BindProofAuthority(authority);
        Assert.Throws<PlanningConflictException>(() => authority.RequireProofWorkspace(Path.Combine(root, "other")));
        Assert.False(DelegatedTestReviewAuthority.AllowsTool("synthetic", "filesystem", "cmd_run"));
        Assert.Empty(sample.Calls);
    }

    [Fact]
    public async Task ReadReceiptStillPassesThroughOrdinaryArtifactApprovalAndRejectsStaleReceipts()
    {
        var root = Directory.CreateTempSubdirectory("gnougo-proof-approval-").FullName;
        await using var sample = new LocalExecutionCases(root, CmdExecutable());
        sample.Prepare(PlanningBenchmarkCases.ReadGroupProofCase, "nominal");
        var engine = new WorkflowEngine { McpClientFactory = sample.Factory };
        var catalog = await Discover(engine);
        var operation = TaskOperations.Describe(Assert.Single(catalog.Capabilities, c => c.Server == "filesystem" && c.Method == "cmd_run")).Id;
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        foreach (var defect in new[] { "revision", "artifact", "reviewer", "owner", "missing_host_authority" })
        {
            var fixture = ReviewRead(ReadGroup(operation), catalog, root);
            fixture = defect switch
            {
                "revision" => fixture with { Receipt = fixture.Receipt with { ReviewedRevision = 2 } },
                "artifact" => fixture with { Receipt = fixture.Receipt with { ArtifactHash = new('0', 64) } },
                "reviewer" => fixture with { Receipt = fixture.Receipt with { Reviewer = "other-reviewer" } },
                "owner" => fixture with { Receipt = fixture.Receipt with { TenantId = "other-tenant" } },
                _ => fixture
            };
            var authority = fixture.Authority();
            var snapshot = JsonSerializer.Serialize(fixture.State, PlanningJsonContext.Default.PlanningSession);
            await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(fixture.State, authority.Command(),
                defect == "missing_host_authority" ? runtime : new DelegatedTestReviewAuthority.Runtime(runtime, authority), Ct));
            Assert.Equal(snapshot, JsonSerializer.Serialize(fixture.State, PlanningJsonContext.Default.PlanningSession));
        }
        var valid = ReviewRead(ReadGroup(operation), catalog, root);
        var accepted = valid.Authority();
        var approved = await new HybridWorkflowPlanner().AdvanceAsync(valid.State, accepted.Command(),
            new DelegatedTestReviewAuthority.Runtime(runtime, accepted), Ct);
        Assert.Equal(PlanningStatus.Approved, approved.Status);
        PlanningArtifactApproval.Verify(approved, accepted.Receipt);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(approved));
        Assert.Single(approved.ValidationResults, result => result.Outcome == "delegated_test_reviewed");
        Assert.Empty(sample.Calls);
    }

    [Fact]
    public async Task ConcreteReadArgumentsAndSymlinkConfinementAreCheckedBeforeTransport()
    {
        var root = Directory.CreateTempSubdirectory("gnougo-proof-confinement-").FullName;
        await using var sample = new LocalExecutionCases(root, CmdExecutable());
        sample.Prepare(PlanningBenchmarkCases.ReadGroupProofCase, "nominal");
        var engine = new WorkflowEngine { McpClientFactory = sample.Factory };
        var catalog = await Discover(engine);
        var operation = TaskOperations.Describe(Assert.Single(catalog.Capabilities, c => c.Server == "filesystem" && c.Method == "cmd_run")).Id;
        var fixture = ReviewRead(ReadGroup(operation), catalog, root);
        var authority = fixture.Authority();
        authority.Validate(fixture.State);
        sample.BindProofAuthority(authority);
        foreach (var defect in new[] { "extra_argument", "extra_parameter", "write", "path", "server", "tool" })
        {
            var args = new JsonObject { ["commandName"] = "cat_file", ["parameters"] = new JsonObject { ["path"] = "seed.txt" } };
            if (defect == "extra_argument") args["workingDirectory"] = root;
            if (defect == "extra_parameter") args["parameters"]!["extra"] = true;
            if (defect == "write") args["commandName"] = "write_file";
            if (defect == "path") args["parameters"]!["path"] = "alpha.txt";
            Assert.Throws<PlanningConflictException>(() => authority.RequireTool(defect == "server" ? "other" : "filesystem",
                defect == "tool" ? "other" : "cmd_run", args));
        }
        File.Delete(Path.Combine(root, "seed.txt"));
        File.CreateSymbolicLink(Path.Combine(root, "seed.txt"), Path.Combine(root, "alpha.txt"));
        Assert.Throws<PlanningConflictException>(() => authority.RequireProofWorkspace(root));
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("filesystem", "cmd_run",
            new JsonObject { ["commandName"] = "cat_file", ["parameters"] = new JsonObject { ["path"] = "seed.txt" } }));
        Assert.Empty(sample.Calls);
    }

    private static TaskPlan Pure() => new()
    {
        Inputs = NumericInputs(),
        Root = new()
        {
            Tasks = [Value("shared", SumInputs()), Value("double_consumer", Arithmetic("multiply", Output("shared"), Number(2))),
                Value("shift_consumer", Arithmetic("add", Output("shared"), Number(1)))],
            Outputs = [new("doubled", Output("double_consumer")), new("shifted", Output("shift_consumer"))]
        }
    };

    private static TaskPlan ReadGroup(string operation) => new()
    {
        Inputs = NumericInputs(),
        Groups = [new() { Id = "double", Inputs = [new() { Name = "operand", Type = new() { Kind = "number" } }],
            Body = new() { Tasks = [Value("group_calculation", Arithmetic("multiply", Input("operand"), Number(2)))],
                Outputs = [new("value", Output("group_calculation"))] } }],
        Root = new()
        {
            Tasks = [Read("audit_read", operation),
                new() { Id = "left_call", Kind = "call", Objective = "Double the supplied left input through the reusable group.", Group = "double", Inputs = [new("operand", Input("left"))] },
                new() { Id = "right_call", Kind = "call", Objective = "Double the supplied right input through the reusable group.", Group = "double", Inputs = [new("operand", Input("right"))] },
                Value("left_finish", Arithmetic("add", Output("left_call"), Number(1))),
                Value("right_finish", Arithmetic("add", Output("right_call"), Number(1)))],
            Outputs = [new("leftResult", Output("left_finish")), new("rightResult", Output("right_finish"))]
        }
    };

    private static List<TaskInput> NumericInputs() => [new() { Name = "left", Type = new() { Kind = "number" } },
        new() { Name = "right", Type = new() { Kind = "number" } }];
    private static PlanTask Value(string id, TaskValue value) => new() { Id = id, Kind = "value", Objective = "Compute the required numeric contribution.", Outputs = [new("value", value)] };
    private static PlanTask Read(string id, string operation) => new() { Id = id, Kind = "operation", Objective = "Read the supplied seed exactly once as the initial audit observation.", Operation = operation,
        Inputs = [new("commandName", Text("cat_file")), new("parameters", new() { Kind = "object", Members = [new("path", Text("seed.txt"))] })] };
    private static TaskValue SumInputs() => Arithmetic("add", Input("left"), Input("right"));
    private static TaskValue Arithmetic(string operation, params TaskValue[] items) => new() { Kind = "arithmetic", Text = operation, Items = [.. items] };
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string task, string port = "value") => new() { Kind = "output", Source = task, Port = port };
    private static TaskValue Number(double number) => new() { Kind = "number", Number = number };
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };

    // This minimal catalog is used only for negative supplementary-oracle checks.
    // Successful compilation and execution above resolve the actual shipped contract.
    private static PlanningCatalog ReadCatalog() => new() { Capabilities = [new() { Id = "seed_operation", Server = "filesystem", Method = "cmd_run", StepType = "mcp.call" }] };

    private sealed record ReadReview(PlanningSession State, DelegatedTestAuthorization Authorization,
        DelegatedReviewEvidence Evidence, PlanningApprovalReview Receipt, string Workspace)
    {
        internal DelegatedTestReviewAuthority Authority() => new(Authorization, new(Receipt, Evidence), "isolated-proof-test",
            "authored-proof-run", PlanningBenchmarkCases.ReadGroupProofCase, Workspace, "authored-proof-source");
    }

    private static string AuthorizationHash(DelegatedTestAuthorization authorization) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization));
    private static string EvidenceHash(DelegatedReviewEvidence evidence) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(evidence, DelegatedTestJsonContext.Default.DelegatedReviewEvidence));

    private sealed class ReviewedConfirmation(DelegatedTestReviewAuthority authority) : IHumanInputProvider
    {
        internal List<HumanInputRequest> Requests { get; } = [];
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(authority.RequireRuntimeRequest(request));
            Requests.Add(request);
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = true });
        }
    }

    private static ReadReview ReviewRead(TaskPlan plan, PlanningCatalog catalog, string workspace)
    {
        const string campaign = "isolated-proof-test", run = "authored-proof-run", source = "authored-proof-source", reviewer = "fixture-reviewer";
        var requirement = new PlanningRequirement("read_and_compute", PlanningBenchmarkCases.Prompt(PlanningBenchmarkCases.ReadGroupProofCase));
        var state = new PlanningSession { IntentVersion = 2, Revision = 3, Status = PlanningStatus.FinalReview, Plan = plan, Catalog = catalog,
            Request = new() { TenantId = "benchmark", SessionId = run, Name = PlanningBenchmarkCases.ReadGroupProofCase, Prompt = requirement.Description },
            Requirements = new() { Outcomes = [requirement], Inputs = NumericInputs(),
                Outputs = [new() { Name = "leftResult", Type = new() { Kind = "number" } }, new() { Name = "rightResult", Type = new() { Kind = "number" } }] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        state.Graph = compilation.Graph!;
        PlanningConfirmationGuards.Apply(state.Graph, catalog);
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, catalog, state.Request.Name);
        var authorization = new DelegatedTestAuthorization("isolated-proof-authorization", "Explicit authored test delegation for this disposable seed read only",
            reviewer, campaign, run, PlanningBenchmarkCases.ReadGroupProofCase, workspace, source);
        var evidence = new DelegatedReviewEvidence([new(requirement.Id,
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(requirement, PlanningJsonContext.Default.PlanningRequirement)),
            ["exact_outputs", "reusable_group_calls", "single_seed_read", "unchanged_workspace"], "accepted_with_recorded_uncertainty")],
            plan.Root.Tasks.Concat(plan.Groups.SelectMany(group => group.Body.Tasks)).Select(task => new DelegatedTaskReview(task.Id,
                PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask)),
                "Authored exact seed read or deterministic contributing group computation; numeric and filesystem results checked independently below.", "reviewed")).ToList());
        var receipt = new PlanningApprovalReview("isolated-proof-review", "delegated_test", reviewer,
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization)),
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(evidence, DelegatedTestJsonContext.Default.DelegatedReviewEvidence)),
            "benchmark", run, state.Revision, state.ComputeArtifactHash()!, [requirement.Id], DateTimeOffset.UtcNow);
        return new(state, authorization, evidence, receipt, workspace);
    }

    private static async Task<PlanningCatalog> Discover(WorkflowEngine engine, bool requireExternalConfirmation = false)
    {
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = requireExternalConfirmation } }, Ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(Ct))
        {
            string? cursor = null;
            do
            {
                var page = await runtime.Capabilities.ListAsync(source.Id, cursor, Ct);
                Assert.Null(page.UnavailableReason);
                foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, Ct));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        return catalog;
    }

    private static async Task<RunResult> Execute(TaskPlan plan, PlanningCatalog catalog, WorkflowEngine engine, JsonObject inputs)
    {
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], inputs, Ct);
    }

    private static string CmdExecutable()
    {
        var name = "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "");
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "GnOuGo.Cmd.Mcp", name);
        var executable = File.Exists(bundled) ? bundled : Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(executable), "The existing shipped Cmd MCP test prerequisite is absent.");
        return executable;
    }
}
