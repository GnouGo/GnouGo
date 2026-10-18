using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class IncrementalConstructionTests(ITestOutputHelper output)
{
    private static long Estimate(LLMRequest request) => (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 2L) / 3L + 256;
    private void ReportPeak(string stage, IEnumerable<LLMRequest> calls)
    {
        var peak = calls.Max(Estimate);
        output.WriteLine("Complete serialized request peak: {0}={1} conservative tokens (UTF-8 bytes / 3, rounded up, +256).", stage, peak);
        Assert.InRange(peak, 1, 32000);
    }
    private static PlanningSession Fresh(int calls = 12) => new()
    {
        Request = new() { TenantId = "construction-test", Prompt = "Return the exact greeting Hello.", MaxModelCalls = calls,
            Options = new JsonObject { ["automatic_correction"] = true } }
    };
    private static PlanningRequirements Requirements() => new()
    {
        Summary = "Return the exact greeting Hello.", Inputs = [], Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }],
        Outcomes = [new("greeting", "Return the exact greeting Hello.")]
    };
    private static PlanTask Value(string id, string text) => new()
    {
        Id = id, Kind = "value", Objective = "Retain the requested greeting", Outputs = [new("message", new() { Kind = "string", Text = text })]
    };
    private static PlanningConstructionExtension Complete(string id = "greet") => new()
    {
        Tasks = [Value(id, "Hello")], Outputs = [new("message", new() { Kind = "output", Source = id, Port = "message" })],
        CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"]
    };
    private static LLMResponse Extension(LLMRequest request, PlanningConstructionExtension extension, bool requirements = true, PlanningRequirements? declaredRequirements = null)
    {
        var response = new JsonObject
        {
            ["baseHash"] = BaseHash(request),
            ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
            ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null
        };
        if (requirements) response["requirements"] = JsonSerializer.SerializeToNode(declaredRequirements ?? Requirements(), PlanningJsonContext.Default.PlanningRequirements);
        return new() { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    }
    private static string BaseHash(LLMRequest request)
    {
        var schema = request.StructuredOutputSchema!;
        var value = schema["properties"]!["baseHash"]!;
        while (value["$ref"]?.ToString() is { } reference) value = schema["$defs"]![reference[8..]]!;
        return value["enum"]![0]!.ToString();
    }
    private static LLMResponse Reviewed() => new() { Json = new JsonObject { ["findings"] = new JsonArray(),
        ["uncertainty"] = "Execution and prose completeness remain unverified.", ["evidenceRequests"] = null } };

    [Fact]
    public async Task SimpleWorkflowUsesOneDraftAndLocalJudgeBeforeApproval()
    {
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.DraftPurpose ? Extension(request, Complete()) : Reviewed() };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.True(state.Construction.Complete);
        Assert.Null(state.ApprovedHash);
        Assert.NotNull(state.Yaml);
        Assert.Equal(8192, runtime.Calls[0].MaxTokens);
        Assert.Equal(2048, runtime.Calls[1].MaxTokens);
        Assert.DoesNotContain("complete TaskPlan", runtime.Calls[1].Prompt, StringComparison.Ordinal);
        Assert.Equal("unverified", state.Construction.Checkpoints[0].Reviews[0]["outcome"]!.ToString());
    }

    [Fact]
    public async Task AcceptedFragmentSurvivesAnotherDraftAndItsContextOmitsWholePlan()
    {
        var drafts = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose != PlanningConstruction.DraftPurpose ? Reviewed() :
            ++drafts == 1 ? Extension(request, new() { Tasks = [Value("first", "Hello")], RequirementIds = ["greeting"] }) :
            Extension(request, new() { Outputs = [new("message", new() { Kind = "output", Source = "first", Port = "message" })], CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"] }, requirements: false) };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.Equal("Hello", Assert.Single(state.Plan!.Root.Tasks).Outputs[0].Value.Text);
        Assert.All(runtime.Calls, request => Assert.DoesNotContain("\"taskPlan\":", request.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidCandidateNeverReplacesAcceptedPlanAndRepairIsLocal()
    {
        var draft = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose.EndsWith("judge", StringComparison.Ordinal)) return Reviewed();
            if (purpose == "replan")
            {
                if (PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()))
                    return new() { Json = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
                        ["patch"] = new JsonObject { ["edits"] = new JsonArray() } } };
                return Extension(request, new() { Outputs = [new("message", new() { Kind = "output", Source = "first", Port = "message" })], CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"] }, false);
            }
            if (++draft == 1) return Extension(request, new() { Tasks = [Value("first", "Hello")], RequirementIds = ["greeting"] });
            return Extension(request, new() { Tasks = [new() { Id = "invalid", Objective = "Read an unavailable producer", Kind = "value",
                Outputs = [new("bad", new() { Kind = "output", Source = "missing", Port = "value" })] }] });
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.NotNull(state.Plan);
        Assert.Single(state.Plan.Root.Tasks);
        Assert.Equal("first", state.Plan.Root.Tasks[0].Id);
        Assert.DoesNotContain(state.Plan.Root.Tasks, t => t.Id == "invalid");
    }

    [Fact]
    public async Task InvalidJudgeEvidenceStopsWithoutCheckpointOrRetry()
    {
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.DraftPurpose ? Extension(request, Complete()) :
            new() { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject { ["kind"] = "omission", ["location"] = "/not-present",
                ["evidence"] = new JsonArray("/request"), ["explanation"] = "Unsupported observation", ["uncertainty"] = "" }), ["uncertainty"] = "", ["evidenceRequests"] = null } } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Null(state.Plan);
        Assert.NotNull(state.Construction.Candidate);
        Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_REVIEW_INCONCLUSIVE");
    }

    [Fact]
    public async Task InsufficientReviewAllowanceStopsBeforeDraftDispatch()
    {
        var runtime = new TestRuntime();
        var state = await PlannerFixture.RunAsync(runtime, Fresh(1));
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(runtime.Calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_REVIEW_ALLOWANCE");
    }

    [Fact]
    public async Task PendingLocalReviewResumesItsExactRequestAndContext()
    {
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.DraftPurpose ? Extension(request, Complete()) : throw new OperationCanceledException() };
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(Fresh(), new(), runtime, PlannerFixture.Ct);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.NotNull(state.PendingCall);
        var request = state.PendingCall.Request;
        state.Status = PlanningStatus.Generating;
        runtime.Respond = (_, _) => Reviewed();
        var resumed = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, resumed.Status);
        Assert.Equal(request.ClientRequestId, runtime.Calls[^1].ClientRequestId);
        Assert.Equal(request.Prompt, runtime.Calls[^1].Prompt);
        Assert.Equal(2, resumed.ModelCalls);
    }

    [Fact]
    public async Task FreshBaselineUsesConstructionAndPreservesItsExistingTasks()
    {
        var fresh = Fresh(); fresh.Request.Baseline = new() { Root = new() { Tasks = [Value("retained", "Hello")] } };
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.DraftPurpose
            ? Extension(request, new() { Outputs = [new("message", new() { Kind = "output", Source = "retained", Port = "message" })],
                CloseScopes = ["/root"], RequirementIds = ["greeting"], Complete = true }) : Reviewed() };
        var state = await PlannerFixture.RunAsync(runtime, fresh);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.NotNull(state.Construction);
        Assert.Equal("retained", Assert.Single(state.Plan!.Root.Tasks).Id);
        Assert.Equal(2, state.ModelCalls);
    }

    [Fact]
    public async Task InvalidInitialActionsConsumeCorrectionAllowanceAndStopRepeatedCandidates()
    {
        var invalid = Complete(); invalid.Tasks[0].Id = "invalid/identity";
        var runtime = new TestRuntime { Respond = (request, _) => Extension(request, invalid) };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.Plan);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
    }

    [Fact]
    public async Task OpenNestedScopeClosesAcrossDraftsWithIntermediateReview()
    {
        var next = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose.EndsWith("judge", StringComparison.Ordinal)) return Reviewed();
            return ++next switch
            {
                1 => Extension(request, new() { Tasks = [new() { Id = "group", Kind = "sequence", Objective = "Preserve a computation boundary", Body = new() }], RequirementIds = ["greeting"] }),
                2 => Extension(request, new() { Scope = "/tasks/group/body", Tasks = [Value("greet", "Hello")],
                    Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })],
                    CloseScopes = ["/tasks/group/body"], RequirementIds = ["greeting"] }, false),
                _ => Extension(request, new() { Outputs = [new("message", new() { Kind = "output", Source = "group", Port = "message" })],
                    CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"] }, false)
            };
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(state.Status == PlanningStatus.FinalReview, state.Status + " calls=" + runtime.Calls.Count + " " + string.Join("; ", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(3, state.Construction!.Checkpoints.Count);
        Assert.Contains(state.Construction.Checkpoints.SelectMany(c => c.Reviews), r => r["purpose"]!.ToString() == PlanningConstruction.IntermediatePurpose);
        Assert.Equal("greet", Assert.Single(state.Plan!.Root.Tasks[0].Body!.Tasks).Id);
    }

    [Fact]
    public async Task RollbackPreservesIndependentWorkAndRejectsChangedGuards()
    {
        var runtime = new TestRuntime();
        var state = Fresh(); state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Construction = new();
        state.Plan = new() { Root = new() { Tasks = [Value("original", "Hello"), Value("independent", "Unchanged"),
            new() { Id = "consumer", Kind = "value", Objective = "Consume the original", Requires = new() { Kind = "boolean", Boolean = true },
                Outputs = [new("message", new() { Kind = "output", Source = "original", Port = "message" })] }] } };
        var independent = JsonSerializer.Serialize(state.Plan.Root.Tasks[1], PlanningJsonContext.Default.PlanTask);
        PlanningConstruction.Rollback(state, "original", "The imported interface needs reconstruction.");
        Assert.Equal("independent", Assert.Single(state.Plan.Root.Tasks).Id);
        Assert.Equal(independent, JsonSerializer.Serialize(state.Plan.Root.Tasks[0], PlanningJsonContext.Default.PlanTask));
        var reconstructed = PlanningConstruction.Clone(state.Plan);
        reconstructed.Root.Tasks.AddRange(PlanningConstruction.Clone(new TaskPlan { Root = new() { Tasks = state.Construction.RebuildTasks } }).Root.Tasks);
        reconstructed.Root.Tasks.Single(t => t.Id == "consumer").Requires = null;
        Assert.Contains(PlanningConstruction.RebuildFindings(state, reconstructed, complete: true), d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task FiveHundredTasksKeepEverySerializedRequestBounded()
    {
        var factory = new InMemoryMcpClientFactory();
        var contract = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""");
        factory.RegisterServer("construction_messages", new()
        {
            Tools = [new() { Name = "retain", Description = "Retain a requested message", EffectKind = "read", InputSchema = contract,
                OutputSchema = contract!.DeepClone() }],
            ToolHandlers = new() { ["retain"] = input => new() { Content = input!.DeepClone() } }
        });
        var runtime = new TestRuntime(new WorkflowEngine { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        catalog.Policy.MaxStepsTotal = 5000; catalog.Policy.MaxStepsPerWorkflow = 5000; catalog.Policy.MaxWorkflows = 1024;
        var capability = catalog.Capabilities.Single(c => c.Method == "retain"); var operation = TaskOperations.Describe(capability);
        var state = Fresh(384); state.Request.Options["construction_preset"] = "large"; state.Request.MaxReplanAttempts = 64;
        state.Request.Policy = catalog.Policy; state.Catalog = catalog;
        state.Discovery.Sources = [new("messages", "Isolated message source")];
        state.Discovery.Resolved = [capability];
        state.Discovery.Pages = [new("messages", null, [new(capability.Id, "messages", "retain", capability.Description,
            capability.StepType, capability.EffectKind, capability.Version, Operation: operation)], null)];
        var emitted = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose.EndsWith("judge", StringComparison.Ordinal)) return Reviewed();
            var extension = new PlanningConstructionExtension { RequirementIds = ["greeting"] };
            for (var n = 0; n < 8 && emitted < 500; n++, emitted++)
                extension.Tasks.Add(new() { Id = "retain_" + emitted, Objective = "Preserve the exact requested message", Operation = operation.Id,
                    Inputs = [new("message", emitted == 0 ? new() { Kind = "string", Text = "Hello" } :
                        new() { Kind = "output", Source = "retain_" + (emitted - 1), Port = "message" })] });
            if (emitted == 500)
            {
                extension.Outputs = [new("message", new() { Kind = "output", Source = "retain_499", Port = "message" })];
                extension.CloseScopes = ["/root"]; extension.Complete = true;
            }
            return Extension(request, extension, requirements: emitted <= 8);
        };
        var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 200 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(500, TaskPlanRevisions.Tasks(state.Plan!).Count());
        Assert.Equal(63, state.Construction!.Checkpoints.Count);
        Assert.All(runtime.Calls, request => Assert.InRange(Estimate(request), 1, 32000));
        var drafts = runtime.Calls.Where(r => r.StructuredOutputSchema?["properties"]?["extension"] is not null).ToArray();
        ReportPeak("500-task generation", drafts);
        ReportPeak("500-task Judges", runtime.Calls.Where(r => r.StructuredOutputSchema?["properties"]?["findings"] is not null));
        Assert.True(Estimate(drafts[^1]) < Estimate(drafts[1]) + 1000);
        Assert.All(runtime.Calls, request => Assert.DoesNotContain("\"taskPlan\":", request.Prompt, StringComparison.Ordinal));
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData(PlanningMode.Auto)]
    [InlineData(PlanningMode.Interactive)]
    public async Task HostChoiceIsReviewedBeforeItsCheckpointIdentityIsAccepted(string mode)
    {
        var extension = Complete();
        extension.Choices = [new() { Id = "salutation", Question = "Choose a greeting", Type = new() { Kind = "string" }, Recommended = "hello",
            Alternatives = [new("hello", "Hello", new() { Kind = "string", Text = "Hello" }), new("hi", "Hi", new() { Kind = "string", Text = "Hi" })] }];
        extension.Tasks[0].Outputs = [new("message", new() { Kind = "choice", Source = "salutation" })];
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.DraftPurpose ? Extension(request, extension) : Reviewed() };
        var state = Fresh(); state.Request.Mode = mode;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        if (mode == PlanningMode.Interactive)
        {
            Assert.Equal(PlanningStatus.Clarification, state.Status);
            Assert.Null(state.Plan);
            Assert.Single(state.GetChoices());
            state = await planner.AdvanceAsync(state, new() { Kind = "choose", ExpectedRevision = state.Revision, Selections = new JsonObject { ["salutation"] = "hello" } }, runtime, PlannerFixture.Ct);
        }
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal("hello", state.Plan!.Choices[0].Selected);
        Assert.Equal(PlanningConstruction.Hash(state.Plan), state.Construction!.Checkpoints[^1].PlanHash);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task TypedRepairReviewsAndCheckpointsTheCorrectedFragment()
    {
        var extension = Complete();
        extension.Tasks[0].Outputs = [new("message", new() { Kind = "output", Source = "seed", Port = "wrong" })];
        extension.Tasks.Insert(0, Value("seed", "Hello"));
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose.EndsWith("judge", StringComparison.Ordinal)) return Reviewed();
            if (purpose == PlanningConstruction.DraftPurpose) return Extension(request, extension);
            var context = PlanningRepairPatch.RequestContext(request);
            var slot = context["repair"]!["slots"]!.AsArray().Single(s => s!["location"]!.ToString() == "/tasks/greet/outputs/message");
            var payload = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["patch"] = new JsonObject
                { ["edits"] = new JsonArray(new JsonObject { ["slot"] = slot!["id"]!.ToString(), ["action"] = "replace",
                    ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "seed", ["port"] = "message" } }) } };
            return new() { Json = PlanningCorpus.Transport(payload, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal("message", state.Plan!.Root.Tasks.Single(t => t.Id == "greet").Outputs[0].Value.Port);
        var review = runtime.Calls.Single(c => c.StructuredOutputSchema?["properties"]?["findings"] is not null);
        Assert.DoesNotContain("\"port\":\"wrong\"", review.Prompt, StringComparison.Ordinal);
        Assert.Contains("Hello", review.Prompt, StringComparison.Ordinal);
        ReportPeak("typed repair", runtime.Calls.Where(r => PlanningRepairPatch.Issued(r.StructuredOutputSchema?.AsObject())));
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task MissingCommittedProducerRegeneratesOnlyTheCandidateAndRequiresReview()
    {
        var (runtime, state, operation) = await MissingCommittedProducerCandidateAsync();
        var acceptedHash = PlanningConstruction.Hash(state.Plan);
        var acceptedTask = JsonSerializer.Serialize(Assert.Single(state.Plan!.Root.Tasks), PlanningJsonContext.Default.PlanTask);
        var acceptedCheckpoint = JsonSerializer.Serialize(state.Construction!.Checkpoints[0], PlanningJsonContext.Default.PlanningConstructionCheckpoint);
        var purposes = new List<string>();
        runtime.Respond = (request, purpose) =>
        {
            purposes.Add(purpose);
            if (purpose.EndsWith("judge", StringComparison.Ordinal))
            {
                Assert.Contains("produce", request.Prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("unperformed", request.Prompt, StringComparison.Ordinal);
                return Reviewed();
            }
            Assert.Equal("replan", purpose);
            Assert.False(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            Assert.NotNull(request.StructuredOutputSchema["properties"]!["extension"]);
            Assert.Equal(acceptedHash, BaseHash(request));
            return Extension(request, new()
            {
                Tasks = [new() { Id = "produce", Objective = "Produce the committed greeting", Operation = operation }],
                Outputs = [new("message", new() { Kind = "output", Source = "produce", Port = "message" }),
                    new("independent", new() { Kind = "output", Source = "independent", Port = "message" })],
                CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"]
            }, requirements: false);
        };
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Construction!.Checkpoints);
        Assert.False(state.Construction.Candidate!.LocalReviewed);
        Assert.Empty(state.Construction.Candidate.Diagnostics);
        Assert.Equal("produce", Assert.Single(state.Construction.Candidate.Extension.Tasks).Id);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);

        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(new[] { "replan", PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose }, purposes);
        Assert.Equal(6, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(new[] { "independent", "produce" }, state.Plan!.Root.Tasks.Select(t => t.Id));
        Assert.Equal(acceptedTask, JsonSerializer.Serialize(state.Plan.Root.Tasks[0], PlanningJsonContext.Default.PlanTask));
        Assert.Equal(acceptedCheckpoint, JsonSerializer.Serialize(state.Construction!.Checkpoints[0], PlanningJsonContext.Default.PlanningConstructionCheckpoint));
        Assert.Equal(2, state.Construction.Checkpoints.Count);
        Assert.All(state.Construction.Checkpoints[1].Reviews, review => Assert.Equal("unverified", review["outcome"]!.ToString()));
        Assert.Contains(state.ValidationResults, r => r.Id == "coverage:greeting:0:result" && r.Outcome == "verified");
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task MissingCommittedProducerDoesNotBroadenAnExplicitOutputRepair()
    {
        var (runtime, state, _) = await MissingCommittedProducerCandidateAsync();
        var acceptedHash = PlanningConstruction.Hash(state.Plan);
        var candidateHash = state.Construction!.Candidate!.Hash;
        state.Construction!.Candidate!.AuthorizedPaths = ["/root/outputs/message"];
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            Assert.Null(request.StructuredOutputSchema["properties"]!["extension"]);
            var slot = Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!;
            Assert.Equal("/root/outputs/message", slot["location"]!.ToString());
            var response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["slot"] = slot["id"]!.DeepClone(),
                    ["action"] = "replace", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "Still unperformed" } }) } };
            return new() { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()) };
        };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        var issued = runtime.Calls[^1];
        Assert.True(PlanningRepairPatch.Issued(issued.StructuredOutputSchema!.AsObject()));
        Assert.Null(issued.StructuredOutputSchema["properties"]!["extension"]);
        Assert.Equal("/root/outputs/message", Assert.Single(PlanningRepairPatch.RequestContext(issued)["repair"]!["slots"]!.AsArray())!["location"]!.ToString());
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan)); Assert.Single(state.Construction!.Checkpoints);
        Assert.DoesNotContain(state.Construction.Candidate!.Plan.Root.Tasks, t => t.Id == "produce");
        // Explicit output authority cannot introduce the tentative exports into the
        // accepted baseline, whose root has no exports yet. Reject atomically.
        Assert.Equal(candidateHash, state.Construction.Candidate.Hash);
        Assert.Equal("unperformed", state.Construction.Candidate.Plan.Root.Outputs.Single(o => o.Name == "message").Value.Text);
        var rejected = Assert.Single(state.Diagnostics);
        Assert.Equal("REVISION_SCOPE_CHANGED", rejected.Code); Assert.Equal("/root/outputs", rejected.Location);
        Assert.Contains(state.Construction.Candidate.Diagnostics, d => d.Code == PlanningCommitments.MissingOperation);
        Assert.False(state.Construction.Complete); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    private static async Task<(TestRuntime Runtime, PlanningSession State, string Operation)> MissingCommittedProducerCandidateAsync()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("greetings", new() { Tools = [new() { Name = "produce", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""") }] });
        var runtime = new TestRuntime(new WorkflowEngine { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        var capability = catalog.Capabilities.Single(c => c.Method == "produce");
        var operation = TaskOperations.Describe(capability).Id;
        var state = Fresh(); state.Catalog = catalog; state.Request.Policy = catalog.Policy;
        state.Request.Prompt = "Preserve the exact independent text Untouched and produce the greeting through the inspected operation.";
        var requirements = Requirements();
        requirements.Outputs!.Add(new() { Name = "independent", Type = new() { Kind = "string" } });
        requirements.Outcomes = [new("independent", "Preserve the exact text Untouched"),
            new("greeting", "Produce and return the observed greeting") { Commitments = [new(operation) { Result = new("message", "message") }] }];
        state.Discovery.Resolved = [capability];
        var drafts = 0;
        runtime.Respond = (request, purpose) => purpose.EndsWith("judge", StringComparison.Ordinal) ? Reviewed() :
            ++drafts == 1 ? Extension(request, new() { Tasks = [Value("independent", "Untouched")], RequirementIds = ["independent"] }, declaredRequirements: requirements) :
            Extension(request, new() { Outputs = [new("message", new() { Kind = "string", Text = "unperformed" }),
                new("independent", new() { Kind = "output", Source = "independent", Port = "message" })],
                CloseScopes = ["/root"], Complete = true, RequirementIds = ["greeting"] }, false);
        var planner = new HybridWorkflowPlanner();
        for (var phase = 0; phase < 3; phase++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.ModelCalls == 3, state.Status + ": " + string.Join(';', state.Diagnostics)); Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("independent", Assert.Single(state.Plan!.Root.Tasks).Id);
        Assert.Contains(state.Construction.Candidate!.Diagnostics, d => d.Code == PlanningCommitments.MissingOperation);
        Assert.Contains(state.Construction.Candidate.Diagnostics, d => d.Code == PlanningCommitments.DisconnectedResult);
        return (runtime, state, operation);
    }

    [Fact]
    public async Task JudgeCanRetrieveOriginalClosedScopeWithoutUnboundedContext()
    {
        var reviews = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.DraftPurpose ? Extension(request, Complete()) :
            ++reviews == 1 ? new() { Json = new JsonObject { ["findings"] = null, ["uncertainty"] = "Inspect the original root exports.",
                ["evidenceRequests"] = new JsonArray("scope:/root", "task:greet") } } : Reviewed() };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(3, state.ModelCalls);
        Assert.Contains("scope:/root", runtime.Calls[^1].Prompt, StringComparison.Ordinal);
        Assert.Contains("taskCount", runtime.Calls[^1].Prompt, StringComparison.Ordinal);
        ReportPeak("evidence review", runtime.Calls.Skip(1));
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterCheckpointOrRollbackRetainsAcceptedWorkAndAccounting(bool rollback)
    {
        var drafts = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose.EndsWith("judge", StringComparison.Ordinal)) return Reviewed();
            if (++drafts == 1) return Extension(request, new() { Tasks = [Value("greet", "Hello"), Value("independent", "Retained")], RequirementIds = ["greeting"] });
            var next = Complete();
            if (!rollback) next.Tasks.Clear();
            return Extension(request, next, requirements: false);
        } };
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(Fresh(), new(), runtime, PlannerFixture.Ct);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        // Resume the durable acceptance boundary, as after losing the process before its caller receives the result.
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.Construction?.Checkpoints.Count == 1 && s.Construction.Candidate is null));
        var independent = JsonSerializer.Serialize(state.Plan!.Root.Tasks[1], PlanningJsonContext.Default.PlanTask);
        if (rollback)
        {
            PlanningConstruction.Rollback(state, "greet", "Reconstruct the greeting interface without changing its meaning.");
            state = PlannerFixture.Clone(state);
            Assert.Single(state.Construction!.RebuildTasks);
            Assert.Contains("greet", state.Construction.Checkpoints[0].InvalidatedTaskIds);
        }
        Assert.Equal(2, state.ModelCalls);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(5, state.ModelCalls); Assert.Equal(2, drafts);
        Assert.Equal(new[] { "greet", "independent" }, state.Plan!.Root.Tasks.Select(t => t.Id));
        Assert.Equal(independent, JsonSerializer.Serialize(state.Plan.Root.Tasks.Single(t => t.Id == "independent"), PlanningJsonContext.Default.PlanTask));
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task HiddenScopeCannotBeClosedByGuessingItsIdentity()
    {
        var runtime = new TestRuntime(); var state = Fresh();
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Construction = new(); state.Requirements = Requirements();
        state.Plan = new() { Root = new() { Tasks = Enumerable.Range(0, 10).Select(n => new PlanTask { Id = "group_" + n,
            Kind = "sequence", Objective = "Preserve the original scope", Body = new() }).ToList() } };
        state.Construction.OpenScopes = Enumerable.Range(0, 10).Select(n => "/tasks/group_" + n + "/body").Append("/root").ToList();
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, new()
        { Scope = "/tasks/group_0/body", CloseScopes = ["/tasks/group_9/body"] }));
        Assert.Contains(failure.Diagnostics, d => d.Code == "CONSTRUCTION_SCOPE_INVALID");
        Assert.Empty(state.Plan.Root.Tasks[0].Body!.Tasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedConstructionResponseNeverBecomesAnAcceptedCheckpoint(bool reportedUsage)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var extension = Complete();
            if (!reportedUsage) extension.Tasks[0].Objective = new string('x', 26000);
            var response = Extension(request, extension);
            if (reportedUsage) response.Usage = new JsonObject { ["output_tokens"] = 8193 };
            return response;
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(1, state.ModelCalls); Assert.Null(state.Plan);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
    }

    [Fact]
    public async Task MandatoryReviewMoneyAdmissionRunsBeforeAnyDispatchOrCounter()
    {
        var admitted = false;
        var runtime = new TestRuntime { AdmitConstruction = (request, input, outputs) =>
        {
            admitted = true;
            Assert.Equal(32000, input); Assert.Equal(new[] { 8192, 2048 }, outputs);
            Assert.Equal(8192, request.MaxTokens);
            throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("LLM_BUDGET_EXCEEDED", "The parent monetary envelope cannot cover both mandatory calls.");
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(admitted); Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(runtime.Calls); Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Fact]
    public async Task RepairWithFiveHundredRequirementsAndAnswersKeepsOnlyRelevantPages()
    {
        var extension = Complete(); extension.Complete = false; extension.CloseScopes.Clear(); extension.Outputs.Clear();
        extension.Tasks[0].Outputs = [new("message", new() { Kind = "output", Source = "seed", Port = "wrong" })];
        extension.Tasks.Insert(0, Value("seed", "Hello"));
        var runtime = new TestRuntime { Respond = (request, _) => Extension(request, extension, requirements: false) };
        var initial = Fresh(); initial.IntentVersion = 3; initial.Requirements = Requirements();
        initial.Requirements.Outcomes.AddRange(Enumerable.Range(0, 500).Select(i => new PlanningRequirement("obligation_" + i, "Original obligation " + i)));
        initial.AnswerHistory = Enumerable.Range(0, 500).Select(i => new PlanningAnswerBatch(i,
            [new("question_" + i, "Original question " + i, [], null)], [new("question_" + i, Text: "Original answer " + i)])).ToList();
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(initial, new(), runtime, PlannerFixture.Ct);
        Assert.True(state.Construction?.Candidate is not null, string.Join(';', state.Diagnostics));
        runtime.Respond = (request, _) =>
        {
            var context = PlanningRepairPatch.RequestContext(request);
            Assert.InRange(context["requirements"]!["outcomes"]!.AsArray().Count, 1, 8);
            Assert.Equal(3, context["answers"]!.AsArray().Count);
            Assert.Equal(500, context["answerHistoryCount"]!.GetValue<int>());
            Assert.DoesNotContain("Original answer 100", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Original obligation 100", request.Prompt, StringComparison.Ordinal);
            Assert.NotNull(context["repair"]!["authority"]);
            var slot = context["repair"]!["slots"]!.AsArray().Single(s => s!["location"]!.ToString() == "/tasks/greet/outputs/message");
            return new() { Json = PlanningCorpus.Transport(new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["slot"] = slot!["id"]!.ToString(), ["action"] = "replace",
                    ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "seed", ["port"] = "message" } }) } },
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Construction!.Candidate!.Diagnostics);
        Assert.Equal(2, state.ModelCalls);
        ReportPeak("500-requirement/history repair", runtime.Calls.Skip(1));
        PlanningConstruction.LoadEvidence(state, ["page:answers:100", "requirement:obligation_100"]);
        Assert.Contains("Original answer 100", state.Construction.Evidence!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Original obligation 100", state.Construction.Evidence.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackRejectsRebuiltWorkInDifferentScopeOrCleanupPhase(bool cleanup)
    {
        var runtime = new TestRuntime(); var state = Fresh(); state.Construction = new(); state.Requirements = Requirements();
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        var child = Value("original", "Hello");
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "container", Kind = "sequence", Objective = "Keep its original boundary",
            Body = new() { Tasks = cleanup ? [] : [child], Always = cleanup ? [child] : [] } }, Value("independent", "Unchanged"),
            new() { Id = "completion", Kind = "value", Objective = "Observe original container completion", DependsOn = ["container"], Outputs = [new("done", new() { Kind = "boolean", Boolean = true })] }] } };
        PlanningConstruction.Rollback(state, "original", "Reconstruct the retained producer within the same boundary.");
        Assert.DoesNotContain(state.Plan.Root.Tasks, t => t.Id == "completion");
        Assert.Contains(state.Construction.RebuildTasks, t => t.Id == "completion");
        var wrong = new PlanningConstructionExtension { Scope = cleanup ? "/tasks/container/body" : "/root", Tasks = [child] };
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, wrong));
        Assert.Contains(failure.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Empty(state.Plan.Root.Tasks[0].Body!.Tasks);
    }

    [Fact]
    public async Task InvalidationJournalRetainsOriginalNestedTasksAndEveryReason()
    {
        var runtime = new TestRuntime(); var state = Fresh(); state.Construction = new(); state.Requirements = Requirements();
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "container", Kind = "sequence", Objective = "Keep its original boundary",
            Body = new() { Tasks = [Value("original", "Hello")] } }, Value("independent", "Unchanged")] } };
        PlanningConstruction.Rollback(state, "container", "First retained invalidation reason.");
        var first = Assert.Single(state.Construction.Invalidations);
        Assert.Equal("original", Assert.Single(first.Tasks.Single(t => t.Id == "container").Body!.Tasks).Id);
        Assert.Equal(new[] { "container", "independent" }, first.Scopes.Single(s => s.Path == "/root").Tasks);
        var replacement = PlanningConstruction.Clone(new TaskPlan { Root = new() { Tasks = [first.Tasks.Single(t => t.Id == "container")] } }).Root.Tasks;
        var candidate = PlanningConstruction.Apply(state, new() { Tasks = replacement, CloseScopes = ["/tasks/container/body"] });
        state.Plan = candidate.Plan; state.Construction.OpenScopes = candidate.OpenScopes;
        Assert.Equal(new[] { "container", "independent" }, state.Plan.Root.Tasks.Select(t => t.Id));
        // A different dependent region keeps a distinct durable event rather than replacing the first reason.
        PlanningConstruction.Rollback(state, "original", "Second retained invalidation reason.");
        state = PlannerFixture.Clone(state);
        Assert.Equal(2, state.Construction!.Invalidations.Count);
        Assert.Equal("First retained invalidation reason.", state.Construction.Invalidations[0].Reason);
        Assert.Equal("Second retained invalidation reason.", state.Construction.Invalidations[1].Reason);
        Assert.Equal(first.BeforeHash, state.Construction.Invalidations[1].BeforeHash);
        PlanningConstruction.LoadEvidence(state, ["invalidation:1", "original:1:container"]);
        Assert.Contains("First retained invalidation reason.", state.Construction.Evidence!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("/root", state.Construction.Evidence["original:1:container"]!["scope"]!.ToString());
    }
}
