using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactReviewEvidenceLinkTests
{
    [Theory]
    [InlineData("draft")]
    [InlineData("task")]
    [InlineData("scope")]
    public void GroupNavigationUsesOnlySuppliedCallsAndPreservesEvidence(string source)
    {
        var state = new PlanningSession
        {
            Plan = new() { Groups = Enumerable.Range(0, 110).Select(index => new TaskGroup { Id = "group_" + index }).ToList() },
            Construction = new() { Version = 2, Compact = new() { PresentationVersion = 2 } }
        };
        var tasks = new JsonArray(Enumerable.Range(0, 9).Select(index => (JsonNode)new JsonObject
            { ["id"] = "call_" + index, ["kind"] = "call", ["group"] = "group_" + index }).ToArray());
        tasks.Add((JsonNode)new JsonObject { ["id"] = "literal", ["kind"] = "value",
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "payload", ["value"] = new JsonObject
                { ["kind"] = "json", ["json"] = new JsonObject { ["kind"] = "call", ["group"] = "group_109" } } }) });
        var context = new JsonObject();
        if (source == "draft") context["draft"] = new JsonObject { ["tasks"] = tasks };
        else if (source == "scope") context["evidence"] = new JsonObject { ["scope:/root"] = new JsonObject { ["tasks"] = tasks } };
        else context["evidence"] = new JsonObject(tasks.Select((task, index) =>
            new KeyValuePair<string, JsonNode?>("task:call_" + index, task!.DeepClone())));
        var original = context.DeepClone();
        PlanningCompactConstruction.AddReviewGroupEvidence(state, context);
        Assert.Equal(9, context["groupEvidence"]!["count"]!.GetValue<int>());
        Assert.Equal(8, context["groupEvidence"]!["items"]!.AsArray().Count);
        Assert.DoesNotContain("group_109", context["groupEvidence"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.All(context["groupEvidence"]!["items"]!.AsArray(), link =>
            Assert.Equal("scope:/groups/" + link!["id"] + "/body", link["address"]!.ToString()));
        context.Remove("groupEvidence");
        Assert.True(JsonNode.DeepEquals(original, context));
        state.Construction.Compact!.PresentationVersion = null;
        PlanningCompactConstruction.AddReviewGroupEvidence(state, context);
        Assert.True(JsonNode.DeepEquals(original, context));
    }

    [Fact]
    public async Task ReviewFollowsReferencedGroupBodyWithoutChangingExactScopeEvidence()
    {
        var (state, runtime) = await Ready(calledGroup: true);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var retainedGroup = JsonSerializer.Serialize(state.Plan!.Groups[1], PlanningJsonContext.Default.TaskGroup);
        var calls = state.ModelCalls;
        var reviews = 0;
        runtime.Respond = (request, _) =>
        {
            AssertBounded(request);
            var context = Context(request);
            Assert.True(JsonNode.DeepEquals(state.Construction!.Evidence, context["evidence"]));
            switch (reviews++)
            {
                case 0:
                    Assert.Null(context["groupEvidence"]);
                    return Wire(request, Evidence("scope:/tasks/iteration/body"));
                case 1:
                    var scope = context["evidence"]!["scope:/tasks/iteration/body"]!;
                    var task = Assert.Single(scope["tasks"]!.AsArray())!;
                    Assert.Equal("call", task["kind"]!.ToString());
                    Assert.Equal("leaf", task["group"]!.ToString());
                    var link = Assert.Single(context["groupEvidence"]!["items"]!.AsArray())!;
                    Assert.Equal(1, context["groupEvidence"]!["count"]!.GetValue<int>());
                    Assert.Equal("leaf", link["id"]!.ToString());
                    Assert.Equal("scope:/groups/leaf/body", link["address"]!.ToString());
                    Assert.DoesNotContain("unrelated_group", context["groupEvidence"]!.ToJsonString(), StringComparison.Ordinal);
                    Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/groupEvidence/items/0/address", compact: true));
                    return Wire(request, Evidence(link["address"]!.ToString()));
                default:
                    var body = context["evidence"]!["scope:/groups/leaf/body"]!;
                    Assert.Equal("leaf_value", Assert.Single(body["tasks"]!.AsArray())!["id"]!.ToString());
                    Assert.Equal("multiply", body["tasks"]![0]!["outputs"]![0]!["value"]!["text"]!.ToString());
                    Assert.Equal("value", body["group"]!["inputs"]![0]!["name"]!.ToString());
                    Assert.False(body["open"]!.GetValue<bool>());
                    return Wire(request, Reviewed());
            }
        };
        for (var index = 0; index < 2; index++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.Single(state.Construction!.Checkpoints);
        }
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 3, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.Equal(retainedGroup, JsonSerializer.Serialize(state.Plan!.Groups[1], PlanningJsonContext.Default.TaskGroup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupTaskAliasAndNavigationOnlyFindingsRemainInconclusive(bool navigationFinding)
    {
        var (state, runtime) = await Ready(calledGroup: true);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) => Wire(request, Evidence("scope:/tasks/iteration/body"));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.NotNull(context["groupEvidence"]);
            if (!navigationFinding) return Wire(request, Evidence("task:leaf"));
            return Wire(request, new()
            {
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "omission", ["location"] = "/draft/outputs/0",
                    ["evidence"] = new JsonArray("/groupEvidence/items/0/address"),
                    ["explanation"] = "A navigation link alone does not establish the claimed computation.",
                    ["uncertainty"] = "The group body has not been inspected."
                }),
                ["uncertainty"] = "The original group body is unverified.", ["evidenceRequests"] = null
            });
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(calls + 2, state.ModelCalls);
        var dispatched = runtime.Calls.Count;
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(dispatched, runtime.Calls.Count);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
    }

    [Fact]
    public async Task IntermediateReviewFollowsExactProducerAndChildScopeLinksBeforeAccepting()
    {
        var (state, runtime) = await Ready();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var producer = JsonSerializer.Serialize(state.Plan!.Groups[0].Body.Tasks[0], PlanningJsonContext.Default.PlanTask);
        var calls = state.ModelCalls;
        var reviews = 0;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.IntermediatePurpose, purpose);
            AssertBounded(request);
            Assert.Contains("findings[].location", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("evidenceRequests uses retrieval addresses", request.Prompt, StringComparison.Ordinal);
            var context = Context(request);
            var entry = Assert.Single(context["taskEvidence"]!["items"]!.AsArray())!;
            Assert.Equal(1, context["taskEvidence"]!["count"]!.GetValue<int>());
            Assert.Equal("task:iteration", entry["address"]!.ToString());
            Assert.Equal("scope:/tasks/iteration/body", Assert.Single(entry["scopes"]!.AsArray())!.ToString());
            Assert.DoesNotContain("unrelated", context["taskEvidence"]!.ToJsonString(), StringComparison.Ordinal);
            if (reviews++ == 0) return Wire(request, Evidence(entry["address"]!.ToString()));
            if (reviews == 2)
            {
                var task = context["evidence"]!["task:iteration"]!;
                Assert.Equal("foreach", task["kind"]!.ToString());
                Assert.Null(task["body"]);
                Assert.Equal("/tasks/iteration/body", Assert.Single(task["scopes"]!.AsArray())!.ToString());
                return Wire(request, Evidence(entry["scopes"]![0]!.ToString()));
            }
            var body = context["evidence"]!["scope:/tasks/iteration/body"]!;
            Assert.False(body["open"]!.GetValue<bool>());
            Assert.Equal("per_item", Assert.Single(body["tasks"]!.AsArray())!["id"]!.ToString());
            Assert.Equal("multiply", body["tasks"]![0]!["outputs"]![0]!["value"]!["text"]!.ToString());
            Assert.Equal("item", body["tasks"]![0]!["outputs"]![0]!["value"]!["items"]![0]!["kind"]!.ToString());
            Assert.Equal(2, body["tasks"]![0]!["outputs"]![0]!["value"]!["items"]![1]!["number"]!.GetValue<int>());
            return Wire(request, Reviewed());
        };
        for (var index = 0; index < 2; index++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.Single(state.Construction!.Checkpoints);
            Assert.False(state.Construction.Candidate!.IntermediateReviewed);
            Assert.Null(state.PendingCall);
        }
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 3, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(3, reviews);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(producer, JsonSerializer.Serialize(state.Plan!.Groups[0].Body.Tasks[0], PlanningJsonContext.Default.PlanTask));
        Assert.Equal("iteration", Assert.Single(state.Plan.Groups[0].Body.Outputs).Value.Source);
        Assert.Equal(7, state.Plan.Root.Tasks[0].Outputs[0].Value.Number);
        Assert.False(state.Construction.Complete);
    }

    [Fact]
    public async Task BareProducerIdentityRemainsInconclusiveAndCannotAcceptOrRetry()
    {
        var (state, runtime) = await Ready();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) => Wire(request, Evidence("iteration"));
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Construction!.Checkpoints);
        Assert.False(state.Construction.Candidate!.IntermediateReviewed);
        Assert.Null(state.Construction.Evidence);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        var dispatched = runtime.Calls.Count;
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(dispatched, runtime.Calls.Count);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
    }

    [Fact]
    public async Task HistoricalPendingReviewConsumesItsExactEvidenceReceiptAndAccounting()
    {
        var (state, runtime) = await Ready(calledGroup: true);
        state.Construction!.Compact!.PresentationVersion = null;
        state.Request.Options.Remove("construction_presentation_version");
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
            checkpoint.Status == PlanningStatus.Generating && checkpoint.PendingCall?.Purpose == PlanningConstruction.IntermediatePurpose));
        var issued = saved.PendingCall!.Request;
        Assert.Null(Context(issued)["taskEvidence"]);
        Assert.Null(Context(issued)["groupEvidence"]);
        Assert.DoesNotContain("evidenceRequests uses retrieval addresses", issued.Prompt, StringComparison.Ordinal);
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var reviewContext = saved.Construction!.PendingReviewContext!.ToJsonString();
        var reviewHash = saved.Construction.PendingReviewHash;
        var accepted = PlanningConstruction.Hash(saved.Plan);
        var calls = saved.ModelCalls;
        saved.Construction.Compact!.PresentationVersion = 2;
        saved.Request.Options["construction_presentation_version"] = 2;
        var receipt = Wire(issued, Evidence("scope:/tasks/iteration/body"));
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(reviewContext, saved.Construction.PendingReviewContext!.ToJsonString());
            Assert.Equal(reviewHash, saved.Construction.PendingReviewHash);
            return receipt;
        } };
        state = await Advance(saved, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(recovery.Calls);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.NotNull(state.Construction!.Evidence!["scope:/tasks/iteration/body"]);
        Assert.Null(state.PendingCall);
        Assert.Single(state.Construction.Checkpoints);
        recovery.Respond = (request, _) =>
        {
            Assert.NotNull(Context(request)["taskEvidence"]);
            Assert.Equal("scope:/groups/leaf/body", Assert.Single(Context(request)["groupEvidence"]!["items"]!.AsArray())!["address"]!.ToString());
            AssertBounded(request);
            return Wire(request, Reviewed());
        };
        state = await Advance(state, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(bool calledGroup = false)
    {
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            var context = Context(request);
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                return Wire(request, new() { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                    ["clauses"] = new JsonArray(new JsonObject { ["text"] = context["sources"]![0]!["text"]!.ToString(),
                        ["kind"] = "requirement", ["interfaces"] = "none" }) });
            if (purpose == PlanningCompactConstruction.SelectPurpose)
                return Wire(request, new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["evidenceRequests"] = null, ["clarifications"] = null,
                    ["step"] = new JsonObject { ["scope"] = "/groups/worker/body", ["kind"] = "close", ["objective"] = "Expose the ordered doubled values from the reusable work.",
                        ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "output", ["source"] = "iteration", ["port"] = "result" }),
                        ["dependencyTaskIds"] = new JsonArray("iteration"), ["expectedOutputs"] = new JsonArray("Reusable ordered doubled values."),
                        ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["groupId"] = null } });
            if (purpose == PlanningConstruction.DraftPurpose)
                return Wire(request, new() { ["baseHash"] = context["baseHash"]!.DeepClone(), ["requirements"] = null, ["commitments"] = null,
                    ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                    {
                        Scope = "/groups/worker/body", Outputs = [new("result", new() { Kind = "output", Source = "iteration", Port = "result" })],
                        CloseScopes = ["/groups/worker/body"], RequirementIds = ["req_0"]
                    }, PlanningJsonContext.Default.PlanningConstructionExtension),
                    ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                    ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null });
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return Wire(request, Reviewed());
        } };
        var state = await Advance(new() { Request = new() { TenantId = "linked-review",
            Prompt = "Double each original number through reusable work, preserving order and repetition.",
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        state.Plan = new()
        {
            Root = new() { Tasks = [new() { Id = "unrelated", Kind = "value", Objective = "Retained independent value.",
                Outputs = [new("value", new() { Kind = "number", Number = 7 })] }] },
            Groups = [new() { Id = "worker", Inputs = [new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "number" } } }],
                Body = new() { Tasks = [new() { Id = "iteration", Kind = "foreach", Objective = "Double each original value.",
                    Items = new() { Kind = "input", Source = "values" }, MaxItems = 100, MaxConcurrency = 1,
                    Body = new() { Tasks = [new() { Id = "per_item", Kind = "value", Objective = "Double the current value.",
                        Outputs = [new("value", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "item" }, new() { Kind = "number", Number = 2 }] })] }],
                        Outputs = [new("result", new() { Kind = "output", Source = "per_item", Port = "value" })] } }] } }]
        };
        if (calledGroup)
        {
            state.Plan.Groups[0].Body.Tasks[0].Body!.Tasks =
            [
                new() { Id = "per_item", Kind = "call", Group = "leaf", Objective = "Invoke the reusable per-item calculation.",
                    Inputs = [new("value", new() { Kind = "item" })] }
            ];
            state.Plan.Groups.Add(new()
            {
                Id = "leaf", Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }],
                Body = new()
                {
                    Tasks = [new() { Id = "leaf_value", Kind = "value", Objective = "Double the supplied value.",
                        Outputs = [new("value", new() { Kind = "arithmetic", Text = "multiply",
                            Items = [new() { Kind = "input", Source = "value" }, new() { Kind = "number", Number = 2 }] })] }],
                    Outputs = [new("value", new() { Kind = "output", Source = "leaf_value", Port = "value" })]
                }
            });
            state.Plan.Groups.Add(new() { Id = "unrelated_group" });
        }
        state.Construction!.OpenScopes = ["/root", "/groups/worker/body"];
        state.Construction.Requirements[0].Status = "in_progress";
        state.Construction.Checkpoints.Add(new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(state.Plan),
            TaskIds = ["iteration", "per_item"], RequirementIds = ["req_0"] });
        state = await Advance(state, runtime); // Select the group closure.
        Assert.Empty(state.Diagnostics);
        state = await Advance(state, runtime); // Validate its candidate.
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Candidate!.NeedsIntermediateReview);
        state = await Advance(state, runtime); // The local review does not replace the required intermediate review.
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Candidate!.LocalReviewed);
        Assert.False(state.Construction.Candidate.IntermediateReviewed);
        return (state, runtime);
    }

    private static void AssertBounded(LLMRequest request) =>
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningConstruction.IntermediatePurpose, 4000).EstimatedInputTokens, 1, 4000);
    private static JsonObject Evidence(string address) => new() { ["findings"] = null,
        ["uncertainty"] = "Inspect the accepted producer before reviewing its exported result.", ["evidenceRequests"] = new JsonArray(address) };
    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(),
        ["uncertainty"] = "Execution and remaining requirements are unverified.", ["evidenceRequests"] = null };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
