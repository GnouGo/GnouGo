using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace GnOuGo.GithubCopilot.Core;

public sealed class CopilotReviewManager
{
    private const string ReviewSystemMessage = "You are a read-only pull-request reviewer. Report only concrete correctness, security, reliability, or maintainability defects introduced by the supplied diff. Never reveal hidden reasoning. Return only the requested JSON array.";

    private readonly CopilotSessionManager _sessions;
    private readonly ConcurrentDictionary<string, ReviewState> _reviews = new(StringComparer.Ordinal);

    public CopilotReviewManager(CopilotSessionManager sessions)
    {
        _sessions = sessions;
    }

    public async Task<CopilotReviewSession> StartAsync(CopilotReviewStartRequest request, CancellationToken cancellationToken)
    {
        ValidateStart(request);
        var batches = ReviewValidation.CreateBatches(request.Files, request.MaxBatchCharacters);
        var coverage = ReviewValidation.CalculateCoverage(request.Files);
        var createRequest = new CopilotSessionCreateRequest(
            request.Context,
            request.Configuration with
            {
                SystemMessage = ReviewSystemMessage,
                EnableConfigDiscovery = false,
                AvailableTools = [],
                SkillDirectories = [],
                ManagedSessionTtlSeconds = Math.Max(request.Configuration.ManagedSessionTtlSeconds, 300)
            },
            CopilotSessionKind.Managed,
            request.PermissionMode,
            Streaming: false);
        var session = await _sessions.CreateAsync(createRequest, cancellationToken);
        var reviewHandle = $"cpr_{Guid.NewGuid():N}";
        var state = new ReviewState(reviewHandle, request, session.Handle, batches, coverage);
        if (!_reviews.TryAdd(reviewHandle, state))
        {
            await _sessions.DeleteAsync(request.Context, session.Handle, CancellationToken.None);
            throw new InvalidOperationException("Could not allocate a unique review handle.");
        }
        return state.Describe();
    }

    public async Task<CopilotReviewAnalyzeResult> AnalyzeBatchAsync(
        CopilotRequestContext context,
        string reviewHandle,
        int batchIndex,
        CancellationToken cancellationToken)
    {
        var state = GetOwnedState(reviewHandle, context.TenantId);
        if (batchIndex < 0 || batchIndex >= state.Batches.Count)
            throw new ArgumentOutOfRangeException(nameof(batchIndex), $"batchIndex must be between 0 and {state.Batches.Count - 1}.");

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.CompletedBatches.Contains(batchIndex))
            {
                return new CopilotReviewAnalyzeResult(
                    reviewHandle,
                    batchIndex,
                    state.Findings.Where(finding => state.Batches[batchIndex].Files.Any(file => file.Path == finding.Path)).ToArray(),
                    state.Rejections.ToArray());
            }

            var batch = state.Batches[batchIndex];
            var prompt = BuildBatchPrompt(state.Request, batch);
            var response = await _sessions.SendAsync(
                new CopilotSendRequest(context, state.SessionHandle, prompt, "enqueue", "interactive"),
                cancellationToken);
            var candidates = ParseCandidates(response.Content);
            var fileMap = state.Request.Files.ToDictionary(static file => ReviewValidation.NormalizePath(file.Path), StringComparer.Ordinal);
            var accepted = new List<ReviewFinding>();
            var rejected = new List<string>();
            foreach (var candidate in candidates)
            {
                if (ReviewValidation.TryValidate(candidate, fileMap, out var finding, out var rejection))
                    accepted.Add(finding!);
                else
                    rejected.Add(rejection!);
            }

            state.Findings.AddRange(accepted);
            state.Rejections.AddRange(rejected);
            state.CompletedBatches.Add(batchIndex);
            return new CopilotReviewAnalyzeResult(reviewHandle, batchIndex, ReviewValidation.Deduplicate(accepted), rejected);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<CopilotReviewResult> FinishAsync(CopilotRequestContext context, string reviewHandle, CancellationToken cancellationToken)
    {
        var state = GetOwnedState(reviewHandle, context.TenantId);
        if (!_reviews.TryRemove(reviewHandle, out _))
            throw new InvalidOperationException("The review was already finished.");

        try
        {
            var findings = ReviewValidation.Deduplicate(state.Findings);
            var missingBatches = state.Batches.Select(static batch => batch.Index).Except(state.CompletedBatches).ToArray();
            if (missingBatches.Length > 0)
                state.Rejections.Add($"Review finished before batches {string.Join(", ", missingBatches)} were analyzed.");
            var summary = findings.Count == 0
                ? "No validated findings were produced."
                : $"Produced {findings.Count} validated finding(s), including {findings.Count(static finding => finding.Severity >= ReviewSeverity.High)} high-or-critical finding(s).";
            return new CopilotReviewResult(state.Request.BaseSha, state.Request.HeadSha, findings, state.Coverage, state.Rejections.ToArray(), summary);
        }
        finally
        {
            await _sessions.DeleteAsync(context, state.SessionHandle, CancellationToken.None);
            state.Gate.Dispose();
        }
    }

