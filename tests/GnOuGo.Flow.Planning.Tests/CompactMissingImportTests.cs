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
    public async Task AnExportMustSelectItsGroupParameterBeforeTheExistingReselectionCanRepairIt()
    {
        var state = await Ready();
        var accepted = Accepted(state);
        var calls = state.ModelCalls;
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Draft(request)) };
        state = await Advance(state, runtime);
        AssertMissingParameter(Assert.Single(state.Diagnostics));
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(accepted, Accepted(state));
        Assert.Equal(calls + 1, state.ModelCalls);

        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            var diagnostic = Assert.Single(Context(request)["diagnostics"]!.AsArray())!;
            Assert.Contains("\"source\":\"parameter\"", diagnostic["message"]!.ToString(), StringComparison.Ordinal);
            Assert.Equal("/extension/outputs/0/value/items/0", diagnostic["locations"]![0]!.ToString());
            Assert.Contains("reselectStep alone", diagnostic["message"]!.ToString(), StringComparison.Ordinal);
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 8000).EstimatedInputTokens, 1, 8000);
            return Wire(request, new()
            {
                ["baseHash"] = Context(request)["baseHash"]!.DeepClone(),
                ["reselectStep"] = "Include the declared numeric group parameter as an exact typed import for the export."
            });
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Equal(calls + 2, state.ModelCalls);
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(Scope, state.Construction.Compact.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction.Compact.ReplacementRequirements);
        Assert.Single(state.Construction.Compact.RejectedSteps);
        Assert.Equal(accepted, Accepted(state));

        // The same typed import makes the unchanged export admissible; no
        // automatic import or mutation of the accepted group is permitted.
        var replacement = Step(state);
        replacement.Inputs = [Parameter()];
        PlanningCompactConstruction.ValidateSelection(state, replacement);
        state.Construction.Compact.CurrentStep = replacement;
        var imported = Assert.Single(PlanningCompactConstruction.ImportedInterfaces(state, replacement));
        Assert.Equal("number", imported!["schema"]!["type"]!.ToString());
        var candidate = PlanningConstruction.Apply(state, Extension());
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal(accepted, Accepted(state));
    }

    [Theory]
    [InlineData("output", "retained", "number", "/tasks/consumer/outputs/result/members/0/value")]
    [InlineData("item", null, null, "/tasks/consumer/outputs/result/members/0/value")]
    [InlineData("index", null, null, "/tasks/consumer/outputs/result/members/0/value")]
    public async Task MissingNestedBindingsIdentifyOnlyTheBindingAndItsConsumer(
        string kind, string? source, string? port, string location)
    {
        var state = await Ready();
        state.Construction!.Compact!.CurrentStep = Step(state, "value");
        var binding = new TaskValue { Kind = kind, Source = source, Port = port };
        var candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = Scope, RequirementIds = ["req_0"],
            Tasks = [new() { Id = "consumer", Kind = "value", Objective = "Assemble an exact result.", Outputs =
                [new("result", new() { Kind = "object", Members = [new("selected", binding),
                    new("unrelated", new() { Kind = "string", Text = "Private unrelated literal must not enter the diagnostic." })] })] }]
        });
        var accepted = Accepted(state);
        var diagnostic = Assert.Single(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, candidate)).Diagnostics);
        Assert.Equal(location, diagnostic.Location);
        Assert.Contains("\"kind\":\"" + kind + "\"", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(Scope, diagnostic.Message, StringComparison.Ordinal);
        if (source is not null) Assert.Contains("\"source\":\"" + source + "\"", diagnostic.Message, StringComparison.Ordinal);
        if (port is not null) Assert.Contains("\"port\":\"" + port + "\"", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private unrelated literal", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(accepted, Accepted(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedDraftKeepsItsExactRequestAndAccounting(bool historicalProfile)
    {
        var state = await Ready();
        if (historicalProfile)
        {
            state.Construction!.Compact!.PresentationVersion = null;
            state.Construction.Compact.InterfaceExtractions = null;
            state.Request.Options.Remove("construction_presentation_version");
        }
        var issued = PlanningGenerationPolicy.Apply(PlanningConstruction.DraftRequest(state, repair: false), state.Request.Generation);
        issued.Prompt = "Retained draft instructions.\n" + Context(issued).ToJsonString();
        issued.ClientRequestId = "retained-import-draft";
        state.PendingCall = new() { Id = issued.ClientRequestId, Purpose = PlanningConstruction.DraftPurpose, Request = issued };
        state.Construction!.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var accepted = Accepted(state);
        var calls = state.ModelCalls;
        var receipt = Wire(issued, Draft(issued));
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Single(runtime.Calls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Null(state.PendingCall);
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(accepted, Accepted(state));
        var diagnostic = Assert.Single(state.Diagnostics);
        if (historicalProfile)
        {
            Assert.Equal("/extension", diagnostic.Location);
            Assert.Equal("An external binding was not selected as an exact typed import; reselect the tentative step with the required input.", diagnostic.Message);
        }
        else AssertMissingParameter(diagnostic);
    }

    [Fact]
    public async Task SelectionWithExactGroupAndCallerInterfacesStillFitsItsCompleteEnvelope()
    {
        var state = await Ready();
        state.Construction!.Compact!.CurrentStep = null;
        var request = PlanningGenerationPolicy.Apply(PlanningCompactConstruction.Request(
            PlanningCompactConstruction.SelectGuidance(state), PlanningCompactConstruction.SelectionContext(state),
            PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose), state.Request.Generation);
        request.Provider = "OpenAi"; request.Model = "retained-medium-model";
        request.UseBackgroundMode = true; request.StructuredOutputStrict = true;
        request.MaxTokens = 8192; request.ClientRequestId = new string('s', 64) + ":9:" + new string('h', 64);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
        var scope = Context(request)["scopes"]!.AsArray().Single(item => item!["path"]!.ToString() == Scope)!;
        Assert.Equal("parameter", scope["inputs"]![0]!["input"]!.ToString());
        Assert.Equal("number", scope["inputs"]![0]!["typeKind"]!.ToString());
        Assert.Equal("/root", Context(request)["interfaces"]!.AsArray().Single(item => item!["input"]?.ToString() == "caller")!["scope"]!.ToString());
    }

    private static void AssertMissingParameter(PlanningDiagnostic diagnostic)
    {
        Assert.Equal("CONSTRUCTION_STEP_INPUT_INVALID", diagnostic.Code);
        Assert.Equal("/extension/outputs/0/value/items/0", diagnostic.Location);
        Assert.Contains("{\"kind\":\"input\",\"source\":\"parameter\"}", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(Scope, diagnostic.Message, StringComparison.Ordinal);
    }

    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Return the transformed numeric parameter through the reusable group.";
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "missing-imports", Prompt = objective,
            Options = new() { ["automatic_correction"] = true } } }, version: 4), runtime);
        Assert.Empty(state.Diagnostics);
        // This corpus preserves the retained strict-import protocol. Fresh
        // semantic imports are exercised by CompactActualImportReviewTests.
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction!.Limits.PhasePolicy = null;
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements!.Inputs = [new() { Name = "caller", Type = new() { Kind = "number" } }];
        state.Plan = new()
        {
            Inputs = state.Requirements.Inputs,
            Root = new() { Tasks = [new() { Id = "retained", Kind = "value", Objective = "Keep the accepted value.",
                Outputs = [new("number", new() { Kind = "number", Number = 7 })] }] },
            Groups = [new() { Id = "calculation", Inputs = [new() { Name = "parameter", Type = new() { Kind = "number" } }] }]
        };
        state.Construction!.OpenScopes = ["/root", Scope];
        state.Construction.Compact!.CurrentStep = Step(state);
        return state;
    }

    private static PlanningConstructionStep Step(PlanningSession state, string kind = "close") => new()
    {
        Id = "export", BaseHash = PlanningConstruction.Hash(state.Plan), Kind = kind, Scope = Scope,
        Objective = "Export the transformed parameter.", RequirementIds = ["req_0"], DiscoveryComplete = true
    };
    private static TaskValue Parameter() => new() { Kind = "input", Source = "parameter" };
    private static PlanningConstructionExtension Extension() => new()
    {
        Scope = Scope, RequirementIds = ["req_0"], CloseScopes = [Scope],
        Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply", Items = [Parameter(), new() { Kind = "number", Number = 3 }] })]
    };
    private static JsonObject Draft(LLMRequest request) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.DeepClone(),
        ["extension"] = JsonSerializer.SerializeToNode(Extension(), PlanningJsonContext.Default.PlanningConstructionExtension)
    };
    private static string Accepted(PlanningSession state) => new JsonObject
    {
        ["plan"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan),
        ["requirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["checkpoints"] = JsonSerializer.SerializeToNode(state.Construction!.Checkpoints, PlanningJsonContext.Default.ListPlanningConstructionCheckpoint)
    }.ToJsonString();
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
