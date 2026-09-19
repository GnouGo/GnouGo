using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Reviews;

/// <summary>Owns original evidence, immutable review content, human confirmation and one publication attempt.</summary>
public sealed class ReviewPublicationService(
    IKeyVaultRecordStore records, AgentHumanInputProvider humanInput,
    IOptions<ReviewPublicationSettings> settings, IOptions<OpenTelemetrySettings> telemetry)
{
    internal const string Drafts = "agent-review-drafts-v1", Evidence = "agent-review-evidence-v1";
    private const string Author = "GnOuGo.Agent.Server.Reviews";
    internal ReviewPublicationSettings Settings => settings.Value;
    internal string Tenant => WorkflowExecutionTenant.Resolve(telemetry);
    internal IMcpClientFactory Decorate(IMcpClientFactory inner) => new ReviewMcpClientFactory(inner, this);

    internal async Task CaptureAsync(string runId, string tool, JsonNode? content, CancellationToken ct)
    {
        if (content is not JsonObject obj) return;
        if (tool is "copilot_review" or "copilot_review_finish")
            await SaveEvidence("review", JsonSerializer.SerializeToNode(JsonSerializer.Deserialize(obj, CopilotCoreJsonContext.Default.CopilotReviewResult), CopilotCoreJsonContext.Default.CopilotReviewResult)!, ct);
        if (tool is "copilot_one_shot" or "copilot_interactive_one_shot" or "copilot_session_send")
            foreach (var execution in obj["toolExecutions"] as JsonArray ?? [])
                if (execution is not null) await SaveEvidence("execution", JsonSerializer.SerializeToNode(JsonSerializer.Deserialize(execution, CopilotCoreJsonContext.Default.CopilotToolExecutionObservation), CopilotCoreJsonContext.Default.CopilotToolExecutionObservation)!, ct);

        Task<KeyVaultRecordValue> SaveEvidence(string kind, JsonNode value, CancellationToken token) => records.UpsertAsync(Evidence, Tenant,
            EvidenceKey(runId, kind, value), value.ToJsonString(), Author, token);
    }

    internal async Task<ReviewDraft> EvaluateAsync(string runId, ReviewDraftRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!SafeName(request.Owner) || !SafeName(request.Repository) || request.PullNumber <= 0 || request.Evaluation?.Review is not { } review ||
            review.HeadSha.Length is not (40 or 64) || !review.HeadSha.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact repository, pull-request number and reviewed head SHA are required.");
        await RequireEvidence("review", JsonSerializer.SerializeToNode(review, CopilotCoreJsonContext.Default.CopilotReviewResult)!, ct);
        foreach (var check in request.Evaluation.Checks)
            if (check.Execution is not null)
                await RequireEvidence("execution", JsonSerializer.SerializeToNode(check.Execution, CopilotCoreJsonContext.Default.CopilotToolExecutionObservation)!, ct);
        var evaluation = ReviewEvaluation.Evaluate(request.Evaluation);
        var identity = JsonSerializer.Serialize(request, ReviewPublicationJsonContext.Default.ReviewDraftRequest);
        var id = Hash(Tenant + "\n" + runId + "\n" + identity);
        var body = evaluation.Body + "\n<!-- gnougo-review:" + id + " -->\n";
        if (body.Length > 60_000) throw new ArgumentException("The complete review exceeds the publication size limit.");
        var draft = new ReviewDraft(id, request.Owner, request.Repository, request.PullNumber, review.HeadSha, evaluation with { Body = body });
        using var lease = Acquire(id);
        if (await LoadAsync(runId, id, ct) is { } existing) return existing.Draft;
        await SaveAsync(new(Tenant, runId, draft), ct);
        return draft;

        async Task RequireEvidence(string kind, JsonNode value, CancellationToken token)
        {
            var receipt = await records.GetAsync(Evidence, Tenant, EvidenceKey(runId, kind, value), Author, token);
            if (receipt is null || !JsonNode.DeepEquals(JsonNode.Parse(receipt.Value), value))
                throw new InvalidOperationException("Review evaluation requires an unchanged original " + kind + " result from this execution.");
        }
    }

    internal async Task<ReviewPublishResult> PublishAsync(string runId, string stepId, string id, IMcpSession github,
        CancellationToken ct, IHumanInputProvider? confirmation = null, McpCallExecutionContext? call = null)
    {
        ValidateId(id);
        using var lease = Acquire(id);
        var stored = await LoadAsync(runId, id, ct) ?? throw new InvalidOperationException("No review draft exists for this tenant and execution.");
        if (stored.Status != "prepared") return Result(stored.Status, stored.Status == "dispatching"
            ? "Publication completion is uncertain. No automatic retry will send another review." : "The stored publication outcome was replayed without another write.");
        var draft = stored.Draft;
        var prompt = new HumanInputRequest
        {
            RunId = call?.Correlation.RunId ?? runId, StepId = stepId, Mode = HumanInputContract.ModeConfirm, Choices = ["approve", "reject"], AllowAbandon = true,
            Prompt = "Publish this exact review to " + draft.Owner + "/" + draft.Repository + " pull request #" + draft.PullNumber + "?\n\n" + draft.Evaluation.Body
        };
        Signal(McpHumanInputSignalPhase.Waiting);
        JsonNode? answer;
        try { answer = await (confirmation ?? humanInput).RequestInputAsync(prompt, ct); }
        catch { Signal(McpHumanInputSignalPhase.Cancelled); throw; }
        var rejected = HumanInputContract.IsAbandoned(answer) || !HumanInputContract.TryReadConfirmation(answer, ["approve", "reject"], out var approved) || !approved;
        Signal(rejected ? McpHumanInputSignalPhase.Refused : McpHumanInputSignalPhase.Resumed);
        if (rejected)
        {
            await SaveAsync(stored with { Status = "rejected" }, CancellationToken.None);
            return Result("rejected", "The publication was rejected or abandoned.");
        }
        ct.ThrowIfCancellationRequested();
        // Read AFTER confirmation, then submit directly against the exact reviewed commit.
        var details = await github.CallToolAsync("pull_request_read", Arguments("get"), ct);
        if (details.IsError || Payload(details.Content)?["head"]?["sha"]?.GetValue<string>() is not { } current)
            throw new InvalidOperationException("The current pull-request head could not be verified.");
        if (!string.Equals(current, draft.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync(stored with { Status = "stale" }, CancellationToken.None);
            return Result("stale", "The pull-request head changed. A new review and confirmation are required.");
        }
        ct.ThrowIfCancellationRequested();
        await SaveAsync(stored with { Status = "dispatching" }, ct);
        var write = Arguments("create");
        write["commitID"] = draft.HeadSha; write["body"] = draft.Evaluation.Body;
        write["event"] = draft.Evaluation.SubmitEvent switch { ReviewSubmitEvent.Approve => "APPROVE", ReviewSubmitEvent.RequestChanges => "REQUEST_CHANGES", _ => "COMMENT" };
        // Any exception or cancellation after reservation leaves an uncertain receipt;
        // restart and workflow retries never redispatch it.
        var response = await github.CallToolAsync("pull_request_review_write", write, ct);
        if (response.IsError) return Result("dispatching", "The publication failed or is uncertain. It will not be automatically retried.");
        await SaveAsync(stored with { Status = "published" }, CancellationToken.None);
        return Result("published", "The confirmed review was published on the reviewed commit.");

        JsonObject Arguments(string method) => new() { ["method"] = method, ["owner"] = draft.Owner, ["repo"] = draft.Repository, ["pullNumber"] = draft.PullNumber };
        ReviewPublishResult Result(string status, string message) => new(status, id, message);
        void Signal(McpHumanInputSignalPhase phase)
        { if (call is not null) call.HumanInputHandler(new(call.Correlation, prompt, phase)); }
    }

    private async Task<StoredReviewDraft?> LoadAsync(string runId, string id, CancellationToken ct)
    {
        var row = await records.GetAsync(Drafts, Tenant, id, Author, ct);
        if (row is null) return null;
        var value = JsonSerializer.Deserialize(row.Value, ReviewPublicationJsonContext.Default.StoredReviewDraft)
            ?? throw new InvalidDataException("Invalid stored review.");
        if (value.TenantId != Tenant || value.RunId != runId || value.Draft.DraftId != id)
            throw new InvalidOperationException("The stored review belongs to another execution.");
        return value;
    }
    private Task<KeyVaultRecordValue> SaveAsync(StoredReviewDraft value, CancellationToken ct) => records.UpsertAsync(Drafts, Tenant, value.Draft.DraftId,
        JsonSerializer.Serialize(value, ReviewPublicationJsonContext.Default.StoredReviewDraft), Author, ct);
    private FileStream Acquire(string id)
    {
        ValidateId(id);
        var directory = Path.Combine(GnOuGoWorkspace.ResolveDefaultWorkingDirectorySafe(), ".GnOuGo", "data", "review-locks");
        Directory.CreateDirectory(directory);
        // Empty lock files contain no review content. OS ownership also covers separate host processes.
        return new FileStream(Path.Combine(directory, Hash(Tenant) + "-" + id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static void ValidateId(string id)
    { if (id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid review draft identity."); }
    private static bool SafeName(string value) => !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static string EvidenceKey(string run, string kind, JsonNode value) => Hash(run + "\n" + kind + "\n" + Canonical(value));
    private static string Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonValue.Create(p.Key)!.ToJsonString() + ":" + Canonical(p.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]", _ => value?.ToJsonString() ?? "null"
    };
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static JsonNode? Payload(JsonNode? value)
    {
        if (value is JsonValue text && text.TryGetValue<string>(out var json)) return JsonNode.Parse(json);
        return value;
    }
}
