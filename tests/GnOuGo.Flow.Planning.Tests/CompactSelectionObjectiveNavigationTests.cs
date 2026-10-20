using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionObjectiveNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteNavigationRetainsExactObjectiveAndEveryPort(bool historical)
    {
        var state = await Ready();
        if (historical) state.Construction!.Compact!.PresentationVersion = null;
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var directory = PlanningCompactConstruction.InterfaceDirectory(state, null, 0);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var interfaces = context["interfaces"]!.AsArray();
        var entry = interfaces.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        var prior = directory.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        Assert.False(entry["objectiveComplete"]!.GetValue<bool>());
        Assert.Equal(historical, entry.ContainsKey("objective"));
        Assert.Equal(historical ? prior["objective"]!.ToString() : null, entry["objective"]?.ToString());
        foreach (var field in prior.Where(field => field.Key != "objective"))
            Assert.True(JsonNode.DeepEquals(field.Value, entry[field.Key]), field.Key);
        Assert.Equal(10, entry["portCount"]!.GetValue<int>());
        Assert.Equal(8, entry["ports"]!.AsArray().Count);
        Assert.NotNull(entry["nextPorts"]);
        var shortEntry = interfaces.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "short");
        Assert.True(shortEntry["objectiveComplete"]!.GetValue<bool>());
        Assert.NotNull(state.Plan);
        Assert.Equal(state.Plan.Root.Tasks[2].Objective, shortEntry["objective"]!.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var address = entry["objectiveEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var evidence = Assert.IsType<JsonObject>(state.Construction!.Evidence);
        Assert.Equal(state.Plan.Root.Tasks[0].Objective, evidence[address]!["text"]!.ToString());
        Assert.Null(evidence[address]!["next"]);
        var stale = PlannerFixture.Clone(state);
        stale.Construction!.EvidenceRequests.Clear();
        stale.Plan!.Root.Tasks[0].Objective += " Changed original.";
        var before = JsonSerializer.Serialize(stale, PlanningJsonContext.Default.PlanningSession);
        var error = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(stale, [address]));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        Assert.Equal(before, JsonSerializer.Serialize(stale, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task CompleteSelectionRequestOmitsOnlyIncompleteNavigationPrefixes()
    {
        var state = await Ready();
        var context = PlanningCompactConstruction.SelectionContext(state);
        var previous = context.DeepClone().AsObject();
        previous["interfaces"] = PlanningCompactConstruction.InterfaceDirectory(state, null, 0);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        var current = PlanningRequestMeasurements.Measure(Request(state, context, schema),
            PlanningCompactConstruction.SelectPurpose, 3000);
        var old = PlanningRequestMeasurements.Measure(Request(state, previous, schema),
            PlanningCompactConstruction.SelectPurpose, 3000);
        Assert.Equal(old.SchemaHash, current.SchemaHash);
        Assert.Equal(old.BaselineHash, current.BaselineHash);
        Assert.True(current.CompleteRequestBytes < old.CompleteRequestBytes - 400);
        Assert.InRange(current.EstimatedInputTokens, 1, 3000);
        Assert.Equal(current.CompleteRequestBytes, current.Components.Sum(component => component.SerializedBytes));
        foreach (var field in previous.Where(field => field.Key != "interfaces"))
            Assert.True(JsonNode.DeepEquals(field.Value, context[field.Key]), field.Key);
        var before = previous["interfaces"]!.AsArray();
        var after = context["interfaces"]!.AsArray();
        Assert.Equal(before.Count, after.Count);
        for (var index = 0; index < before.Count; index++)
            foreach (var field in before[index]!.AsObject())
                if (field.Key != "objective" || before[index]!["objectiveComplete"]?.GetValue<bool>() != false)
                    Assert.True(JsonNode.DeepEquals(field.Value, after[index]![field.Key]), field.Key);
    }

    [Fact]
    public async Task SavedSelectionWithIncompletePrefixesResumesExactIssuedBytesAndAccounting()
    {
        var state = await Ready();
        var context = PlanningCompactConstruction.SelectionContext(state);
        context["interfaces"] = PlanningCompactConstruction.InterfaceDirectory(state, null, 0);
        var request = Request(state, context, PlanningCompactConstruction.SelectionSchema(state));
        state.ModelCalls++;
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state = PlannerFixture.Clone(state);
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var calls = state.ModelCalls;
        var address = context["interfaces"]!.AsArray().OfType<JsonObject>().First()["objectiveEvidence"]!.ToString();
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject
        {
            ["baseHash"] = accepted, ["step"] = null, ["clarifications"] = null,
            ["evidenceRequests"] = new JsonArray(address)
        } } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, result.ModelCalls);
        Assert.Equal(0, result.ReplanAttempts);
        Assert.Equal(accepted, PlanningConstruction.Hash(result.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(result.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(state.Plan!.Root.Tasks[0].Objective, result.Construction!.Evidence![address]!["text"]!.ToString());
    }

    private static LLMRequest Request(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            context, schema.DeepClone().AsObject(), PlanningCompactConstruction.SelectPurpose);
        request.Provider = "OpenAi"; request.Model = "retained-model"; request.StructuredOutputStrict = true;
        request.UseBackgroundMode = true; request.MaxTokens = 8192;
        request.ClientRequestId = state.Request.SessionId + ":3:" + new string('a', 64);
        return PlanningGenerationPolicy.Apply(request, state.Request.Generation);
    }

    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Preserve every supplied value and its ordering in the requested result.";
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["sourceId"] = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
        } } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(new() { Request = new()
        { TenantId = "selection-objectives", Prompt = objective, Options = new() { ["automatic_correction"] = true } } },
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        state.Catalog = new();
        state.Plan = new() { Root = new() { Tasks = [Task("first", 10), Task("second", 7), Task("short", 1)] } };
        state.Plan.Root.Tasks[2].Objective = "Keep the supplied value.";
        return state;

        static PlanTask Task(string id, int ports) => new()
        {
            Id = id, Kind = "value", Objective = string.Concat(Enumerable.Repeat("Keep each original observed value and its exact identity. ", 8)),
            Outputs = Enumerable.Range(0, ports).Select(index => new TaskOutput("field_" + index,
                new() { Kind = "string", Text = "observed-" + index })).ToList()
        };
    }
}
