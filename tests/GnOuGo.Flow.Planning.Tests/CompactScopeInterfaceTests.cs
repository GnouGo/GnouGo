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
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ReusableKindMeaningDoesNotMigrateHistoricalGuidance(int presentation)
    {
        var state = PlannerFixture.CapturedPresentation(new() { Request = new() { Prompt = PlanningBenchmarkCases.Prompt("collections") } }, presentation);
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "iterate", Kind = "foreach", Body = new() }] } };
        var step = new PlanningConstructionStep { Scope = "/tasks/iterate/body", Kind = "close" };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var guidance in new[] { PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.DraftSemantics(state, step), PlanningCompactConstruction.DraftSemantics(state, step, review: true) })
            Assert.Equal(presentation == 10, guidance.Contains(PlanningCompactConstruction.ReusableGroupSemantics, StringComparison.Ordinal));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task FreshReusableItemCallsAreConstructedAndExecuteBothIndependentVariants()
    {
        var stage = 0;
        var sawEmptyLoopReview = false;
        Exception? modelFailure = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
                var context = Context(request);
                if (purpose == PlanningCompactConstruction.IntentPurpose)
                {
                    var source = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.ToString();
                    return StrictWire(request, request.StructuredOutputSchema["properties"]!["clauses"] is not null
                        ? new JsonObject { ["sourceId"] = source, ["clauses"] = new JsonArray(new JsonObject
                            { ["text"] = context["sources"]![0]!["text"]!.ToString(), ["kind"] = "requirement", ["interfaces"] = "both" }) }
                        : new JsonObject { ["sourceId"] = source, ["interfaces"] = new JsonObject
                            {
                                ["inputs"] = JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "values", Type = NumericArray(), Required = true } }, PlanningJsonContext.Default.ListTaskInput),
                                ["outputs"] = JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "result", Type = NumericArray(), Required = true } }, PlanningJsonContext.Default.ListTaskInput),
                                ["dispositions"] = new JsonArray(context["sources"]!.AsArray().Select(item => (JsonNode)new JsonObject
                                {
                                    ["sourceId"] = item!["id"]!.DeepClone(), ["version"] = item["version"]!.DeepClone(), ["kind"] = "interfaces",
                                    ["inputs"] = new JsonArray("values"), ["outputs"] = new JsonArray("result"),
                                    ["reason"] = "The original explicitly declares these numeric caller/result interfaces.",
                                    ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = item["id"]!.DeepClone(), ["version"] = item["version"]!.DeepClone() })
                                }).ToArray())
                            }, ["evidenceRequests"] = null, ["clarifications"] = null });
                }
                if (purpose == PlanningCompactConstruction.SelectPurpose)
                {
                    Assert.Contains(PlanningCompactConstruction.ReusableGroupSemantics, request.Prompt, StringComparison.Ordinal);
                    var selection = Select(request, stage switch { 0 => "group", 1 or 4 => "value", 2 => "foreach", _ => "call" },
                        stage switch { 1 => "/groups/double/body", 3 => "/tasks/iterate/body", _ => "/root" }, complete: stage == 4);
                    var step = selection["step"]!.AsObject();
                    step["objective"] = stage switch { 0 => "Declare the reusable numeric doubling interface.", 1 => "Double the isolated numeric parameter.",
                        2 => "Open parallel ordered iteration over every input item.", 3 => "Invoke the reusable doubling group for this item.", _ => "Return the ordered doubled values." };
                    step["expectedOutputs"] = new JsonArray(); step["outputNames"] = stage == 4 ? new JsonArray("result") : new JsonArray();
                    step["inputs"] = stage switch { 1 => Values(new() { Kind = "input", Source = "number" }),
                        2 => Values(new() { Kind = "input", Source = "values" }), 3 => Values(new() { Kind = "item" }),
                        4 => Values(new() { Kind = "output", Source = "iterate", Port = "doubled" }), _ => new JsonArray() };
                    step["groupId"] = stage == 3 ? "double" : null;
                    step["operationIds"] = new JsonArray(); step["closeScopes"] = null; step["prerequisites"] = null;
                    if (request.StructuredOutputSchema!["properties"]!["resumeStep"] is not null) selection["resumeStep"] = null;
                    return StrictWire(request, selection, exact: true);
                }
                if (purpose == PlanningConstruction.DraftPurpose)
                {
                    Assert.Contains(PlanningCompactConstruction.ReusableGroupSemantics, request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
                    var extension = stage++ switch
                    {
                        0 => new PlanningConstructionExtension { Groups = [new() { Id = "double", Inputs = [new() { Name = "number", Type = new() { Kind = "number" } }] }], RequirementIds = ["req_0"] },
                        1 => new() { Scope = "/groups/double/body", Tasks = [new() { Id = "multiply", Kind = "value", Objective = "Double the isolated numeric parameter.",
                            Outputs = [new("doubled", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "number" }, new() { Kind = "number", Number = 2 }] })] }],
                            Outputs = [new("doubled", new() { Kind = "output", Source = "multiply", Port = "doubled" })], CloseScopes = ["/groups/double/body"], RequirementIds = ["req_0"] },
                        2 => new() { Tasks = [new() { Id = "iterate", Kind = "foreach", Objective = "Open parallel ordered iteration over every input item.",
                            Items = new() { Kind = "input", Source = "values" }, Body = new(), Parallel = true, MaxItems = 100, MaxConcurrency = 4 }], RequirementIds = ["req_0"] },
                        3 => new() { Scope = "/tasks/iterate/body", Tasks = [new() { Id = "invoke", Kind = "call", Group = "double", Objective = "Invoke the reusable doubling group for this item.",
                            Inputs = [new("number", new() { Kind = "item" })] }], Outputs = [new("doubled", new() { Kind = "output", Source = "invoke", Port = "doubled" })],
                            CloseScopes = ["/tasks/iterate/body"], RequirementIds = ["req_0"] },
                        _ => new() { Outputs = [new("result", new() { Kind = "output", Source = "iterate", Port = "doubled" })], CloseScopes = ["/root"], RequirementIds = ["req_0"], Complete = true }
                    };
                    return StrictWire(request, Draft(request, extension));
                }
                Assert.Contains(PlanningCompactConstruction.ReusableGroupSemantics, request.Prompt, StringComparison.Ordinal);
                if (stage == 3)
                {
                    sawEmptyLoopReview = true;
                    Assert.Empty(context["draft"]!["tasks"]![0]!["body"]!["tasks"]!.AsArray());
                    Assert.Equal(1, context["constructionStage"]!["openedScopeCount"]!.GetValue<int>());
                    Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
                    Assert.Empty(context["constructionStage"]!["closeRequirementIds"]!.AsArray());
                }
                return StrictWire(request, new() { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray(),
                    ["acknowledgments"] = new JsonArray(context["reviewPropositions"]!.AsArray().Select(item => (JsonNode)new JsonObject
                        { ["id"] = item!["id"]!.DeepClone(), ["applicability"] = "broad" }).ToArray()) }, ["uncertainty"] = "Execution remains unverified." });
            }
            catch (Exception error) { modelFailure = error; throw; }
        } };
        var state = await PlannerFixture.RunAsync(runtime, new() { Request = new() { TenantId = "fresh-reusable", Prompt = PlanningBenchmarkCases.Prompt("collections"),
            MaxModelCalls = 64, Options = new() { ["automatic_correction"] = true } } });
        Assert.Null(modelFailure);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(item => item.Code + ":" + item.Message)));
        Assert.Equal(10, state.Construction!.Compact!.PresentationVersion); Assert.True(sawEmptyLoopReview);
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
            if (stage == 3 && purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose)
            {
                closureReviews++;
                var context = Context(request);
                Assert.Empty(context["draft"]!["outputs"]!.AsArray());
                if (purpose == PlanningConstruction.LocalPurpose)
                    Assert.Equal(context["baseHash"]!.ToString(), context["candidateHash"]!.ToString());
                else
                {
                    Assert.Null(context["baseHash"]);
                    Assert.Null(context["candidateHash"]);
                }
                var view = context["scopeInterface"]!;
                Assert.Equal("tentative_candidate", view["basis"]!.ToString());
                Assert.False(view["open"]!.GetValue<bool>()); Assert.False(view["consumable"]!.GetValue<bool>());
                Assert.Equal("assemble", view["outputs"]![0]!["value"]!["source"]!.ToString());
                Assert.Equal("message", view["outputs"]![0]!["value"]!["port"]!.ToString());
                Assert.Equal("string", view["outputs"]![0]!["schema"]!["type"]!.ToString());
                Assert.Null(view["tasks"]); Assert.Null(context["scopeGroup"]!["outputs"]);
                Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/scopeInterface/outputs/0/schema/type", compact: true));
                Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/scopeInterface/outputs/0/schema/type: string", compact: true));
                Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/scopeInterface/outputs/0/schema/type", compact: false));
                Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 4000).EstimatedInputTokens, 1, 4000);
            }
        });
        Assert.Equal(2, closureReviews);
        Assert.Equal(4, state.Construction!.Checkpoints.Count);
        Assert.Equal(state.Construction.Checkpoints[1].PlanHash, state.Construction.Checkpoints[2].PlanHash);
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
        state.Construction.Compact!.CurrentStep = new() { Scope = "/groups/reusable/body", Kind = "close", RequirementIds = ["req_0"] };
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var view = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!;
        Assert.Equal(8, view["outputs"]!.AsArray().Count); Assert.Equal(10, view["outputCount"]!.GetValue<int>());
        Assert.Null(view["tasks"]);
        var address = view["next"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.Equal("field_8", state.Construction.Evidence![address]!["outputs"]![0]!["name"]!.ToString());
        Assert.Equal(2, state.Construction.Evidence[address]!["outputs"]!.AsArray().Count);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Null(PlanningCompactConstruction.GroupInterface(state, "reusable")!["outputs"]);
        group.Body.Outputs[8] = new("changed", new() { Kind = "string", Text = "Changed" });
        var stale = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Contains(stale.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        var renewed = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!["next"]!.ToString();
        state.Construction.OpenScopes.Remove("/groups/reusable/body");
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
            if (purpose == PlanningCompactConstruction.SelectPurpose) return Wire(request, Select(request, kind, "/root", complete: true));
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                var task = kind == "value" ? Greeting() : new PlanTask { Id = "assemble", Kind = "transform", Mode = "extract", Objective = "Return the exact supplied greeting.",
                    Inputs = [new("message", new() { Kind = "string", Text = "Hello" })],
                    ResultType = new() { Kind = "object", Fields = [new() { Name = "message", Type = new() { Kind = "string" } }] } };
                return Wire(request, Draft(request, new() { Tasks = [task], Outputs = [new("message", new() { Kind = "output", Source = "assemble", Port = "message" })],
                    RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }));
            }
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                reviewed = true;
                Assert.Equal(kind == "transform", request.Prompt.Contains("named output ports", StringComparison.Ordinal));
                Assert.Equal("string", Context(request)["scopeInterface"]!["outputs"]![0]!["schema"]!["type"]!.ToString());
                Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 4000).EstimatedInputTokens, 1, 4000);
            }
            return Wire(request, Reviewed());
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh("Return exactly Hello as message."));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(2, state.Construction!.Compact!.PresentationVersion);
        Assert.True(Assert.Single(state.Construction.Compact.InterfaceExtractions!).Complete);
        Assert.True(reviewed);
    }

    [Theory]
    [InlineData("foreach")]
    [InlineData("value")]
    public async Task ScopedLoopReviewKeepsSafetyLimitsAndRecoversItsExactIssuedRequest(string kind)
    {
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose) return Wire(request, Select(request, kind, "/root", complete: false));
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                var task = kind == "value" ? Greeting() : new PlanTask
                {
                    Id = "iterate", Kind = "foreach", Objective = "Process the complete literal array.",
                    Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = 2 }] },
                    Body = new(), MaxItems = 2, MaxConcurrency = 1
                };
                return Wire(request, Draft(request, new() { Tasks = [task], RequirementIds = ["req_0"] }));
            }
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            throw new OperationCanceledException();
        } };
        _ = await PlannerFixture.RunAsync(runtime, Fresh("Process the complete literal array [1,2], then return message."));
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        var request = saved.PendingCall!.Request;
        Assert.Equal(kind == "foreach", request.Prompt.Contains("foreach.maxItems", StringComparison.Ordinal));
        if (kind == "foreach")
        {
            Assert.Equal(2, Context(request)["draft"]!["tasks"]![0]!["maxItems"]!.GetValue<int>());
            Assert.Equal(1, Context(request)["draft"]!["tasks"]![0]!["maxConcurrency"]!.GetValue<int>());
        }
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose, 4000).EstimatedInputTokens, 1, 4000);
        var issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        saved.Status = PlanningStatus.Generating;
        var recovery = new TestRuntime { Respond = (issuedRequest, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return Wire(issuedRequest, Reviewed());
        } };
        var resumed = await new HybridWorkflowPlanner().AdvanceAsync(saved, new() { ExpectedRevision = saved.Revision }, recovery, PlannerFixture.Ct);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Single(resumed.Construction!.Checkpoints);
        if (kind == "foreach")
        {
            Assert.Equal(2, resumed.Plan!.Root.Tasks[0].MaxItems);
            Assert.Equal(1, resumed.Plan.Root.Tasks[0].MaxConcurrency);
        }
    }

    private static async Task<PlanningSession> ReusableGroup(Action<LLMRequest, string, int>? observe = null)
    {
        var stage = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            observe?.Invoke(request, purpose, stage);
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose)
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
                    0 => new PlanningConstructionExtension { Groups = [new() { Id = "reusable", Inputs = [new() { Name = "word", Type = new() { Kind = "string" } }] }], RequirementIds = ["req_0"] },
                    1 => new() { Scope = "/groups/reusable/body", Tasks = [Greeting(input: true)], Outputs = [new("message", new() { Kind = "output", Source = "assemble", Port = "message" })], RequirementIds = ["req_0"] },
                    2 => new() { Scope = "/groups/reusable/body", CloseScopes = ["/groups/reusable/body"], RequirementIds = ["req_0"] },
                    _ => new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "reusable", Objective = "Return the greeting from the reusable group.",
                        Inputs = [new("word", new() { Kind = "string", Text = "Hello" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "invoke", Port = "message" })], RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }
                };
                return Wire(request, Draft(request, extension));
            }
            return Wire(request, Reviewed());
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(2, state.Construction!.Compact!.PresentationVersion);
        Assert.True(Assert.Single(state.Construction.Compact.InterfaceExtractions!).Complete);
        return state;
    }

    private static PlanningSession Fresh(string prompt = "Return exactly Hello as message through a reusable group.") =>
        PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "compact-scope", Prompt = prompt,
            Options = new JsonObject { ["automatic_correction"] = true } } });
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new() { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null };
    private static JsonObject Index(LLMRequest request)
    {
        var sourceId = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.ToString();
        return request.StructuredOutputSchema["properties"]!["clauses"] is not null
            ? new()
            {
                ["sourceId"] = sourceId,
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = Context(request)["sources"]![0]!["text"]!.ToString(),
                    ["kind"] = "requirement", ["interfaces"] = "output" })
            }
            : new()
            {
                ["sourceId"] = sourceId, ["inputs"] = new JsonArray(),
                ["outputs"] = new JsonArray(new JsonObject { ["name"] = "message",
                    ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false }, ["required"] = true })
            };
    }
    private static JsonObject Select(LLMRequest request, string kind, string scope, bool complete) => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.ToString(), ["step"] = new JsonObject { ["scope"] = scope, ["kind"] = kind, ["objective"] = "Preserve the exact greeting through the reusable interface.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The exact greeting."),
            ["closeRequirementIds"] = complete ? new JsonArray("req_0") : new JsonArray(), ["outputNames"] = complete ? new JsonArray("message") : new JsonArray() },
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
