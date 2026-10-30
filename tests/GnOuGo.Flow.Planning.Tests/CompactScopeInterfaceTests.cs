using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactScopeInterfaceTests
{

    [Fact]
    public async Task FreshReusableItemCallsAreConstructedAndExecuteBothIndependentVariants()
    {
        var stage = 0;
                Exception? modelFailure = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
                var context = Context(request);
                if (purpose == PlanningCompactConstruction.IntentPurpose)
                {
                    return StrictWire(request, CurrentPlanningFixture.Intent(context,
                        JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "values", Type = NumericArray(), Required = true } }, PlanningJsonContext.Default.ListTaskInput)!.AsArray(),
                        JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "result", Type = NumericArray(), Required = true } }, PlanningJsonContext.Default.ListTaskInput)!.AsArray()));
                }
                if (purpose == PlanningCompactConstruction.SelectPurpose)
                    return StrictWire(request, Block(request), exact: true);
                if (purpose == PlanningCompactConstruction.ImplementationPurpose)
                {
                    Assert.Contains(PlanningCompactConstruction.ReusableGroupSemantics, request.Prompt, StringComparison.Ordinal);
                    var selection = Select(request, stage switch { 0 => "group", 1 or 4 => "value", 2 => "foreach", _ => "call" },
                        stage switch { 1 => "/groups/double/body", 3 => "/tasks/iterate/body", _ => "/root" }, complete: stage == 4);
                    var step = selection["step"]!.AsObject();
                    step["outputNames"] = stage == 4 ? new JsonArray("result") : new JsonArray();
                    step["inputs"] = stage switch { 1 => Values(new() { Kind = "input", Source = "number" }),
                        2 => Values(new() { Kind = "input", Source = "values" }), 3 => Values(new() { Kind = "item" }),
                        4 => Values(new() { Kind = "output", Source = "iterate", Port = "doubled" }), _ => new JsonArray() };
                    step["groupId"] = stage is 2 or 3 ? "double" : null;
                    step["operationIds"] = new JsonArray(); step["closeScopes"] = null; step["prerequisites"] = null;
                    if (request.StructuredOutputSchema!["properties"]!["resumeStep"] is not null) selection["resumeStep"] = null;
                    return StrictWire(request, selection, exact: true);
                }
                if (purpose == PlanningConstruction.DraftPurpose)
                {
                    Assert.Contains(PlanningCompactConstruction.ReusableGroupSemantics, request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
                    var extension = stage++ switch
                    {
                        0 => new PlanningConstructionExtension { CompleteBlock = false, Groups = [new() { Id = "double", Inputs = [new() { Name = "number", Type = new() { Kind = "number" } }],
                            Body = new() { Tasks = [new() { Id = "multiply", Kind = "value", Objective = "Double the isolated numeric parameter.",
                                Outputs = [new("doubled", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "number" }, new() { Kind = "number", Number = 2 }] })] }] } }], RequirementIds = ["req_0"], ContinueInScope = "/groups/double/body" },
                        1 => new() { CompleteBlock = false, Scope = "/groups/double/body",
                            Outputs = [new("doubled", new() { Kind = "output", Source = "multiply", Port = "doubled" })], CloseScopes = ["/groups/double/body"], RequirementIds = ["req_0"] },
                        2 => new() { CompleteBlock = false, Tasks = [new() { Id = "iterate", Kind = "foreach", Objective = "Open parallel ordered iteration over every input item.",
                            Items = new() { Kind = "input", Source = "values" }, Body = new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "double",
                                Objective = "Invoke the reusable doubling group for this item.", Inputs = [new("number", new() { Kind = "item" })] }] },
                            Parallel = true, MaxItems = 100, MaxConcurrency = 4 }], RequirementIds = ["req_0"], ContinueInScope = "/tasks/iterate/body" },
                        3 => new() { CompleteBlock = false, Scope = "/tasks/iterate/body", Outputs = [new("doubled", new() { Kind = "output", Source = "invoke", Port = "doubled" })],
                            CloseScopes = ["/tasks/iterate/body"], RequirementIds = ["req_0"] },
                        _ => new() { CompleteBlock = true, Outputs = [new("result", new() { Kind = "output", Source = "iterate", Port = "doubled" })], CloseScopes = ["/root"], RequirementIds = ["req_0"], Complete = true }
                    };
                    return StrictWire(request, Draft(request, extension));
                }
                Assert.Contains("group", request.Prompt, StringComparison.Ordinal);
                return StrictWire(request, Reviewed());
            }
            catch (Exception error) { modelFailure = error; throw; }
        } };
        var state = await PlannerFixture.RunAsync(runtime, new() { Request = new() { TenantId = "fresh-reusable", Prompt = PlanningBenchmarkCases.Prompt("collections"),
            MaxModelCalls = 64, Options = new() { ["automatic_correction"] = true } } });
        Assert.Null(modelFailure);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(item => item.Code + ":" + item.Message)));
        Assert.Equal(0, state.ReplanAttempts); Assert.Equal(5, stage);
        PlanningArtifactApproval.Verify(state);
        var loop = Assert.Single(state.Plan!.Root.Tasks); Assert.Equal("foreach", loop.Kind); Assert.True(loop.Parallel);
        var call = Assert.Single(loop.Body!.Tasks); Assert.Equal("call", call.Kind); Assert.Equal(Assert.Single(state.Plan.Groups).Id, call.Group);
        Assert.Equal(call.Id, Assert.Single(loop.Body.Outputs).Value.Source); Assert.Equal(loop.Id, Assert.Single(state.Plan.Root.Outputs).Value.Source);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        foreach (var variant in new[] { "nominal", "alternate" })
        {
            var sample = new PlanningBenchmarkCases.Environment("collections", variant);
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs("collections", variant), PlannerFixture.Ct);
            Assert.True(sample.Verify(result), result.Error?.Message); Assert.Empty(sample.Effects); Assert.Empty(sample.Violations);
        }

        static TaskType NumericArray() => new() { Kind = "array", Items = new() { Kind = "number" } };
        static JsonArray Values(TaskValue value)
        {
            var hint = new JsonObject { ["kind"] = value.Kind };
            if (value.Kind is "input" or "output") hint["source"] = value.Source;
            if (value.Kind == "output") hint["port"] = value.Port;
            return new JsonArray(hint);
        }
        static LLMResponse StrictWire(LLMRequest request, JsonObject response, bool exact = false)
        {
            var value = exact ? response : PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(value, request.StructuredOutputSchema!));
            return new() { Json = value, Text = value.ToJsonString() };
        }
    }

    [Fact]
    public async Task AcceptedExportsRemainVisibleWhenASeparateFragmentClosesTheGroup()
    {
        var closureReviews = 0;
        var state = await ReusableGroup((request, purpose, stage) =>
        {
            if (purpose == PlanningConstruction.DraftPurpose && stage == 2)
            {
                var context = Context(request);
                Assert.Equal("accepted_declarations", context["scopeInterface"]!["basis"]!.ToString());
                Assert.True(context["scopeInterface"]!["open"]!.GetValue<bool>());
                Assert.False(context["scopeInterface"]!["consumable"]!.GetValue<bool>());
                Assert.Equal("message", context["scopeInterface"]!["outputs"]![0]!["name"]!.ToString());
                Assert.Equal("string", context["scopeInterface"]!["outputs"]![0]!["schema"]!["type"]!.ToString());
                Assert.Null(context["scopeGroup"]!["outputs"]);
            }
            if (purpose == PlanningConstruction.GlobalPurpose)
            {
                closureReviews++;
                var context = Context(request);
                var groupScope = context["groups"]!.AsArray().Single(group => group!["id"]!.ToString() == "reusable")!["body"]!.ToString();
                var scope = context["scopes"]!.AsArray().Single(scope => scope!["id"]!.ToString() == groupScope)!;
                var producer = context["actions"]!.AsArray().Single(action => action!["scope"]!.ToString() == groupScope && action["kind"]!.ToString() == "value")!;
                Assert.Equal(producer["id"]!.ToString(), scope["outputs"]![0]!["value"]!["source"]!.ToString());
                Assert.False(scope["open"]!.GetValue<bool>());
            }
        });
        Assert.Equal(1, closureReviews);
        Assert.Equal(4, state.Construction!.Checkpoints.Count);
        Assert.Equal(state.Construction!.Checkpoints[1].PlanHash, state.Construction!.Checkpoints[2].PlanHash);
        Assert.Single(state.Plan!.Groups[0].Body.Outputs);
        Assert.Single(state.Plan.Root.Tasks);
        Assert.Equal("call", state.Plan.Root.Tasks[0].Kind);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task ScopeExportPagesBindExactDeclarationsAndFrontierWithoutExposingOtherScopes()
    {
        var state = await ReusableGroup();
        var group = state.Plan!.Groups[0];
        state.Plan.Root.Tasks.Clear(); state.Plan.Root.Outputs.Clear();
        for (var i = 1; i < 10; i++) group.Body.Outputs.Add(new("field_" + i, new() { Kind = "string", Text = "Value " + i }));
        state.Construction!.OpenScopes = ["/root", "/groups/reusable/body"];
        state.Construction!.Compact!.CurrentStep = new() { Scope = "/groups/reusable/body", Kind = "close", RequirementIds = ["req_0"] };
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var view = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!;
        Assert.Equal(8, view["outputs"]!.AsArray().Count); Assert.Equal(10, view["outputCount"]!.GetValue<int>());
        Assert.Null(view["tasks"]);
        var address = view["next"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.Equal("field_8", state.Construction!.Evidence![address]!["outputs"]![0]!["name"]!.ToString());
        Assert.Equal(2, state.Construction!.Evidence[address]!["outputs"]!.AsArray().Count);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Null(PlanningCompactConstruction.GroupInterface(state, "reusable")!["outputs"]);
        group.Body.Outputs[8] = new("changed", new() { Kind = "string", Text = "Changed" });
        var stale = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Contains(stale.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        var renewed = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!["next"]!.ToString();
        state.Construction!.OpenScopes.Remove("/groups/reusable/body");
        var frontier = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [renewed]));
        Assert.Contains(frontier.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("value")]
    public async Task JudgeReceivesNamedPortSemanticsOnlyForTheSelectedTransformKind(string kind)
    {
        var reviewed = false;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose) return Wire(request, Block(request));
            if (purpose == PlanningCompactConstruction.ImplementationPurpose) return Wire(request, Select(request, kind, "/root", complete: true));
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                var task = kind == "value" ? Greeting() : new PlanTask { Id = "assemble", Kind = "transform", Mode = "extract", Objective = "Return the exact supplied greeting.",
                    Inputs = [new("message", new() { Kind = "string", Text = "Hello" })],
                    ResultType = new() { Kind = "object", Fields = [new() { Name = "message", Type = new() { Kind = "string" } }] } };
                return Wire(request, Draft(request, new() { Tasks = [task], Outputs = [new("message", new() { Kind = "output", Source = "assemble", Port = "message" })],
                    RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true, CompleteBlock = true }));
            }
            if (purpose == PlanningConstruction.GlobalPurpose)
            {
                reviewed = true;
                var action = Assert.Single(Context(request)["actions"]!.AsArray())!;
                Assert.Equal(kind == "transform", action["instruction"] is not null);
                Assert.Equal("message", Context(request)["publicOutputs"]![0]!["name"]!.ToString());
                Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 4000).EstimatedInputTokens, 1, 4000);
            }
            return Wire(request, Reviewed());
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh("Return exactly Hello as message."));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction!.Compact!));
        Assert.True(reviewed);
    }

    [Theory]
    [InlineData("foreach")]
    [InlineData("value")]
    public async Task ScopedLoopReviewKeepsSafetyLimitsAndRecoversItsExactIssuedRequest(string kind)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        if (kind == "foreach")
        {
            state.Construction!.Compact!.CurrentStep!.Kind = "foreach";
            state.Construction.Candidate = PlanningConstruction.Apply(state, new()
            {
                RequirementIds = ["work"], CompleteBlock = true, CloseScopes = ["/tasks/iterate/body"], Tasks = [new() { Id = "iterate", Kind = "foreach",
                    Objective = "Preserve the ordered literal items.", Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = 2 }] },
                    MaxItems = 2, MaxConcurrency = 1, Body = new() { Tasks = [new() { Id = "keep", Kind = "value", Objective = "Preserve this literal item.",
                        Outputs = [new("number", new() { Kind = "item" })] }] } }]
            });
        }
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await BusinessReviewProtocolTests.Advance(state, runtime);
        var saved = BusinessReviewProtocolTests.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        var request = saved.PendingCall!.Request;
        if (kind == "foreach")
        {
            var loop = Context(request)["actions"]!.AsArray().Single(action => action!["id"]!.ToString() == runtime.EvidenceId(request, "task:iterate"))!;
            Assert.Equal(2, loop["maxItems"]!.GetValue<int>()); Assert.Equal(1, loop["maxConcurrency"]!.GetValue<int>());
        }
        var issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls; saved.Status = PlanningStatus.Generating;
        var recovery = new TestRuntime { Respond = (issuedRequest, purpose) => { Assert.Equal(PlanningConstruction.LocalPurpose, purpose); return Wire(issuedRequest, Reviewed()); } };
        var resumed = await BusinessReviewProtocolTests.Advance(saved, recovery);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls); Assert.Single(resumed.Construction!.Checkpoints);
        if (kind == "foreach") { Assert.Equal(2, resumed.Plan!.Root.Tasks[0].MaxItems); Assert.Equal(1, resumed.Plan.Root.Tasks[0].MaxConcurrency); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferedAcceptedGroupExportLoadsAcrossSelectionBoundariesWithoutGrantingBindings(bool selectedElsewhere)
    {
        var state = await ReusableGroup();
        var construction = state.Construction!;
        construction.OpenScopes = ["/root"];
        construction.Compact!.CurrentStep = selectedElsewhere ? new()
        {
            Id = "root-work", Scope = "/root", Kind = "value", RequirementIds = ["req_0"],
            Objective = "Continue independent caller work.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true
        } : null;
        const string groupScope = "/groups/reusable/body";
        var context = PlanningCompactConstruction.ImplementationContext(state);
        var exports = context["requirements"]!.AsArray().SelectMany(requirement =>
            (requirement!["acceptedProgress"]?["items"] as JsonArray ?? []).OfType<JsonObject>())
            .SelectMany(checkpoint => (checkpoint["outputs"] as JsonArray ?? []).OfType<JsonObject>()).ToArray();
        var offered = Assert.Single(exports, export => export["scope"]?.ToString() == groupScope && export["name"]?.ToString() == "message");
        var address = offered["evidence"]!.ToString();
        var acceptedPlan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var selected = JsonSerializer.Serialize(construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var calls = state.ModelCalls;

        PlanningConstruction.LoadEvidence(state, [address]);

        var view = construction.Evidence![address]!;
        Assert.Equal(groupScope, view["scope"]!.ToString());
        Assert.Equal("accepted_declarations", view["basis"]!.ToString());
        Assert.False(view["consumable"]!.GetValue<bool>());
        Assert.Equal("message", Assert.Single(view["outputs"]!.AsArray())!["name"]!.ToString());
        Assert.Equal("string", view["outputs"]![0]!["schema"]!["type"]!.ToString());
        Assert.Null(view["tasks"]);
        Assert.Equal(selected, JsonSerializer.Serialize(construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        var invalid = PlanningConstruction.Clone(state.Plan!);
        invalid.Root.Tasks.Add(new()
        {
            Id = "illegal-group-leak", Kind = "value", Objective = "Attempt to bypass the isolated reusable group boundary.",
            Outputs = [new("message", new() { Kind = "output", Source = "assemble", Port = "message" })]
        });
        var validation = TaskPlanCompiler.ValidateConstruction(invalid, state.Catalog!, state.Request,
            construction.OpenScopes.ToHashSet(StringComparer.Ordinal));
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "TASK_REFERENCE_UNKNOWN" &&
            diagnostic.Location.StartsWith("/tasks/illegal-group-leak", StringComparison.Ordinal));
        var isolated = PlanningConstruction.Clone(state.Plan!);
        isolated.Inputs.Add(new() { Name = "caller_only", Type = new() { Kind = "number" } });
        isolated.Groups[0].Body.Tasks.Add(new()
        {
            Id = "illegal-caller-capture", Kind = "value", Objective = "Attempt to capture an undeclared caller input inside the isolated group.",
            Outputs = [new("number", new() { Kind = "input", Source = "caller_only" })]
        });
        var isolatedValidation = TaskPlanCompiler.ValidateConstruction(isolated, state.Catalog!, state.Request,
            construction.OpenScopes.ToHashSet(StringComparer.Ordinal));
        Assert.Contains(isolatedValidation.Diagnostics, diagnostic => diagnostic.Code == "TASK_REFERENCE_UNKNOWN" &&
            diagnostic.Location.StartsWith("/tasks/illegal-caller-capture", StringComparison.Ordinal));
        Assert.Equal(acceptedPlan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("claim")]
    [InlineData("diagnostic")]
    public async Task CandidateGroupExportStillRequiresItsOwnCurrentCleanClosure(string rejected)
    {
        var state = await ReusableGroup();
        const string groupScope = "/groups/reusable/body";
        var construction = state.Construction!;
        construction.OpenScopes = ["/root", groupScope];
        construction.Compact!.CurrentStep = new()
        {
            Id = "group-close", Scope = groupScope, Kind = "close", RequirementIds = ["req_0"], CloseScopes = [groupScope],
            Objective = "Review the tentative group closure.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true
        };
        construction.Candidate = PlanningConstruction.Apply(state, new()
            { Scope = groupScope, RequirementIds = ["req_0"], CloseScopes = [groupScope] });
        var address = PlanningCompactConstruction.ScopeInterface(state, candidate: true, navigationOnly: true)!["evidence"]!.ToString();
        if (rejected == "scope") construction.Compact.CurrentStep.Scope = "/root";
        if (rejected == "claim") construction.Candidate.Extension.CloseScopes.Clear();
        if (rejected == "diagnostic") construction.Candidate.Diagnostics.Add(new("INVALID_CANDIDATE", groupScope, "The candidate is not technically valid."));
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);

        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));

        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<PlanningSession> ReusableGroup(Action<LLMRequest, string, int>? observe = null)
    {
        var stage = 0;
        Exception? failure = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
            observe?.Invoke(request, purpose, stage);
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose)
                return Wire(request, Block(request));
            if (purpose == PlanningCompactConstruction.ImplementationPurpose)
            {
                var selection = Select(request, stage switch { 0 => "group", 1 => "value", 2 => "close", _ => "call" },
                    stage is 1 or 2 ? "/groups/reusable/body" : "/root", complete: stage == 3);
                if (stage == 1) selection["step"]!["inputs"] = new JsonArray(new JsonObject { ["kind"] = "input", ["source"] = "word" });
                if (stage == 3) selection["step"]!["groupId"] = "reusable";
                return Wire(request, selection);
            }
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                var extension = stage++ switch
                {
                    0 => new PlanningConstructionExtension { CompleteBlock = false, Groups = [new() { Id = "reusable", Inputs = [new() { Name = "word", Type = new() { Kind = "string" } }],
                        Body = new() { Tasks = [Greeting(input: true)] } }], RequirementIds = ["req_0"], ContinueInScope = "/groups/reusable/body" },
                    1 => new() { CompleteBlock = false, Scope = "/groups/reusable/body", Outputs = [new("message", new() { Kind = "output", Source = "assemble", Port = "message" })], RequirementIds = ["req_0"], ContinueInScope = "/groups/reusable/body" },
                    2 => new() { CompleteBlock = false, Scope = "/groups/reusable/body", CloseScopes = ["/groups/reusable/body"], RequirementIds = ["req_0"] },
                    _ => new() { CompleteBlock = true, Tasks = [new() { Id = "invoke", Kind = "call", Group = "reusable", Objective = "Return the greeting from the reusable group.",
                        Inputs = [new("word", new() { Kind = "string", Text = "Hello" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "invoke", Port = "message" })], RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }
                };
                return Wire(request, Draft(request, extension));
            }
            return Wire(request, Reviewed());
            }
            catch (Exception error) { failure = error; throw; }
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.Null(failure);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction!.Compact!));
        return state;
    }

    private static PlanningSession Fresh(string prompt = "Return exactly Hello as message through a reusable group.") =>
        PlannerFixture.Capture(new() { Request = new() { TenantId = "compact-scope", Prompt = prompt,
            Options = new JsonObject { ["automatic_correction"] = true } } });
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };

    private static JsonObject Reviewed() => BusinessReviewProtocolTests.Report("ok");
    private static JsonObject Index(LLMRequest request) => CurrentPlanningFixture.Intent(Context(request), outputs:
        new JsonArray(new JsonObject { ["name"] = "message", ["required"] = true, ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false } }));

    private static JsonObject Select(LLMRequest request, string kind, string scope, bool complete) => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.ToString(), ["step"] = new JsonObject { ["scope"] = scope, ["kind"] = kind,
            ["operationIds"] = new JsonArray(), ["inputs"] = new JsonArray(), ["closeScopes"] = kind == "close" ? new JsonArray(scope) : null, ["dependencyTaskIds"] = new JsonArray(),
            ["closeRequirementIds"] = complete ? new JsonArray("req_0") : new JsonArray(), ["outputNames"] = complete ? new JsonArray("message") : new JsonArray() },
        ["clarifications"] = null, ["evidenceRequests"] = null
    };
    private static JsonObject Block(LLMRequest request) => new()
    {
        ["block"] = new JsonObject
        {
            ["objective"] = string.Join(" ", Context(request)["requirements"]!.AsArray().Select(row => row!["original"]!.ToString())),
            ["requirementIds"] = Context(request)["remainingObligations"]!.DeepClone(),
            ["informationNeeds"] = new JsonArray("The declared caller inputs."),
            ["expectedResults"] = new JsonArray("The exact requested public result."), ["dependencies"] = new JsonArray(),
            ["successCriteria"] = new JsonArray("Preserve the exact requested values, reuse and ordering requirements.")
        },
        ["clarifications"] = null, ["evidenceRequests"] = null
    };
    private static PlanTask Greeting(bool input = false) => new() { Id = "assemble", Kind = "value", Objective = "Return the exact supplied greeting.",
        Outputs = [new("message", input ? new() { Kind = "input", Source = "word" } : new() { Kind = "string", Text = "Hello" })] };
    private static JsonObject Draft(LLMRequest request, PlanningConstructionExtension extension) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.ToString(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null
    };
}
