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
    public Task StaticCommittedWriterProducesAnIndependentlyInspectedWorkbook(string variant) =>
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
        TaskPlanFixture correct, CancellationToken ct)
    {
        // Explicit reviewed fixture obligations, specified from producer contracts before assessing either plan.
        string Operation(string method) => TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id;
        correct.Requirements!.Outcomes = correct.Requirements.Outcomes.Select(r => r with { Commitments = r.Id switch
        {
            "search_amazon_fr" or "extract_details" => [new(Operation("browser_get_content"))],
            "save_excel" => [new(Operation("document_write")) { Result = new("filePath", "productsFile") }],
            "cleanup_browser" => [new(Operation("browser_close"))], _ => null
        } }).ToList();
        var broken = JsonSerializer.SerializeToNode(correct, PlanningFixtureJsonContext.Default.TaskPlanFixture)!.Deserialize(PlanningFixtureJsonContext.Default.TaskPlanFixture)!;
        var writer = RetainedTasks(broken.Plan!.Root).Single(t => t.Id == "save_products_xlsx");
        writer.Kind = "value"; writer.Operation = null; writer.Inputs = []; writer.Outputs = [new("filePath", new TaskValue { Kind = "string", Text = "fabricated.xlsx" })];
        Assert.Empty(new TaskPlanCompiler().Compile(broken.Plan, catalog, new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v15" } }).Diagnostics);
        var badState = new PlanningSession { Plan = broken.Plan, Requirements = broken.Requirements, Catalog = catalog,
            Request = new() { TenantId = "local", Prompt = correct.Requirements.Summary } };
        var before = PlanningCommitments.Assess(badState);
        Assert.Contains(before, r => r.Diagnostics.Any(d => d.Code == PlanningCommitments.MissingOperation));
        Assert.Contains(before, r => r.Diagnostics.Any(d => d.Code == PlanningCommitments.DisconnectedResult));
        await Task.CompletedTask; ct.ThrowIfCancellationRequested();
        var state = RecordedPlanCompilation.CompileStatic(correct, catalog, new()
        { TenantId = "local", Prompt = correct.Requirements.Summary, Policy = new() { RequireExternalConfirmation = false },
            Options = new() { ["compilation_profile"] = "compact-bindings-v15", ["mapping_profile"] = "adaptive-each-v1" } });
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.DoesNotContain(PlanningCommitments.Assess(state), r => r.Outcome == "violation");
        Assert.Equal(0, state.ModelCalls); Assert.Null(state.ApprovedHash);
        return state;

    }
}
