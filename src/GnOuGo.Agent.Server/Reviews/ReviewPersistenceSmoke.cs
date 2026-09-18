using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Reviews;

internal static class ReviewPersistenceSmoke
{
    internal static async Task RunAsync(IKeyVaultRecordStore records)
    {
        const string run = "published-review-smoke";
        ReviewPublicationService Service(string tenant) => new(records, new AgentHumanInputProvider(), Options.Create(new ReviewPublicationSettings()), Options.Create(new OpenTelemetrySettings { TenantId = tenant }));
        var service = Service("smoke");
        var integrations = new InMemoryMcpClientFactory(); integrations.RegisterServer("github", new());
        await using var factory = new ReviewMcpClientFactory(integrations, service);
        await using var tools = await factory.GetClientAsync(ReviewMcpClientFactory.Server, CancellationToken.None);
        foreach (var tool in await tools.ListToolsAsync(CancellationToken.None))
            if (GnOuGo.Flow.Core.Planning.PlanningContractValidation.ValidateSchema(tool.InputSchema!.AsObject()).Count != 0 ||
                GnOuGo.Flow.Core.Planning.PlanningContractValidation.ValidateSchema(tool.OutputSchema!.AsObject()).Count != 0)
                throw new InvalidOperationException("Published review tool schemas are invalid.");
        var request = new ReviewDraftRequest("example", "repository", 1, new(
            new(new string('a', 40), new string('b', 40), [], new(1, 1, 0, 0, [], []), [], "Private published review smoke") { Complete = true },
            Path.GetFullPath("smoke-workspace"), [new("Review source", false)], [new("Review source", ReviewCheckStatus.Passed, "Private published review smoke evidence", false)]));
        await service.CaptureAsync(run, "copilot_review", JsonSerializer.SerializeToNode(request.Evaluation.Review, CopilotCoreJsonContext.Default.CopilotReviewResult), CancellationToken.None);
        var draft = await service.EvaluateAsync(run, request, CancellationToken.None);
        if (draft.Evaluation.SubmitEvent != ReviewSubmitEvent.Approve) throw new InvalidOperationException("Published review evaluation failed.");
        var reopened = Service("smoke");
        if ((await reopened.EvaluateAsync(run, request, CancellationToken.None)).DraftId != draft.DraftId)
            throw new InvalidOperationException("Published review persistence failed.");
        try { await Service("another-tenant").EvaluateAsync(run, request, CancellationToken.None); throw new InvalidDataException("Review tenant isolation failed."); }
        catch (InvalidOperationException) { }
        var stored = await records.GetAsync(ReviewPublicationService.Drafts, "smoke", draft.DraftId, "smoke", CancellationToken.None);
        var state = JsonSerializer.Deserialize(stored!.Value, ReviewPublicationJsonContext.Default.StoredReviewDraft)!;
        await records.UpsertAsync(ReviewPublicationService.Drafts, "smoke", draft.DraftId,
            JsonSerializer.Serialize(state with { Status = "dispatching" }, ReviewPublicationJsonContext.Default.StoredReviewDraft), "smoke", CancellationToken.None);
        // A restart after dispatch must return the uncertain receipt before touching transport or confirmation.
        var replay = await Service("smoke").PublishAsync(run, "publish", draft.DraftId, null!, CancellationToken.None);
        if (replay.Status != "dispatching") throw new InvalidOperationException("Published review replay failed.");
        Console.WriteLine("Encrypted review draft, tenant isolation and uncertain-publication replay smoke passed.");
    }
}
