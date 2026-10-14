using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("guard", "complete")]
    [InlineData("guard", "missing")]
    [InlineData("guard", "incomplete")]
    [InlineData("choice", "repeated")]
    [InlineData("choice", "unknown")]
    [InlineData("choice", "denied")]
    public Task AutomaticCorrectionProducesIndependentlyVerifiedLocalWorkbook(string defect, string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", automaticCorrection: defect);

    private static async Task<PlanningSession> AutomaticCompositionSession(PlanningCatalog catalog, WorkflowPlanningRuntime runtime,
        PlanningProposal corrected, string defect, CancellationToken ct)
    {
        var broken = JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.PlanningProposal)!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        if (defect == "choice") broken.Plan!.Root.Outputs[0].Value.Kind = "choice";
        else
        {
            var guard = RetainedTasks(broken.Plan!.Root).Select(t => t.Requires).OfType<TaskValue>()
                .SelectMany(TaskPlanCompiler.Values).First(v => v.Kind == "output" && v.Port is not null);
            guard.Port += "},{";
        }
        var findings = new TaskPlanCompiler().Compile(broken.Plan!, catalog).Diagnostics;
        Assert.Contains(findings, d => d.Code == (defect == "choice" ? "CHOICE_UNKNOWN" : "TASK_OUTPUT_UNKNOWN"));
        var original = JsonSerializer.Serialize(broken, PlanningJsonContext.Default.PlanningProposal);
        var adapter = new ProposalRuntime(runtime, corrected);
        adapter.Patch = request =>
        {
            if (adapter.Requests.Count == 1) return JsonSerializer.SerializeToNode(broken, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
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
