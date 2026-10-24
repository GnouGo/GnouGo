using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("repeated")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    [InlineData("redirected")]
    public Task CommittedWriterCorrectionProducesAnIndependentlyInspectedWorkbook(string variant) =>
        ExecuteTypedComposition(variant, "compact-bindings-v15", automaticCorrection: "commitment");

    private static void SaveCommitmentOutcome(string variant, bool success, List<string> visits, List<LLMRequest> requests, string[][]? cells, bool cleanup, bool? liveOracle)
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_COMMITMENT_EVIDENCE") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "product-execution-" + variant + ".json"), new JsonObject
        {
            ["success"] = success, ["localVisits"] = JsonSerializer.SerializeToNode(visits), ["cells"] = JsonSerializer.SerializeToNode(cells),
            ["cleanupVerified"] = cleanup, ["exactFixtureOracle"] = true, ["unchangedLiveOracle"] = liveOracle,
            ["deterministicRuntimeCalls"] = requests.Count, ["providerCalls"] = 0, ["paidCost"] = 0,
            ["requestBytes"] = new JsonArray(requests.Select(r => (JsonNode?)JsonValue.Create(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)))).ToArray())
        }.ToJsonString(new() { WriteIndented = true }));
    }

    private static async Task<PlanningSession> CommitmentCompositionSession(PlanningCatalog catalog, WorkflowPlanningRuntime runtime,
        PlanningProposal correct, CancellationToken ct)
    {
        // Explicit reviewed fixture obligations, specified from producer contracts before assessing either plan.
        string Operation(string method) => TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id;
        correct.Requirements!.Outcomes = correct.Requirements.Outcomes.Select(r => r with { Commitments = r.Id switch
        {
            "search_amazon_fr" or "extract_details" => [new(Operation("browser_get_content"))],
            "save_excel" => [new(Operation("document_write")) { Result = new("filePath", "productsFile") }],
            "cleanup_browser" => [new(Operation("browser_close"))], _ => null
        } }).ToList();
        var broken = JsonSerializer.SerializeToNode(correct, PlanningJsonContext.Default.PlanningProposal)!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        var writer = RetainedTasks(broken.Plan!.Root).Single(t => t.Id == "save_products_xlsx");
        writer.Kind = "value"; writer.Operation = null; writer.Inputs = []; writer.Outputs = [new("filePath", new TaskValue { Kind = "string", Text = "fabricated.xlsx" })];
        Assert.Empty(new TaskPlanCompiler().Compile(broken.Plan, catalog, new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v15" } }).Diagnostics);
        var state = PlanningCorpus.RetainedSession(new PlanningSession { IntentVersion = 3, Plan = broken.Plan, Requirements = broken.Requirements, Catalog = catalog, Request = new()
        {
            TenantId = "local", Prompt = "Perform the accepted observation, visits, exact workbook writing and cleanup. Preserve every existing acquisition and action.",
            Policy = new() { RequireExternalConfirmation = false }, Options = new() { ["automatic_correction"] = true, ["compilation_profile"] = "compact-bindings-v15" },
            Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 }
        } });
        await CommitmentEvidence.WriteCompilingBaseline(state, runtime, "product", ct);
        var before = PlanningCommitments.Assess(state);
        Assert.Contains(before, r => r.Diagnostics.Any(d => d.Code == PlanningCommitments.MissingOperation));
        Assert.Contains(before, r => r.Diagnostics.Any(d => d.Code == PlanningCommitments.DisconnectedResult));
        state.Plan = null; state.Requirements = null;
        var adapter = new ProposalRuntime(runtime, correct);
        adapter.Patch = request => JsonSerializer.SerializeToNode(adapter.Requests.Count == 1 ? broken : correct, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
        var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 4 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, adapter, ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.ValidationResults, r => r.Id == "coverage:save_excel:0:result" && r.Outcome == "verified");
        Assert.Contains(state.ValidationResults, r => r.Id == "coverage:extract_details:0:operation" && r.Outcome == "unverified");
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(correct.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        if (Environment.GetEnvironmentVariable("GNOU_GO_COMMITMENT_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "product-before.json"), JsonSerializer.Serialize(broken, PlanningJsonContext.Default.PlanningProposal), ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "product-after.json"), JsonSerializer.Serialize(correct, PlanningJsonContext.Default.PlanningProposal), ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "product-after.yaml"), state.Yaml, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "product-coverage.json"), new JsonObject
            {
                ["before"] = JsonSerializer.SerializeToNode(before, PlanningJsonContext.Default.ListPlanningValidationResult),
                ["after"] = JsonSerializer.SerializeToNode(PlanningCommitments.Assess(state), PlanningJsonContext.Default.ListPlanningValidationResult),
                ["progress"] = state.Request.Options["automatic_correction_progress"]!.DeepClone(),
                ["deterministicCalls"] = state.ModelCalls, ["corrections"] = state.ReplanAttempts, ["paidCost"] = 0,
                ["estimatedCost"] = null, ["costBasis"] = "No provider pricing used; deterministic adapters only.",
                ["requests"] = new JsonArray(adapter.Requests.Select(r => (JsonNode?)new JsonObject
                { ["serializedBytes"] = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)),
                    ["estimatedInputTokens"] = PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()) }).ToArray())
            }.ToJsonString(new() { WriteIndented = true }), ct);
        }
        return state;
    }
}
