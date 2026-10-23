using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactAcceptedTaskNavigationTests
{
    private const string NestedScope = "/tasks/container/body";

    [Theory]
    [InlineData("/root", false)]
    [InlineData("/root", true)]
    [InlineData(NestedScope, false)]
    [InlineData(NestedScope, true)]
    [InlineData("/groups/worker/body", false)]
    [InlineData("/groups/worker/body", true)]
    public void NavigationIncludesOnlyAcceptedImmediateSiblingsInTheSelectedScope(string scope, bool review)
    {
        var state = Ready(scope);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var symbols = new TaskPlanSymbols(state.Plan!);
        var siblings = symbols.Tasks.Values.Where(site => site.Scope.Path == scope).Select(site => site.Task).ToArray();
        var navigation = Navigation(state, review);
        Assert.Equal(siblings.Length, navigation["count"]!.GetValue<int>());
        Assert.Equal(0, navigation["remaining"]!.GetValue<int>());
        Assert.Null(navigation["remainingEvidence"]);
        Assert.Null(navigation["scopeEvidence"]);
        Assert.Equal(siblings.Select(task => "task:" + task.Id), Addresses(navigation));
        foreach (var item in navigation["items"]!.AsArray())
        {
            var id = item!["task"]!.ToString();
            Assert.Equal(scope, item["scope"]!.ToString());
            Assert.Equal(symbols.Phase(id), item["phase"]!.ToString());
            Assert.Equal(symbols.Tasks[id].Task.Kind, item["kind"]!.ToString());
            Assert.Equal(symbols.Tasks[id].Task.Objective, item["objective"]!.ToString());
            Assert.True(item["objectiveComplete"]!.GetValue<bool>());
        }
        Assert.DoesNotContain("candidate", navigation.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedScopesAndGroupsDoNotGrowTheCompleteRequest(bool review)
    {
        var state = Ready(NestedScope);
        var before = Context(state, review);
        var first = Measure(state, review);
        for (var index = 0; index < 100; index++)
        {
            state.Plan!.Root.Tasks.Add(new() { Id = "unrelated_" + index, Kind = "sequence", Objective = new string('x', 4000),
                Body = new() { Tasks = [Value("unrelated_child_" + index)] } });
            state.Plan.Groups.Add(new() { Id = "unrelated_group_" + index, Body = new() { Tasks = [Value("unrelated_group_task_" + index)] } });
        }
        Stage(state);
        var after = Context(state, review);
        var last = Measure(state, review);
        Assert.True(JsonNode.DeepEquals(before["taskEvidence"], after["taskEvidence"]));
        Assert.DoesNotContain("unrelated", after.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(first.CompleteRequestBytes, last.CompleteRequestBytes);
        Assert.Equal(first.EstimatedInputTokens, last.EstimatedInputTokens);
        Assert.Equal(last.CompleteRequestBytes, last.Components.Sum(component => component.SerializedBytes));
        Assert.InRange(last.EstimatedInputTokens, 1, review ? 8000 : 16000);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    public void ImportsHavePriorityAndRecentSiblingsShareTheBoundWithoutDuplicates(int externalCount, bool review)
    {
        var state = Ready(NestedScope);
        var scope = new TaskPlanSymbols(state.Plan!).Scopes.Single(site => site.Path == NestedScope).Source;
        scope.Tasks = Enumerable.Range(0, 6).Select(index => Value("sibling_" + index)).ToList();
        scope.Always = [Value("cleanup")];
        var external = Enumerable.Range(0, externalCount).Select(index => "external_" + index).ToArray();
        state.Plan!.Root.Tasks.InsertRange(0, external.Select(Value));
        Stage(state);
        var producers = external.Concat(["sibling_0"]).ToArray();
        // These are presentation hints; the candidate's semantic bindings and
        // complete compiler validation remain separate from this directory.
        state.Construction!.Compact!.CurrentStep!.InputTaskIds = producers.Concat(["sibling_0"]).ToList();
        state.Construction.Compact.CurrentStep.DependencyTaskIds = ["sibling_0"];
        state.Construction.Candidate!.ImportedTaskIds = producers.Concat(["sibling_0"]).ToList();
        var navigation = Navigation(state, review);
        var addresses = Addresses(navigation);
        var importedCount = Math.Min(8, producers.Length);
        var siblingCount = Math.Min(4, 8 - importedCount);
        Assert.Equal(externalCount + 7, navigation["count"]!.GetValue<int>());
        Assert.Equal(importedCount + siblingCount, addresses.Length);
        Assert.Equal(addresses.Length, addresses.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(producers.Take(8).Select(id => "task:" + id), addresses.Take(importedCount));
        var recent = scope.Tasks.Concat(scope.Always).Where(task => task.Id != "sibling_0").TakeLast(siblingCount);
        Assert.Equal(recent.Select(task => "task:" + task.Id), addresses.Skip(importedCount));
        Assert.Equal(externalCount + 7 - addresses.Length, navigation["remaining"]!.GetValue<int>());
        Assert.Equal("page:tasks:0", navigation["remainingEvidence"]!.ToString());
        Assert.Equal("scope:" + NestedScope, navigation["scopeEvidence"]!.ToString());
        Assert.DoesNotContain("task:candidate", addresses);
    }

    [Theory]
    [InlineData("opaque repeated text ")]
    [InlineData("汉字😀\"\\\n")]
    public void BoundedObjectivesRetainExactRetrievalAndCallOrOperationIdentity(string text)
    {
        var state = Ready(NestedScope);
        var scope = new TaskPlanSymbols(state.Plan!).Scopes.Single(site => site.Path == NestedScope).Source;
        var objective = string.Concat(Enumerable.Repeat(text, 80));
        scope.Tasks =
        [
            new() { Id = "reusable_call", Kind = "call", Group = "worker", Objective = objective },
            new() { Id = "operation_call", Kind = "operation", Operation = "service@v1/read", Objective = objective },
            new() { Id = "nested_container", Kind = "sequence", Objective = "Retain its existing child scope.",
                Body = new() { Tasks = [Value("child")] } }
        ];
        scope.Always.Clear();
        state.Construction!.OpenScopes = new TaskPlanSymbols(state.Plan!).Scopes.Select(site => site.Path).ToList();
        Stage(state);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var context = Context(state, review: true);
        var items = context["taskEvidence"]!["items"]!.AsArray();
        Assert.Equal("worker", items[0]!["group"]!.ToString());
        Assert.Equal("service@v1/read", items[1]!["operation"]!.ToString());
        Assert.Equal(1, items[2]!["scopeCount"]!.GetValue<int>());
        Assert.Equal("scope:/tasks/nested_container/body", Assert.Single(items[2]!["scopes"]!.AsArray())!.ToString());
        foreach (var historical in new[] { false, true })
        {
            var selection = PlannerFixture.Clone(state);
            if (historical)
            {
                selection.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
                selection.Construction!.Limits.PhasePolicy = null;
            }
            var directory = PlanningCompactConstruction.InterfaceDirectory(selection, null, 0);
            var presented = PlanningCompactConstruction.SelectionContext(selection)["interfaces"]!.AsArray();
            foreach (var entries in new[] { directory, presented })
            {
                var call = Assert.Single(entries, entry => entry?["task"]?.ToString() == "reusable_call")!;
                var operation = Assert.Single(entries, entry => entry?["task"]?.ToString() == "operation_call")!;
                Assert.Equal(historical ? null : "worker", call["group"]?.ToString());
                Assert.Equal(historical ? null : "service@v1/read", operation["operation"]?.ToString());
                Assert.Equal("task:reusable_call", call["evidence"]!.ToString());
                Assert.Equal("task:operation_call", operation["evidence"]!.ToString());
            }
        }
        foreach (var item in items.Take(2))
        {
            var excerpt = item!["objective"]!.ToString();
            Assert.False(item["objectiveComplete"]!.GetValue<bool>());
            Assert.StartsWith(excerpt, objective, StringComparison.Ordinal);
            Assert.InRange(Encoding.UTF8.GetByteCount(item["objective"]!.ToJsonString()), 1, 204);
            Assert.StartsWith("task-objective:" + item["task"] + "@", item["objectiveEvidence"]!.ToString(), StringComparison.Ordinal);
        }
        var address = items[0]!["objectiveEvidence"]!.ToString();
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, address, out var evidence));
        Assert.StartsWith(evidence!["text"]!.ToString(), objective, StringComparison.Ordinal);
        Assert.Equal(PlanningGraphCompiler.Fingerprint(objective), evidence["version"]!.ToString());
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
    }

    [Fact]
    public void NavigationCannotGroundFindingsButItsTaskAddressRetrievesOriginalBindings()
    {
        var state = Ready(NestedScope);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var context = Context(state, review: true);
        var item = context["taskEvidence"]!["items"]![0]!;
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0", compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0/objective", compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0/address", compact: true));
        var address = item["address"]!.ToString();
        var task = new TaskPlanSymbols(state.Plan!).Tasks[item["task"]!.ToString()].Task;
        PlanningConstruction.LoadEvidence(state, [address]);
        var reviewed = Context(state, review: true);
        var original = PlanningConstruction.TaskNode(task);
        original["scope"] = NestedScope;
        original["phase"] = "tasks";
        Assert.True(JsonNode.DeepEquals(original, reviewed["evidence"]![address]));
        Assert.Equal(7d, reviewed["evidence"]![address]!["outputs"]![0]!["value"]!["number"]!.GetValue<double>());
        Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(reviewed, "/evidence/" + address, compact: true));
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void HistoricalPolicyKeepsTheExactProducerOnlyShapeAndNoGenerationDirectory()
    {
        var state = Ready(NestedScope);
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction!.Limits.PhasePolicy = null;
        state.Construction.Candidate!.ImportedTaskIds = ["nested_value"];
        var expected = new JsonObject
        {
            ["count"] = 1,
            ["items"] = new JsonArray(new JsonObject { ["address"] = "task:nested_value", ["scopeCount"] = 0, ["scopes"] = new JsonArray() })
        };
        Assert.True(JsonNode.DeepEquals(expected, Navigation(state, review: true)));
        Assert.Null(Context(state, review: false)["taskEvidence"]);
        state.Construction.Compact!.PresentationVersion = null;
        Assert.Null(Context(state, review: true)["taskEvidence"]);
    }

    [Fact]
    public async Task IssuedProducerOnlyReviewResumesExactBytesWithoutRebuildingSiblingContext()
    {
        var state = Ready(NestedScope);
        Stage(state, ["nested_value"]);
        var construction = state.Construction!;
        var pending = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        pending.Context["taskEvidence"] = new JsonObject
        {
            ["count"] = 1,
            ["items"] = new JsonArray(new JsonObject { ["address"] = "task:nested_value", ["scopeCount"] = 0, ["scopes"] = new JsonArray() })
        };
        var prompt = pending.Request.Prompt[..(pending.Request.Prompt.LastIndexOf('\n') + 1)] + pending.Context.ToJsonString();
        construction.PendingReviewContext = pending.Context;
        construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            construction.Candidate!.Hash + "\n" + pending.Context.ToJsonString());
        var interrupted = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, interrupted,
            PlanningConstruction.LocalPurpose, prompt, pending.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct,
            construction.Limits.ReviewOutputTokens));
        var saved = PlannerFixture.Clone(interrupted.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var retainedContext = saved.Construction!.PendingReviewContext!.ToJsonString();
        var retainedHash = saved.Construction.PendingReviewHash;
        var calls = saved.ModelCalls;
        var accepted = PlanningConstruction.Hash(saved.Plan);
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(retainedContext, saved.Construction.PendingReviewContext!.ToJsonString());
            Assert.Equal(retainedHash, saved.Construction.PendingReviewHash);
            Assert.Equal(accepted, PlanningConstruction.Hash(saved.Plan));
            return new() { Json = new JsonObject { ["findings"] = new JsonArray(), ["evidenceRequests"] = null,
                ["uncertainty"] = "Runtime execution remains unverified." } };
        } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(saved, new() { ExpectedRevision = saved.Revision }, recovery, PlannerFixture.Ct);
        Assert.Empty(result.Diagnostics);
        Assert.Single(recovery.Calls);
        Assert.Equal(calls, result.ModelCalls);
        Assert.Equal(0, result.ReplanAttempts);
        Assert.Null(result.PendingCall);
        Assert.Single(result.Construction!.Checkpoints);
    }

    private static PlanningSession Ready(string scope)
    {
        const string requirement = "Preserve the requested values and complete their declared processing.";
        var state = new PlanningSession { Request = new() { TenantId = "accepted-task-navigation", Prompt = requirement } };
        PlanningSessionCreation.Capture(state);
        // Directory-only fixtures deliberately contain unresolved synthetic
        // plans; retain their protocol instead of asserting verified v4 facts.
        state.Request.Options["construction_presentation_version"] = 3;
        state.Construction!.Compact!.PresentationVersion = 3;
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        PlanningCompactConstruction.InitializeSources(state);
        var construction = state.Construction!;
        var compact = construction.Compact!;
        foreach (var source in compact.Sources) source.Indexed = true;
        compact.RequirementSources["req_0"] = [compact.Sources[0].Id];
        state.Requirements = new() { Outcomes = [new("req_0", requirement)], Inputs = [], Outputs = [] };
        construction.Requirements = [new() { Id = "req_0" }];
        state.Catalog = new();
        state.Plan = new()
        {
            Root = new() { Tasks = [Value("root_value"), new() { Id = "container", Kind = "sequence", Objective = "Process the declared values.",
                Body = new() { Tasks = [Value("nested_value"), new() { Id = "child_container", Kind = "sequence", Objective = "Keep its child isolated.",
                    Body = new() { Tasks = [Value("child_value")] } }], Always = [Value("cleanup_value")] } }] },
            Groups = [new() { Id = "worker", Body = new() { Tasks = [Value("group_value")] } }]
        };
        construction.OpenScopes = new TaskPlanSymbols(state.Plan).Scopes.Select(site => site.Path).ToList();
        compact.CurrentStep = new() { Id = "selected", Scope = scope, Kind = "value", Objective = "Retain the next declared value.",
            RequirementIds = ["req_0"], DiscoveryComplete = true };
        Stage(state);
        return state;
    }

    private static void Stage(PlanningSession state, List<string>? dependencies = null)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var task = Value("candidate");
        task.DependsOn = dependencies ?? [];
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
            { Scope = step.Scope, Tasks = [task], RequirementIds = ["req_0"] });
    }
    private static PlanTask Value(string id) => new() { Id = id, Kind = "value", Objective = "Preserve the declared value.",
        Outputs = [new("value", new() { Kind = "number", Number = 7 })] };
    private static JsonObject Context(PlanningSession state, bool review) => PlanningCompactConstruction.Context(state, review);
    private static JsonObject Navigation(PlanningSession state, bool review) => Context(state, review)["taskEvidence"]!.AsObject();
    private static string[] Addresses(JsonObject navigation) => navigation["items"]!.AsArray().Select(item => item!["address"]!.ToString()).ToArray();
    private static PlanningRequestMeasurement Measure(PlanningSession state, bool review)
    {
        var purpose = review ? PlanningConstruction.LocalPurpose : PlanningConstruction.DraftPurpose;
        var request = review ? HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request :
            PlanningCompactConstruction.Request(PlanningCompactConstruction.DraftGuidance(state), Context(state, review: false),
                PlanningCompactConstruction.DraftSchema(state), purpose);
        return PlanningRequestMeasurements.Measure(request, purpose, review ? 8000 : 16000);
    }
}
