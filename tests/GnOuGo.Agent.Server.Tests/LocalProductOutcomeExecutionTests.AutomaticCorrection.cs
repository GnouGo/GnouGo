using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task AutomaticCorrectionReplaysBothRetainedMalformedGuardsWithoutChangingBusinessWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new GnOuGo.Document.Mcp.DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, ct); catalog.Policy.RequireExternalConfirmation = false;
        var retained = GuardRetained(catalog); var corrected = GuardRetained(catalog);
        foreach (var task in RetainedTasks(corrected.Plan!.Root).Where(t => t.Id is "extract_choices" or "extract_record"))
            task.Requires!.Items[1].Port = "available";
        var result = await AutomaticCompositionSession(catalog, runtime, corrected, "retained_guard", ct, retained);
        Assert.Equal(2, result.Request.Options["automatic_correction_progress"]!["locations"]!.AsArray().Count);
        Assert.Equal(0, factory.InvocationAttempts);
        // This corrects the diagnosed references, not the retained plan's undiagnosed
        // business omissions. It must still undergo requirement/artifact review.
        Assert.Null(result.ApprovedHash);
    }

    [Theory]
    [InlineData("guard", "complete")]
    [InlineData("guard", "missing")]
    [InlineData("guard", "incomplete")]
    [InlineData("choice", "repeated")]
    [InlineData("choice", "unknown")]
    [InlineData("choice", "denied")]
    [InlineData("requirements", "complete")]
    [InlineData("requirements", "repeated")]
    public Task AutomaticCorrectionProducesIndependentlyVerifiedLocalWorkbook(string defect, string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", automaticCorrection: defect);

    private static async Task<PlanningSession> AutomaticCompositionSession(PlanningCatalog catalog, WorkflowPlanningRuntime runtime,
        PlanningProposal corrected, string defect, CancellationToken ct, PlanningProposal? retained = null)
    {
        if (defect == "commitment") return await CommitmentCompositionSession(catalog, runtime, corrected, ct);
        var broken = JsonSerializer.SerializeToNode(retained ?? corrected, PlanningJsonContext.Default.PlanningProposal)!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        if (defect == "requirements") broken.Requirements = null;
        else if (retained is null && defect == "choice") broken.Plan!.Root.Outputs[0].Value.Kind = "choice";
        else if (retained is null)
        {
            var guard = RetainedTasks(broken.Plan!.Root).Select(t => t.Requires).OfType<TaskValue>()
                .SelectMany(TaskPlanCompiler.Values).First(v => v.Kind == "output" && v.Port is not null);
            guard.Port += "},{";
        }
        var findings = new TaskPlanCompiler().Compile(broken.Plan!, catalog).Diagnostics;
        if (defect == "requirements") Assert.Empty(findings); // Valid bindings cannot replace missing business intent.
        else Assert.Contains(findings, d => d.Code == (retained is not null ? "TASK_FIELD_UNKNOWN" : defect == "choice" ? "CHOICE_UNKNOWN" : "TASK_OUTPUT_UNKNOWN"));
        var original = JsonSerializer.Serialize(broken, PlanningJsonContext.Default.PlanningProposal);
        var adapter = new ProposalRuntime(runtime, corrected);
        adapter.Patch = request =>
        {
            if (adapter.Requests.Count == 1) return JsonSerializer.SerializeToNode(broken, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
            if (defect == "requirements")
            {
                Assert.False(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
                Assert.Contains("REQUIREMENTS_INVALID", request.Prompt);
                return JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
            }
            Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            var context = PlanningRepairPatch.RequestContext(request)["repair"]!;
            var index = PlanningRepairPatch.Index(JsonSerializer.SerializeToNode(corrected.Plan, PlanningJsonContext.Default.TaskPlan)!);
            var edits = new JsonArray();
            foreach (var slot in context["slots"]!.AsArray())
            {
                var site = index[slot!["location"]!.ToString()].Node!;
                if (site is JsonObject obj && obj.ContainsKey("name") && obj.ContainsKey("value")) site = obj["value"]!;
                edits.Add((JsonNode)new JsonObject { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = site.DeepClone() });
            }
            return new() { ["discoveryRequests"] = null, ["clarifications"] = null, ["patch"] = new JsonObject { ["edits"] = edits } };
        };
        var session = new PlanningSession { Catalog = catalog, Request = new()
        {
            TenantId = "local", Prompt = "Preserve the requested exact observed data, visits, workbook and cleanup.",
            Options = new() { ["automatic_correction"] = true }, Policy = new() { RequireExternalConfirmation = false },
            Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 }
        } };
        var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 4 && !PlanningStatus.IsWaiting(session.Status) && !PlanningStatus.IsTerminal(session.Status); i++)
            session = await planner.AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, adapter, ct);
        Assert.True(session.Status == PlanningStatus.FinalReview, string.Join(';', session.Diagnostics));
        Assert.Equal(2, adapter.Requests.Count); Assert.Equal(1, session.ReplanAttempts); Assert.Null(session.ApprovedHash);
        Assert.Equal(original, JsonSerializer.Serialize(broken, PlanningJsonContext.Default.PlanningProposal));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(corrected.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.SerializeToNode(session.Plan, PlanningJsonContext.Default.TaskPlan)));
        if (Environment.GetEnvironmentVariable("GNOU_GO_AUTOCORRECTION_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, defect + "-before.json"), original, ct);
            await File.WriteAllTextAsync(Path.Combine(folder, defect + "-after.json"), JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan), ct);
            await File.WriteAllTextAsync(Path.Combine(folder, defect + "-after.yaml"), session.Yaml, ct);
            var progress = session.Request.Options["automatic_correction_progress"]!.DeepClone().AsObject();
            progress["calls"] = session.ModelCalls; progress["repairs"] = session.ReplanAttempts;
            progress["status"] = session.Status;
            progress["requestEstimatedTokens"] = new JsonArray(adapter.Requests.Select(r => (JsonNode?)JsonValue.Create(
                PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()))).ToArray());
            await File.WriteAllTextAsync(Path.Combine(folder, defect + "-correction.json"), progress.ToJsonString(), ct);
        }
        return session;
    }
}
