using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactReviewContextTests
{
    [Fact]
    public async Task EvidenceFollowupKeepsExactEvidenceAndConstraintsWithoutDuplicatingItsGroup()
    {
        var state = await State();
        var originalPlan = PlanningConstruction.Hash(state.Plan);
        var first = PlanningCompactConstruction.Context(state, review: true);
        var originalGroup = first["scopeGroup"]!.DeepClone();
        var originalStep = JsonSerializer.SerializeToNode(state.Construction!.Compact!.CurrentStep,
            PlanningJsonContext.Default.PlanningConstructionStep)!;
        var runtime = new TestRuntime { Respond = (_, _) => Review(new JsonArray("task:producer", "scope:/groups/reusable/body")) };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var evidence = state.Construction!.Evidence!.ToJsonString();
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Equal(evidence, context["evidence"]!.ToJsonString());
            Assert.Null(context["scopeGroup"]);
            Assert.True(JsonNode.DeepEquals(originalGroup, context["evidence"]!["scope:/groups/reusable/body"]!["group"]));
            foreach (var field in originalStep.AsObject().Where(p => p.Key is not ("baseHash" or "discoveryComplete" or "inputs" or "inputTaskIds" or "dependencyTaskIds")))
                Assert.True(JsonNode.DeepEquals(field.Value, context["step"]![field.Key]), field.Key);
            var imported = context["interfaces"]![0]!;
            Assert.Equal(1, imported["schema"]!["minItems"]!.GetValue<int>());
            Assert.Equal(8, imported["schema"]!["maxItems"]!.GetValue<int>());
            Assert.Equal(JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep!.Inputs[0], PlanningJsonContext.Default.TaskValue),
                JsonSerializer.Serialize(imported["value"]!.Deserialize(PlanningJsonContext.Default.TaskValue), PlanningJsonContext.Default.TaskValue));
            Assert.Equal(first["requirements"]!.ToJsonString(), context["requirements"]!.ToJsonString());
            Assert.Equal(first["globalConstraints"]!.ToJsonString(), context["globalConstraints"]!.ToJsonString());
            Assert.Equal(first["draft"]!.ToJsonString(), context["draft"]!.ToJsonString());
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 4000).EstimatedInputTokens, 1, 4000);
            return Review();
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.DoesNotContain(state.Diagnostics, d => d.Required);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(originalPlan, state.Construction!.Checkpoints[^1].BaseHash);
    }

    [Fact]
    public async Task DifferentOrPartialGroupEvidenceDoesNotSuppressTheCompleteSelectedGroup()
    {
        var state = await State();
        PlanningConstruction.LoadEvidence(state, ["scope:/groups/reusable/body"]);
        var original = state.Construction!.Evidence!.DeepClone().AsObject();
        foreach (var change in new[] { "hash", "inputs", "missing" })
        {
            state.Construction.Evidence = original.DeepClone().AsObject();
            var group = state.Construction.Evidence["scope:/groups/reusable/body"]!["group"]!.AsObject();
            if (change == "missing") group.Remove("hash");
            else group[change] = change == "hash" ? JsonValue.Create("different") : new JsonArray();
            var context = PlanningCompactConstruction.Context(state, review: true);
            Assert.NotNull(context["scopeGroup"]);
            Assert.Equal(state.Construction.Evidence.ToJsonString(), context["evidence"]!.ToJsonString());
        }
    }

    [Fact]
    public async Task HistoricalReviewAndGenerationKeepTheirOriginalContextRepresentation()
    {
        var state = await State();
        PlanningConstruction.LoadEvidence(state, ["scope:/groups/reusable/body"]);
        var generation = PlanningCompactConstruction.Context(state);
        Assert.NotNull(generation["scopeGroup"]);
        Assert.NotNull(generation["step"]!["baseHash"]);
        Assert.NotNull(generation["step"]!["discoveryComplete"]);
        state.Request.Options.Remove("construction_presentation_version");
        state.Construction!.Compact!.PresentationVersion = null;
        state.Construction.Compact.InterfaceExtractions = null;
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction.Limits.PhasePolicy = null;
        var retained = PlanningCompactConstruction.Context(state, review: true);
        Assert.Equal(JsonSerializer.SerializeToNode(state.Construction.Compact.CurrentStep,
            PlanningJsonContext.Default.PlanningConstructionStep)!.ToJsonString(), retained["step"]!.ToJsonString());
        Assert.NotNull(retained["scopeGroup"]);
        Assert.Equal(PlanningCompactConstruction.ImportedInterfaces(state, state.Construction.Compact.CurrentStep!).ToJsonString(),
            retained["interfaces"]!.ToJsonString());
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.Status == PlanningStatus.Generating && s.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var contextHash = saved.Construction!.PendingReviewHash;
        var calls = saved.ModelCalls;
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(contextHash, saved.Construction.PendingReviewHash);
            return Review();
        } };
        var resumed = await Advance(saved, recovery);
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Single(recovery.Calls);
        Assert.DoesNotContain(resumed.Diagnostics, d => d.Required);
    }

    private static async Task<PlanningSession> State()
    {
        const string requirement = "Expose the complete supplied sequence through the reusable boundary, preserving order and repetitions.";
        var state = new PlanningSession { Request = new() { TenantId = "compact-review-context", Prompt = requirement } };
        PlanningSessionCreation.Capture(state);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        var compact = construction.Compact!;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in compact.Sources) source.Indexed = true;
        compact.RequirementSources["result"] = [compact.Sources[0].Id];
        state.Requirements = new() { Outcomes = [new("result", requirement)], Inputs = [], Outputs = [] };
        construction.Requirements = [new() { Id = "result" }];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Groups = [new() { Id = "reusable",
            Inputs = [new() { Name = "sequence", Type = new() { Kind = "array", MinItems = 1, MaxItems = 8,
                Items = new() { Kind = "string", Nullable = true, Enum = ["first", "second"] } } }],
            Body = new() { Tasks = [new() { Id = "producer", Kind = "value", Objective = "Preserve each supplied value unchanged.",
                Outputs = [new("sequence", new() { Kind = "input", Source = "sequence" })] }] } }] };
        construction.OpenScopes = ["/root", "/groups/reusable/body"];
        compact.CurrentStep = new() { Id = "close_boundary", Scope = "/groups/reusable/body", Kind = "close",
            BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Expose the exact sequence and close this reusable boundary.",
            RequirementIds = ["result"], InputTaskIds = ["producer"], Inputs = [new() { Kind = "output", Source = "producer", Port = "sequence" }],
            ExpectedOutputs = ["The complete sequence with original order, repetitions and explicit nulls."], DiscoveryComplete = true };
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/groups/reusable/body",
            Outputs = [new("sequence", new() { Kind = "output", Source = "producer", Port = "sequence" })],
            RequirementIds = ["result"], CloseScopes = ["/groups/reusable/body"] });
        return state;
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Review(JsonArray? evidence = null)
    {
        var response = new JsonObject { ["findings"] = evidence is null ? new JsonArray() : null,
            ["evidenceRequests"] = evidence, ["uncertainty"] = "Execution and business outcomes remain unverified." };
        return new() { Json = response, Text = response.ToJsonString() };
    }
}
