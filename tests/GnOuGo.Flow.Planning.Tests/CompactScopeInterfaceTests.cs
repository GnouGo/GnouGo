using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactScopeInterfaceTests
{
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
                Assert.Contains("extension.outputs adds only new exports", request.Prompt, StringComparison.Ordinal);
            }
            if (stage == 3 && purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose)
            {
                closureReviews++;
                var context = Context(request);
                Assert.Empty(context["draft"]!["outputs"]!.AsArray());
                Assert.Equal(context["baseHash"]!.ToString(), context["candidateHash"]!.ToString());
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
                Assert.Equal(kind == "transform", request.Prompt.Contains("A transform's required object resultType declares named output ports", StringComparison.Ordinal));
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
        Assert.Equal(kind == "foreach", request.Prompt.Contains("foreach.maxItems rejects excess input before iteration", StringComparison.Ordinal));
        if (kind == "foreach")
        {
            Assert.Contains("Report conflicts with explicit required sizes", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("retain finite supported capacity as uncertainty", request.Prompt, StringComparison.Ordinal);
            Assert.Equal(2, Context(request)["draft"]!["tasks"]![0]!["maxItems"]!.GetValue<int>());
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

    private static PlanningSession Fresh(string prompt = "Return exactly Hello as message through a reusable group.") => new() { Request = new() { TenantId = "compact-scope", Prompt = prompt, Options = new JsonObject { ["automatic_correction"] = true } } };
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
