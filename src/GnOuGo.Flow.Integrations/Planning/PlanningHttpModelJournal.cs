using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Planning;

/// <summary>Encrypted physical attempts beneath the existing leased planning request journal.</summary>
public sealed class PlanningHttpModelJournal(
    IKeyVaultRecordStore records, LLMUsageBudgetScope budget, PlanningRequest planning,
    LLMRequest request, IModelUsageCostEstimator? estimator, string collection, string author,
    DateTimeOffset deadlineUtc) : ILLMHttpRetryJournal
{
    private string Key => request.ClientRequestId ?? throw Conflict();
    private PlanningTransportPolicy Policy => PlanningTransportPolicies.Read(planning) ?? throw Conflict();
    private string RequestHash => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    private JsonObject PolicyJson => JsonSerializer.SerializeToNode(Policy, PlanningJsonContext.Default.PlanningTransportPolicy)!.AsObject();
    private int Input => Math.Max(PlanningConstructionInputPolicy.EffectiveLimit(planning, request),
        JsonSerializer.SerializeToUtf8Bytes(request, PlanningJsonContext.Default.LLMRequest).Length);
    private int Output => request.MaxTokens is > 0 ? request.MaxTokens.Value : throw Conflict();
    private int[] Reviews => Enumerable.Repeat(PlanningConstructionPhasePolicies.Resolve(planning, "construction_local_judge").OutputTokens,
        request.RequiredReviewCalls is >= 0 and <= 2 ? request.RequiredReviewCalls.Value : throw Conflict()).ToArray();
    private string AttemptKey(int number) => Key + ":attempt:" + number;

    /// <summary>Identifies exact saved success for receipt-only recovery before planner admission.</summary>
    public async Task<bool> HasSuccessfulResponseAsync(CancellationToken ct) =>
        await ReadAsync(ct) is { } record && HasSuccessfulResponse(record);

    /// <summary>Checks exact saved-response recovery without dispatch, charging or changing a record.</summary>
    public async Task VerifyRecoveryAsync(CancellationToken ct)
    {
        var record = await ReadAsync(ct) ?? throw Unknown();
        if (Transport(record)?.Attempts.LastOrDefault() is not { Status: not null, Body: not null }) throw Unknown();
        if (!HasSuccessfulResponse(record) && DateTimeOffset.UtcNow >= record["deadline_utc"]!.GetValue<DateTimeOffset>()) throw Exhausted();
    }

    public async Task<LLMResponse> CallAsync(ILLMClient client, bool allowInitialize, CancellationToken ct)
    {
        PlanningTransportPolicies.ValidateIssued(request, planning);
        var record = await ReadAsync(ct);
        if (record is null)
        {
            if (!allowInitialize) throw Unknown();
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadlineUtc) throw Exhausted();
            // The first reservation also covers synchronous local implementations.
            // No provider invocation is permitted until both writes complete.
            await budget.EnsureAllowanceAsync(request, estimator, ReservationInput,
                new[] { Output }.Concat(Reviews).ToArray(), "planning.http", ct);
            record = new JsonObject
            {
                ["version"] = 1, ["tenant"] = planning.TenantId, ["session"] = planning.SessionId,
                ["request_hash"] = RequestHash, ["policy"] = PolicyJson,
                ["deadline_utc"] = deadlineUtc, ["input_ceiling"] = ReservationInput,
                ["output_ceiling"] = Output, ["reserved_attempts"] = 1,
                ["transport"] = null
            };
            await WriteAsync(record, ct);
            await ReserveAsync(1, ct);
        }
        else
        {
            // Reconcile durable reservations before inspecting recovery eligibility.
            for (var index = 1; index <= record["reserved_attempts"]!.GetValue<int>(); index++)
                await ReserveAsync(index, ct);
            var retained = Transport(record);
            if (retained?.Attempts.LastOrDefault() is not { Status: not null }) throw Unknown();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = record["deadline_utc"]!.GetValue<DateTimeOffset>() - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero && !HasSuccessfulResponse(record)) throw Exhausted();
        // Settling a saved success makes no provider call and cannot replenish time.
        deadline.CancelAfter(HasSuccessfulResponse(record) ? TimeSpan.FromSeconds(30) : remaining);
        var policy = Policy;
        using var context = new LLMHttpRetryContext(Key, this, new LLMProviderRetryPolicyOptions
        {
            MaxAttempts = policy.MaxAttempts, MaxUncertainRetries = 0,
            AttemptTimeoutMilliseconds = policy.AttemptTimeoutMilliseconds,
            BaseDelayMilliseconds = policy.BaseDelayMilliseconds, MaxDelayMilliseconds = policy.MaxDelayMilliseconds,
            MaxTotalDelayMilliseconds = policy.MaxTotalDelayMilliseconds
        }).Activate();
        var response = await client.CallAsync(request, deadline.Token);
        using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        record = await ReadAsync(flush.Token) ?? throw Conflict();
        var state = Transport(record);
        var count = state?.Attempts.Count ?? 1;
        if (state is not null && (state.Attempts.LastOrDefault()?.Status is not (>= 200 and < 300))) throw Unknown();
        if (response.Usage is null) throw Unknown();
        await budget.SettleJournaledAttemptAsync(AttemptKey(count), request, estimator, response.Usage, "planning.http", flush.Token);
        // Provider totals remain whole-call measurements; reservations are separate metadata.
        if (response.Usage is JsonObject usage)
        {
            usage["transport_attempts"] = count;
            usage["unknown_attempts"] = count - 1;
            usage["reserved_input_tokens"] = (long)(count - 1) * ReservationInput;
            usage["reserved_output_tokens"] = (long)(count - 1) * Output;
        }
        return response;
    }

    // Use the existing conservative bound for future review envelopes as well as this request.
    private int ReservationInput => Reviews.Length == 0 ? Input : Math.Max(Input,
        checked(3 * PlanningConstructionPhasePolicies.Resolve(planning, "construction_local_judge").MaxInputTokens));

    private Task ReserveAsync(int number, CancellationToken ct) => budget.ReserveJournaledAttemptAsync(
        AttemptKey(number), request, estimator, ReservationInput, Output, Reviews, "planning.http", ct);

    public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
    {
        var record = await ReadAsync(ct) ?? throw Conflict();
        return Transport(record);
    }

    public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
    {
        var record = await ReadAsync(ct) ?? throw Conflict();
        var previous = Transport(record);
        var oldCount = previous?.Attempts.Count ?? 0;
        if (state.Attempts.Count < 1 || state.Attempts.Count > Policy.MaxAttempts ||
            state.Attempts.Count < oldCount || state.Attempts.Count > oldCount + 1 ||
            previous is not null && previous.Fingerprint != state.Fingerprint ||
            state.Attempts.Any(a => !a.Id.StartsWith(Key + ":", StringComparison.Ordinal)) ||
            state.Attempts.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != state.Attempts.Count)
            throw Conflict();
        if (previous is not null)
            for (var index = 0; index < oldCount; index++)
            {
                var before = previous.Attempts[index]; var after = state.Attempts[index];
                if (before.Id != after.Id || before.Status is not null &&
                    (before.Status != after.Status || before.Body != after.Body || before.ContentType != after.ContentType || before.RetryAfter != after.RetryAfter) ||
                    index < oldCount - 1 && !Same(before, after)) throw Conflict();
            }
        if (state.Attempts.Count > oldCount)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= record["deadline_utc"]!.GetValue<DateTimeOffset>()) throw Exhausted();
            if (state.Attempts[^1] is not { Status: null, Failure: null, Body: null } ||
                oldCount > 0 && previous!.Attempts[^1].Status is null) throw Unknown();
            // Reject ordinary allowance exhaustion before recording a new attempt.
            // The first attempt was admitted and reserved by CallAsync already.
            if (state.Attempts.Count > record["reserved_attempts"]!.GetValue<int>())
                await budget.EnsureAllowanceAsync(request, estimator, ReservationInput,
                    new[] { Output }.Concat(Reviews).ToArray(), "planning.http", ct);
            // Persist intent before accounting. A crash here has no durable outcome,
            // so recovery conservatively reconciles the charge and forbids dispatch.
            record["reserved_attempts"] = state.Attempts.Count;
            record["transport"] = JsonSerializer.SerializeToNode(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
            await WriteAsync(record, ct);
            await ReserveAsync(state.Attempts.Count, ct);
        }
        record["transport"] = JsonSerializer.SerializeToNode(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        await WriteAsync(record, ct);
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        var saved = await records.GetAsync(collection, planning.TenantId, Key, author, ct);
        if (saved is null) return null;
        var record = JsonNode.Parse(saved.Value)?.AsObject() ?? throw Conflict();
        if (record["version"]?.GetValue<int>() != 1 || record["tenant"]?.ToString() != planning.TenantId ||
            record["session"]?.ToString() != planning.SessionId || record["request_hash"]?.ToString() != RequestHash ||
            !JsonNode.DeepEquals(record["policy"], PolicyJson) || record["input_ceiling"]?.GetValue<int>() != ReservationInput ||
            record["output_ceiling"]?.GetValue<int>() != Output || record["reserved_attempts"]?.GetValue<int>() is not (>= 1 and <= 3) ||
            record["deadline_utc"] is null) throw Conflict();
        return record;
    }

    private async Task WriteAsync(JsonObject record, CancellationToken ct) =>
        await records.UpsertAsync(collection, planning.TenantId, Key, record.ToJsonString(), author, ct);
    private static LLMHttpRetryState? Transport(JsonObject record) => record["transport"]?.Deserialize(LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
    private static bool HasSuccessfulResponse(JsonObject record) => Transport(record)?.Attempts.LastOrDefault() is
        { Status: >= 200 and < 300, Body: not null, Failure: null };
    private static bool Same(LLMHttpAttempt left, LLMHttpAttempt right) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left, LLMHttpRetryJsonContext.Default.LLMHttpAttempt),
            JsonSerializer.SerializeToNode(right, LLMHttpRetryJsonContext.Default.LLMHttpAttempt));
    private static PlanningConflictException Conflict() => new("The captured planning HTTP identity, policy or reservation changed.");
    private static WorkflowRuntimeException Unknown() => new(ErrorCodes.LlmBudgetUnverifiable,
        "The previous planning attempt has no verified response or usage. Its reservation is retained; it cannot be dispatched again.");
    private static WorkflowRuntimeException Exhausted() => new(ErrorCodes.LlmBudgetExceeded, "The captured planning deadline is exhausted.");
}
