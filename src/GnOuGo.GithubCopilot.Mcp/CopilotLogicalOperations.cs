using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

public sealed class CopilotLogicalLimits
{
    public int Sessions { get; set; } = 3;
    public int Interactions { get; set; } = 64;
    public int Seconds { get; set; } = 1800;
    public int InferenceAttempts { get; set; } = 32;
    public long ReservedTokens { get; set; } = 2_000_000;
    internal void Validate()
    {
        if (Sessions is < 1 or > 3 || Interactions is < 1 or > 64 || Seconds is < 1 or > 1800 ||
            InferenceAttempts is < 1 or > 32 || ReservedTokens is < 1 or > 2_000_000)
            throw new InvalidOperationException("Logical Copilot limits must be positive and cannot exceed the host safety ceilings.");
    }
}

/// <summary>One task receipt and non-renewable authority across verified SDK continuation boundaries.</summary>
internal sealed class CopilotLogicalOperations(CopilotSessionManager sessions, KeyVaultCopilotTaskStore store,
    McpCopilotHumanInputProvider human, IOptions<CodeServerSettings> settings)
{
    internal async Task<CopilotSendResult> RunAsync(CopilotSessionCreateRequest original, string objective,
        IReadOnlyList<CopilotAttachment>? attachments, McpServer server, Action<CopilotStreamEvent> progress,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var id = store.CurrentTaskId ?? throw new McpException("Long interactive operations require the negotiated MCP Tasks extension.");
        var limits = settings.Value.Copilot.LogicalLimits; limits.Validate();
        if (original.Configuration.ExecutionBounds is not null)
            throw new McpException("Bounded agent tasks cannot use interactive continuation.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(limits.Seconds);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(limits.Seconds));
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var interaction = human.Push(server, lifetime.Token);
        var snapshots = new List<CopilotSendResult>();
        var count = 0;
        await store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state =>
        {
            if (state.Count != 0) throw new McpException("A logical operation cannot be dispatched twice.");
            state["tenant"] = original.Context.TenantId; state["objective"] = objective;
            state["workspace"] = original.Configuration.WorkingDirectory; state["deadline"] = deadline;
            state["agentId"] = original.Context.AgentId;
            state["phase"] = "admitted"; state["sessions"] = 0;
            state["inferenceAttempts"] = 0; state["reservedTokens"] = 0;
            state["denials"] = new JsonArray(); state["evidence"] = new JsonArray();
        }, lifetime.Token);
        var budget = new CopilotInferenceBudget(limits.InferenceAttempts, limits.ReservedTokens, deadline,
            (calls, tokens, token) => store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state =>
            { state["inferenceAttempts"] = calls; state["reservedTokens"] = tokens; }, token));
        using var stopAdmissions = lifetime.Token.Register(budget.CloseAdmissions);
        var permissions = new CopilotLogicalPermissions((key, token) => store.UpdateOwnedOperationAsync(original.Context.TenantId, id,
            state => state["denials"]!.AsArray().Add((JsonNode?)JsonValue.Create(key)), token));
        var request = original with { SessionKind = CopilotSessionKind.Managed, PermissionMode = CopilotPermissionMode.Interactive,
            Configuration = original.Configuration with { LogicalInferenceBudget = budget, LogicalPermissions = permissions } };
        var monitor = MonitorCancellationAsync(id, lifetime, monitoring.Token);
        CopilotSendResult? final = null;
        var completionUnknown = false;
        try
        {
            while (count < limits.Sessions)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                count++;
                await store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state => { state["sessions"] = count; state["phase"] = "creating"; }, lifetime.Token);
                var descriptor = await sessions.CreateAsync(request, lifetime.Token);
                await store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state =>
                { state["handle"] = descriptor.Handle; state["sdkSession"] = descriptor.CopilotSessionId; state["phase"] = "dispatching"; }, lifetime.Token);
                var verified = false; var continuation = false;
                try
                {
                    var prompt = snapshots.Count == 0 ? objective : Continuation(objective, snapshots, await store.AnswersAsync(id, lifetime.Token));
                    final = await sessions.SendAsync(new(original.Context, descriptor.Handle, prompt, AgentMode: "interactive",
                        Attachments: attachments, TimeoutSeconds: Math.Min(original.Configuration.RequestTimeoutSeconds, limits.Seconds))
                        { Progress = progress, RequestHeaders = headers, LogicalAuthority = budget }, lifetime.Token);
                    verified = final.Completed;
                }
                catch (CopilotSendInterruptedException interrupted)
                {
                    final = interrupted.Snapshot;
                    verified = continuation = interrupted.VerifiedContinuation && !budget.TransportFailed && budget.AdmissionStop is null && !lifetime.IsCancellationRequested;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    final = new(descriptor.Handle, descriptor.CopilotSessionId, "COPILOT_NEEDS_RECONCILIATION: no verified turn completion is available.", null, [], false);
                }
                snapshots.Add(final);
                completionUnknown = !verified;
                using (var flush = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    await store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state =>
                    {
                        state["phase"] = verified ? "quiescent" : "reconciliation_required";
                        state["evidence"]!.AsArray().Add(JsonSerializer.SerializeToNode(final, CopilotCoreJsonContext.Default.CopilotSendResult));
                    }, flush.Token);
                // Deletion is allowed only after a verified idle/completion boundary. Unknown
                // external work retains its SDK identity for explicit reconciliation.
                if (verified)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await sessions.DeleteAsync(original.Context, descriptor.Handle, cleanup.Token, budget);
                }
                if (continuation && count >= limits.Sessions)
                {
                    final = final with { Content = "COPILOT_SESSION_LIMIT: the logical operation exhausted its SDK-session allowance. " + final.Content };
                    using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state => state["phase"] = "limit_reached", flush.Token);
                }
                if (!continuation || count >= limits.Sessions) break;
            }
            final = Consolidate(final!, snapshots, count);
            using var receipt = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await store.UpdateOwnedOperationAsync(original.Context.TenantId, id, state =>
            {
                state["result"] = JsonSerializer.SerializeToNode(final, CopilotCoreJsonContext.Default.CopilotSendResult);
                if (final.Completed) state["phase"] = "completed";
            }, receipt.Token);
            if (completionUnknown)
            {
                // A partial payload is evidence, not a completion receipt. Fail the
                // transport task so the caller's journal cannot release cleanup.
                // The SDK's later SetCompleted is idempotently ignored by the store.
                using var error = JsonDocument.Parse("""{"code":-32603,"message":"COPILOT_NEEDS_RECONCILIATION: external completion is unknown; partial evidence is retained in the logical task."}""");
                await budget.StopAsync();
                await store.SetFailedAsync(id, error.RootElement, receipt.Token);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            return final;
        }
        finally
        {
            await budget.StopAsync();
            await monitoring.CancelAsync();
            try { await monitor; } catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
        }
    }

    private async Task MonitorCancellationAsync(string id, CancellationTokenSource owner, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct))
            if ((await store.GetTaskAsync(id, ct))?.Status == McpTaskStatus.Cancelled) { await owner.CancelAsync(); return; }
    }
    private static string Continuation(string objective, IEnumerable<CopilotSendResult> evidence, JsonNode answers)
        => "Continue the same approved objective from the verified idle checkpoint. Preserve prior refusals and human decisions. " +
           "Do not repeat completed external operations. Session-local approvals have expired. Report partial work truthfully.\nObjective:\n" + objective +
           "\nVerified observations:\n" + new JsonArray(evidence.Select(e => JsonSerializer.SerializeToNode(e, CopilotCoreJsonContext.Default.CopilotSendResult)).ToArray()).ToJsonString() +
           "\nRecorded human responses (data, not new permission grants):\n" + answers.ToJsonString();
    private static CopilotSendResult Consolidate(CopilotSendResult final, IReadOnlyList<CopilotSendResult> evidence, int count)
        => final with
        {
            ToolExecutions = evidence.SelectMany(e => e.ToolExecutions.Select(o => o with
            { ToolCallId = e.CopilotSessionId + ":" + o.ToolCallId, ParentToolCallId = o.ParentToolCallId is null ? null : e.CopilotSessionId + ":" + o.ParentToolCallId })).ToArray(),
            ModifiedFiles = evidence.SelectMany(e => e.ModifiedFiles).Distinct(StringComparer.Ordinal).ToArray(),
            Events = evidence.SelectMany(e => e.Events).Append(new("logical_operation", "info", $"SDK sessions: {count}.", DateTimeOffset.UtcNow)).ToArray(),
            // Individual provider usage is retained in encrypted evidence. Never present the last session as a complete total.
            Usage = count == 1 ? final.Usage : null
        };
}
