using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactAcceptedExportNavigationTests(ITestOutputHelper output)
{
    private const string Scope = "/tasks/container/body";

    [Fact]
    public async Task ScalarConsumerReviewFitsWithoutRepeatingUnchangedObjectExportTypes()
    {
        var state = await Ready(14000);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var full = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!;
        var imports = PlanningCompactConstruction.ActualImportedInterfaces(state);
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.True(JsonNode.DeepEquals(imports, context["interfaces"]));
            Assert.Equal(3, imports.Count);
            Assert.All(imports, imported => Assert.DoesNotContain("unused", imported!["schema"]!.ToJsonString(), StringComparison.Ordinal));
            AssertNavigation(full, context["scopeInterface"]!.AsObject());
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).MaxInputTokens;
            var measured = PlanningRequestMeasurements.Measure(request, purpose, limit);
            Assert.Equal(8000, limit);
            Assert.InRange(measured.EstimatedInputTokens, 1, limit);
            Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));

            // Reconstruct only the previous automatic export view in the exact
            // dispatched envelope; no import, clause, schema or setting changes.
            var previous = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest),
                PlanningJsonContext.Default.LLMRequest)!;
            context["scopeInterface"] = full.DeepClone();
            previous.Prompt = request.Prompt[..(request.Prompt.LastIndexOf('\n') + 1)] + context.ToJsonString();
            Assert.True(JsonNode.DeepEquals(HybridWorkflowPlanner.ConstructionReviewSchema(context),
                HybridWorkflowPlanner.ConstructionReviewSchema(Context(request))));
            var original = PlanningRequestMeasurements.Measure(previous, purpose, limit);
            Assert.True(original.EstimatedInputTokens > limit);
            output.WriteLine($"Complete review: previous {original.CompleteRequestBytes}B/{original.EstimatedInputTokens} tokens; navigation {measured.CompleteRequestBytes}B/{measured.EstimatedInputTokens} tokens; ceiling {limit}.");
            return Review(request);
        } };
        var reviewed = await Advance(state, runtime);
        Assert.Empty(reviewed.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Single(reviewed.Construction!.Checkpoints);
        Assert.Equal(0, reviewed.ReplanAttempts);
        Assert.Equal(2, reviewed.Plan!.Root.Tasks[0].Body!.Outputs.Count);
        Assert.Empty(reviewed.Construction.Compact!.CurrentStep?.CloseRequirementIds ?? []);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(14000, false)]
    public async Task IssuedExportAddressResolvesExactEvidenceOrStopsAtCompleteRequestAdmission(int length, bool fits)
    {
        var state = await Ready(length);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var original = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!;
        string? address = null;
        var calls = state.ModelCalls;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var view = Context(request)["scopeInterface"]!.AsObject();
            AssertNavigation(original, view);
            address = view["evidence"]!.ToString();
            var response = Review(request, new JsonArray(address));
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json!, request.StructuredOutputSchema!));
            return response;
        } };
        state = await Advance(state, runtime);
        Assert.NotNull(address);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        if (!fits)
        {
            Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
            Assert.Null(state.Construction.Evidence?[address]);
            Assert.Contains(state.RequestMeasurements!, measurement => measurement.Admission == "rejected_before_dispatch" &&
                measurement.Phase == PlanningConstruction.LocalPurpose && measurement.EstimatedInputTokens > measurement.InputLimit);
            Assert.Single(runtime.Calls);
            return;
        }
        Assert.Empty(state.Diagnostics);
        Assert.True(JsonNode.DeepEquals(original, state.Construction.Evidence![address]));
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.True(JsonNode.DeepEquals(original, context["evidence"]![address]));
            AssertNavigation(original, context["scopeInterface"]!.AsObject());
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context,
                "/evidence/" + address.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), compact: true));
            return Review(request);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("exports")]
    [InlineData("scope")]
    [InlineData("requirement")]
    [InlineData("plan")]
    [InlineData("generation")]
    [InlineData("historical")]
    [InlineData("revision")]
    public async Task ChangedExportsAndClosureClaimsKeepTheExactTypes(string claim)
    {
        var state = await Ready(32);
        var construction = state.Construction!;
        var extension = construction.Candidate!.Extension;
        switch (claim)
        {
            case "exports": extension.Outputs.Add(new("new_export", new() { Kind = "string", Text = "new" })); break;
            case "scope": extension.CloseScopes.Add(Scope); break;
            case "requirement": construction.Compact!.CurrentStep!.CloseRequirementIds = ["req_0"]; break;
            case "plan": extension.Complete = true; break;
            case "historical": Historical(state); break;
            case "revision": construction.Candidate.AuthorizedPaths = ["/tasks/first/outputs/record"]; break;
        }
        if (claim == "scope") construction.Candidate.OpenScopes.Remove(Scope);
        var context = PlanningCompactConstruction.Context(state, review: claim != "generation");
        var exact = PlanningCompactConstruction.ScopeInterface(state, candidate: claim == "scope")!;
        Assert.True(JsonNode.DeepEquals(exact, context["scopeInterface"]));
        Assert.All(context["scopeInterface"]!["outputs"]!.AsArray(), item => Assert.NotNull(item!["schema"]));
        Assert.Null(context["scopeInterface"]!["evidence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedFullExportReviewRecoversExactBytesContextAndAccounting(bool historical)
    {
        var state = await Ready(32, presentation: 3);
        if (historical) Historical(state);
        var construction = state.Construction!;
        var pending = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        pending.Context["scopeInterface"] = PlanningCompactConstruction.ScopeInterface(state, candidate: false);
        // Capture the full view that was issued before this presentation change,
        // through the normal request checkpoint path, under its original policy.
        var guidance = pending.Request.Prompt[..(pending.Request.Prompt.LastIndexOf('\n') + 1)]
            .Replace("outputs are public /root results. scopeInterface shows accepted exports for its scope; basis:tentative_candidate also includes draft additions. Exports at /root are public; nested-scope exports are local. draft.outputs lists additions only; [] preserves accepted exports.",
                "outputs are public /root results; scopeInterface includes preserved local exports.", StringComparison.Ordinal);
        if (!historical) Assert.Contains("scopeInterface includes preserved local exports", guidance, StringComparison.Ordinal);
        var prompt = guidance + pending.Context.ToJsonString();
        construction.PendingReviewContext = pending.Context;
        construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            construction.Candidate!.Hash + "\n" + pending.Context.ToJsonString());
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.LocalPurpose, prompt, pending.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct,
            construction.Limits.ReviewOutputTokens));
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var context = saved.Construction!.PendingReviewContext!.ToJsonString();
        var hash = saved.Construction.PendingReviewHash;
        var calls = saved.ModelCalls;
        var accepted = PlanningConstruction.Hash(saved.Plan);
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(context, saved.Construction.PendingReviewContext!.ToJsonString());
            Assert.Equal(hash, saved.Construction.PendingReviewHash);
            Assert.All(Context(request)["scopeInterface"]!["outputs"]!.AsArray(), item => Assert.NotNull(item!["schema"]));
            Assert.Equal(accepted, PlanningConstruction.Hash(saved.Plan));
            return Review(request);
        } };
        state = await Advance(saved, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Single(recovery.Calls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Single(state.Construction!.Checkpoints);
    }

    [Fact]
    public async Task AnActuallyConsumedIndivisibleObjectIsStillExactAndCannotBypassAdmission()
    {
        var state = await Ready(30000, wholeObject: true);
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Equal("object", Assert.Single(context["interfaces"]!.AsArray())!["schema"]!["type"]!.ToString());
        Assert.Equal(30000, context["interfaces"]![0]!["schema"]!["properties"]!["unused"]!["enum"]![0]!.ToString().Length);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("Oversized review must not dispatch.") };
        var reviewed = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(reviewed.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(reviewed.Plan));
        Assert.Empty(reviewed.Construction!.Checkpoints);
    }

    private static async Task<PlanningSession> Ready(int length, bool wholeObject = false, int presentation = 4)
    {
        const string requirement = "Extract the supplied content, format and error without changing either accepted record export.";
        var state = new PlanningSession { Request = new() { TenantId = "accepted-export-navigation", Prompt = requirement } };
        PlanningSessionCreation.Capture(state);
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        var compact = construction.Compact!;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in compact.Sources) source.Indexed = true;
        compact.RequirementSources["req_0"] = [compact.Sources[0].Id];
        var input = new TaskInput { Name = "record", Type = new() { Kind = "object", Fields =
        [
            new() { Name = "content", Type = new() { Kind = "string" } },
            new() { Name = "format", Type = new() { Kind = "string" } },
            new() { Name = "error", Type = new() { Kind = "string", Nullable = true } },
            new() { Name = "unused", Type = new() { Kind = "string", Enum = [new string('z', length)] } }
        ] } };
        state.Requirements = new() { Outcomes = [new("req_0", requirement)], Inputs = [input], Outputs = [] };
        construction.Requirements = [new() { Id = "req_0" }];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Inputs = [input], Root = new() { Tasks = [new()
        {
            Id = "container", Kind = "sequence", Objective = "Process the supplied records.", Body = new()
            {
                Tasks = [Producer("first"), Producer("second")],
                Outputs = [new("first_record", Record("first")), new("second_record", Record("second"))]
            }
        }] } };
        construction.OpenScopes = ["/root", Scope];
        compact.CurrentStep = new() { Id = "selected", Scope = Scope, Kind = "transform", RequirementIds = ["req_0"],
            Objective = "Extract the supplied content, format and error.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true,
            Inputs = wholeObject ? [Record("first")] : [Field("content"), Field("format"), Field("error")] };
        var task = new PlanTask { Id = "consumer", Kind = "transform", Mode = "extract", Objective = compact.CurrentStep.Objective,
            Inputs = wholeObject ? [new("record", Record("first"))] :
                [new("content", Field("content")), new("format", Field("format")), new("error", Field("error"))],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "content", Type = new() { Kind = "string" } }] } };
        var candidate = PlanningConstruction.Apply(state, new() { Scope = Scope, Tasks = [task], RequirementIds = ["req_0"] });
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        construction.Candidate = candidate;
        return state;
    }

    private static void AssertNavigation(JsonObject original, JsonObject view)
    {
        foreach (var property in original.Where(property => property.Key != "outputs"))
            Assert.True(JsonNode.DeepEquals(property.Value, view[property.Key]), property.Key);
        Assert.Equal(original["outputs"]!.AsArray().Count, view["outputs"]!.AsArray().Count);
        for (var index = 0; index < original["outputs"]!.AsArray().Count; index++)
        {
            Assert.True(JsonNode.DeepEquals(original["outputs"]![index]!["name"], view["outputs"]![index]!["name"]));
            Assert.True(JsonNode.DeepEquals(original["outputs"]![index]!["value"], view["outputs"]![index]!["value"]));
            Assert.Null(view["outputs"]![index]!["schema"]);
        }
        Assert.StartsWith("scope-exports:accepted:", view["evidence"]!.ToString(), StringComparison.Ordinal);
    }
    private static void Historical(PlanningSession state)
    {
        state.Construction!.Compact!.PresentationVersion = 3;
        state.Request.Options["construction_presentation_version"] = 3;
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction!.Limits.PhasePolicy = null;
    }
    private static PlanTask Producer(string id) => new() { Id = id, Kind = "value", Objective = "Preserve the supplied record.",
        Outputs = [new("record", new() { Kind = "input", Source = "record" })] };
    private static TaskValue Record(string id) => new() { Kind = "output", Source = id, Port = "record" };
    private static TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [Record("first")] };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Review(LLMRequest request, JsonArray? evidence = null)
    {
        var response = new JsonObject
        {
            ["findings"] = evidence is null ? new JsonArray() : null, ["evidenceRequests"] = evidence,
            ["uncertainty"] = "Execution and business outcomes remain unverified."
        };
        if (request.StructuredOutputSchema!["properties"]!.AsObject().ContainsKey("inconclusive")) response["inconclusive"] = null;
        return new() { Json = response };
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
