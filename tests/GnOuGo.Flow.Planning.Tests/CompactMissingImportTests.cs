using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactMissingImportTests
{
    private const string Scope = "/groups/calculation/body";

    [Fact]
    public async Task GroupParameterUsedByAnExportIsDerivedWithoutASelectionHint()
    {
        var state = await Ready(); var accepted = Accepted(state);
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.Inputs);
        var candidate = PlanningConstruction.Apply(state, Extension());
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        state.Construction.Candidate = candidate;
        var imported = Assert.Single(PlanningCompactConstruction.ActualImportedInterfaces(state))!;
        Assert.Equal("parameter", imported["binding"]!["source"]!.ToString());
        Assert.Equal("number", imported["schema"]!["type"]!.ToString());
        Assert.Equal(Scope, imported["scope"]!.ToString());
        Assert.Equal(accepted, Accepted(state));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("input", "caller", null)]
    [InlineData("output", "retained", "number")]
    [InlineData("item", null, null)]
    [InlineData("index", null, null)]
    public async Task PreflightRejectsInaccessibleRootOrIterationBindingsInsideAnIsolatedGroup(string kind, string? source, string? port)
    {
        var state = await Ready(); var accepted = Accepted(state);
        state.Construction!.Compact!.CurrentStep!.Kind = "value";
        var candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = Scope, RequirementIds = ["req_0"], Tasks = [new()
            {
                Id = "consumer", Kind = "value", Objective = "Retain the exact selected numeric value.",
                Outputs = [new("result", new() { Kind = "object", Members = [new("selected", new() { Kind = kind, Source = source, Port = port })] })]
            }]
        });
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        var invalid = TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal));
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Required && diagnostic.Location.StartsWith("/tasks/consumer", StringComparison.Ordinal));
        Assert.Equal(accepted, Accepted(state));
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task IssuedDraftRecoveryKeepsExactRequestAndDerivesItsActualImportWithoutAnotherCharge()
    {
        var state = await Ready(); var accepted = Accepted(state);
        var raw = PlanningConstruction.DraftRequest(state, repair: false);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, raw.Prompt, raw.StructuredOutputSchema!.AsObject(), null, null, out var mapping);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.DraftPurpose, Request = issued, EvidenceAddresses = mapping };
        state.Construction!.IssuedBaseHash = PlanningConstruction.Hash(state.Plan); state.ModelCalls++;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest); var calls = state.ModelCalls;
        var receipt = Wire(issued, new() { ["baseHash"] = PlanningConstruction.Hash(state.Plan),
            ["extension"] = JsonSerializer.SerializeToNode(Extension(), PlanningJsonContext.Default.PlanningConstructionExtension) });
        var runtime = new TestRuntime { Respond = (request, _) =>
        { Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)); return receipt; } };
        runtime.CaptureIssued(state.PendingCall!);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(runtime.Calls); Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.PendingCall);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Equal("parameter", Assert.Single(PlanningCompactConstruction.ActualImportedInterfaces(state))!["binding"]!["source"]!.ToString());
        Assert.Equal(accepted, Accepted(state));
    }

    [Fact]
    public async Task SelectionKeepsGroupAndCallerDeclarationsDistinctWithinItsCapturedCompleteCeiling()
    {
        var state = await Ready(); state.Construction!.Compact!.CurrentStep = null;
        var request = Prepared(state);
        var ceiling = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, ceiling).EstimatedInputTokens, 1, ceiling);
        var context = Context(Canonical(state, request));
        if (!context["scopes"]!.AsArray().Any(row => row?["path"]?.ToString() == Scope))
        {
            var next = context["nextScopes"]!.ToString();
            PlanningConstruction.LoadEvidence(state, [next], followup: Prepared, purpose: PlanningCompactConstruction.SelectPurpose);
            request = Prepared(state);
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, ceiling).EstimatedInputTokens, 1, ceiling);
            context = Context(Canonical(state, request));
        }
        var group = Assert.Single(context["scopes"]!.AsArray(), row => row?["path"]?.ToString() == Scope)!;
        Assert.Equal("parameter", group["inputs"]![0]!["input"]!.ToString());
        Assert.Equal("number", group["inputs"]![0]!["typeKind"]!.ToString());
        Assert.Equal("/root", Assert.Single(context["interfaces"]!.AsArray(), row => row?["input"]?.ToString() == "caller")!["scope"]!.ToString());

        LLMRequest Prepared(PlanningSession current)
        {
            var raw = PlanningCompactConstruction.Request(current, PlanningCompactConstruction.SelectGuidance(current),
                PlanningCompactConstruction.SelectionContext(current), PlanningCompactConstruction.SelectionSchema(current), PlanningCompactConstruction.SelectPurpose);
            return PlanningModelCalls.PrepareRequest(current, PlanningCompactConstruction.SelectPurpose, raw.Prompt, raw.StructuredOutputSchema!.AsObject());
        }
        LLMRequest Canonical(PlanningSession current, LLMRequest prepared)
        {
            var raw = PlanningCompactConstruction.Request(current, PlanningCompactConstruction.SelectGuidance(current),
                PlanningCompactConstruction.SelectionContext(current), PlanningCompactConstruction.SelectionSchema(current), PlanningCompactConstruction.SelectPurpose);
            var exact = PlanningModelCalls.PrepareRequest(current, PlanningCompactConstruction.SelectPurpose, raw.Prompt,
                raw.StructuredOutputSchema!.AsObject(), null, null, out var exactMap);
            Assert.Equal(JsonSerializer.Serialize(prepared, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(exact, PlanningJsonContext.Default.LLMRequest));
            return PlanningModelCalls.CanonicalEvidenceRequest(new() { Id = exact.ClientRequestId!, Request = exact, EvidenceAddresses = exactMap });
        }
    }

    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Return the transformed numeric parameter through the reusable group.";
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(PlannerFixture.Capture(new() { Request = new() { TenantId = "actual-imports", Prompt = objective,
            Options = new() { ["automatic_correction"] = true } } }), runtime);
        Assert.Empty(state.Diagnostics); state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements!.Inputs = [new() { Name = "caller", Type = new() { Kind = "number" } }];
        state.Plan = new() { Inputs = state.Requirements.Inputs,
            Root = new() { Tasks = [new() { Id = "retained", Kind = "value", Objective = "Keep the accepted value.", Outputs = [new("number", new() { Kind = "number", Number = 7 })] }] },
            Groups = [new() { Id = "calculation", Inputs = [new() { Name = "parameter", Type = new() { Kind = "number" } }] }] };
        state.Construction!.OpenScopes = ["/root", Scope];
        state.Construction.Compact!.CurrentStep = new() { Id = "export", BaseHash = PlanningConstruction.Hash(state.Plan), Kind = "close", Scope = Scope,
            Objective = "Export the transformed parameter.", RequirementIds = ["req_0"], DiscoveryComplete = true };
        return state;
    }
    private static PlanningConstructionExtension Extension() => new() { Scope = Scope, RequirementIds = ["req_0"], CloseScopes = [Scope],
        Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "parameter" }, new() { Kind = "number", Number = 3 }] })] };
    private static string Accepted(PlanningSession state) => new JsonObject
    {
        ["plan"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan),
        ["requirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["checkpoints"] = JsonSerializer.SerializeToNode(state.Construction!.Checkpoints, PlanningJsonContext.Default.ListPlanningConstructionCheckpoint)
    }.ToJsonString();
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new() { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