    public async Task<CopilotReviewResult> ReviewAsync(CopilotReviewStartRequest request, CancellationToken cancellationToken)
    {
        var started = await StartAsync(request, cancellationToken);
        try
        {
            for (var index = 0; index < started.BatchCount; index++)
                await AnalyzeBatchAsync(request.Context, started.ReviewHandle, index, cancellationToken);
            return await FinishAsync(request.Context, started.ReviewHandle, cancellationToken);
        }
        catch
        {
            if (_reviews.ContainsKey(started.ReviewHandle))
                await FinishAsync(request.Context, started.ReviewHandle, CancellationToken.None);
            throw;
        }
    }

    internal static IReadOnlyList<ReviewFindingCandidate> ParseCandidates(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return [];
        var json = ExtractJson(response);
        try
        {
            return JsonSerializer.Deserialize(json, CopilotCoreJsonContext.Default.IReadOnlyListReviewFindingCandidate) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Copilot review output was not a valid JSON array of findings.", ex);
        }
    }

    private static string BuildBatchPrompt(CopilotReviewStartRequest request, CopilotReviewBatch batch)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Review batch {batch.Index + 1}. Exact base SHA: {request.BaseSha}. Exact head SHA: {request.HeadSha}.");
        builder.AppendLine("Return a JSON array. Each item must contain: severity (low|medium|high|critical), category, confidence (0..1), path, side (left|right), startLine, endLine, evidence, explanation, suggestedPatch (optional). Use only paths and diff lines shown below. Return [] when there is no concrete defect.");
        foreach (var file in batch.Files)
        {
            builder.AppendLine();
            builder.AppendLine($"FILE: {file.Path} STATUS: {file.Status} PREVIOUS: {file.PreviousPath ?? "-"} TRUNCATED: {file.Truncated}");
            builder.AppendLine(file.Patch);
        }
        return builder.ToString();
    }

    private static string ExtractJson(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
        }

        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end < start)
            throw new InvalidOperationException("Copilot review output did not contain a JSON array.");
        return trimmed[start..(end + 1)];
    }

    private ReviewState GetOwnedState(string handle, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (!_reviews.TryGetValue(handle, out var state))
            throw new KeyNotFoundException("The Copilot review handle was not found.");
        if (!string.Equals(state.Request.Context.TenantId, tenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The Copilot review handle does not belong to this tenant.");
        return state;
    }

    private static void ValidateStart(CopilotReviewStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Context.TenantId))
            throw new ArgumentException("TenantId is required.", nameof(request));
        if (!IsExactCommitSha(request.BaseSha) || !IsExactCommitSha(request.HeadSha))
            throw new ArgumentException("Exact 40- or 64-character hexadecimal base and head commit SHAs are required.", nameof(request));
        if (request.Files is null)
            throw new ArgumentException("files is required.", nameof(request));
        if (request.PermissionMode == CopilotPermissionMode.Interactive && request.Configuration.EnableApproveAll)
            throw new InvalidOperationException("Interactive review sessions must not enable approve_all.");
    }

    private static bool IsExactCommitSha(string? value)
        => value is not null
            && value.Length is 40 or 64
            && value.All(Uri.IsHexDigit);

    private sealed class ReviewState
    {
        public ReviewState(string reviewHandle, CopilotReviewStartRequest request, string sessionHandle, IReadOnlyList<CopilotReviewBatch> batches, ReviewCoverage coverage)
        {
            ReviewHandle = reviewHandle;
            Request = request;
            SessionHandle = sessionHandle;
            Batches = batches;
            Coverage = coverage;
        }

        public string ReviewHandle { get; }
        public CopilotReviewStartRequest Request { get; }
        public string SessionHandle { get; }
        public IReadOnlyList<CopilotReviewBatch> Batches { get; }
        public ReviewCoverage Coverage { get; }
        public List<ReviewFinding> Findings { get; } = [];
        public List<string> Rejections { get; } = [];
        public HashSet<int> CompletedBatches { get; } = [];
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public CopilotReviewSession Describe()
            => new(ReviewHandle, SessionHandle, Request.BaseSha, Request.HeadSha, Batches.Count, Coverage);
    }
}
