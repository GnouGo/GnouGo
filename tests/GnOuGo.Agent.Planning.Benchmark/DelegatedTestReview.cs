using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

// Test-host authority only. Neither planner responses nor Judge findings can create a review.
internal sealed record DelegatedTestAuthorization(string Id, string UserAuthorization, string Reviewer,
    string Campaign, string Run, string Scenario, string WorkspaceRoot, string SourceRevision);
internal sealed record DelegatedRequirementReview(string Id, string RequirementHash, List<string> Obligations, string Assessment,
    string UnverifiedAspects = "Business completeness and successful execution remain unverified.");
internal sealed record DelegatedTaskReview(string Id, string TaskHash, string Evidence, string Assessment);
internal sealed record DelegatedReviewEvidence(List<DelegatedRequirementReview> Requirements, List<DelegatedTaskReview> Tasks);
internal sealed record DelegatedTestReviewDocument(PlanningApprovalReview Receipt, DelegatedReviewEvidence Evidence);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DelegatedTestAuthorization))]
[JsonSerializable(typeof(DelegatedTestReviewDocument))]
[JsonSerializable(typeof(DelegatedReviewEvidence))]
internal partial class DelegatedTestJsonContext : JsonSerializerContext;

internal sealed class DelegatedTestReviewAuthority(DelegatedTestAuthorization authorization,
    DelegatedTestReviewDocument document, string campaign, string run, string scenario, string root, string source)
{
    private string? _confirmationPrompt;
    private string? _confirmationStep;
    private List<string> _runtimeInvocationSuffixes = [];
    private readonly Dictionary<string, McpCorrelationContext> _activeCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _browserCursors = new(StringComparer.Ordinal);
    private string? _browserUrl;
    private string? _browserSnapshot;
    private bool _proofReadValidated;

    internal IDisposable BeginCall(McpCallExecutionContext context)
    {
        var call = context.Correlation;
        if (call.ServerName != "GnOuGo.GithubCopilot.Mcp" || call.MethodName != "code_agent_edit") return new CallRegistration(() => { });
        if (call.TenantId != "benchmark" || call.RunId != run || call.StepType != "mcp.call" || call.StepId is null ||
            !_runtimeInvocationSuffixes.Any(s => call.StepId.EndsWith(s, StringComparison.Ordinal)))
            throw new PlanningConflictException("The concrete invocation is outside the reviewed isolated artifact.");
        lock (_activeCalls)
            if (!_activeCalls.TryAdd(call.StepId, call)) throw new PlanningConflictException("Duplicate active delegated invocation.");
        return new CallRegistration(() => { lock (_activeCalls) _activeCalls.Remove(call.StepId); });
    }

    private sealed class CallRegistration(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    internal string AuthorizationHash => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization));
    internal PlanningApprovalReview Receipt => document.Receipt;
    internal static string[] Obligations(string scenario) => scenario switch
    {
        "amazon" => ["search", "observed_product_order", "exact_product_fields", "workbook", "cleanup", "truthful_result"],
        "code" => ["pinned_checkout", "repository_checks", "requested_review", "local_report", "cleanup", "truthful_result"],
        "synthetic" => ["exact_outputs", "declared_effects", "cleanup"],
        PlanningBenchmarkCases.ReadGroupProofCase => ["exact_outputs", "reusable_group_calls", "single_seed_read", "unchanged_workspace"],
        _ => throw new PlanningConflictException("The delegated scenario is not authorized.")
    };

    internal void Validate(PlanningSession state)
    {
        _proofReadValidated = false;
        if (string.IsNullOrWhiteSpace(authorization.Id) || string.IsNullOrWhiteSpace(authorization.UserAuthorization) ||
            string.IsNullOrWhiteSpace(authorization.Reviewer) || authorization.Campaign != campaign || authorization.Run != run ||
            authorization.Scenario != scenario || Path.GetFullPath(authorization.WorkspaceRoot) != Path.GetFullPath(root) ||
            authorization.SourceRevision != source || state.Request.TenantId != "benchmark" || state.Request.SessionId != run ||
            document.Receipt.Reviewer != authorization.Reviewer || document.Receipt.AuthorizationHash != AuthorizationHash ||
            document.Receipt.EvidenceHash != PlanningGraphCompiler.Fingerprint(
                JsonSerializer.Serialize(document.Evidence, DelegatedTestJsonContext.Default.DelegatedReviewEvidence)))
            throw new PlanningConflictException("The delegated review does not match its frozen host authorization and evidence.");
        if (scenario == PlanningBenchmarkCases.ReadGroupProofCase &&
            (state.Request.Name != PlanningBenchmarkCases.ReadGroupProofCase ||
             Receipt.Origin != "delegated_test" || Receipt.TenantId != state.Request.TenantId || Receipt.SessionId != run ||
             Receipt.ReviewedRevision != state.Revision || Receipt.ArtifactHash != state.ComputeArtifactHash() ||
             Receipt.ReviewedRequirementIds.Count != state.Requirements?.Outcomes.Count ||
             !Receipt.ReviewedRequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(state.Requirements?.Outcomes.Select(r => r.Id) ?? []) ||
             SyntheticArtifactOracle.Validate(scenario, state.Plan, state.Catalog).Count != 0))
            throw new PlanningConflictException("The local-read delegation requires the exact proof case and its independently checked artifact.");
        var requirements = state.Requirements?.Outcomes ?? [];
        var reviews = document.Evidence.Requirements;
        if (reviews.Count != requirements.Count || reviews.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != reviews.Count ||
            requirements.Any(r => !reviews.Any(v => v.Id == r.Id && v.Assessment == "accepted_with_recorded_uncertainty" && !string.IsNullOrWhiteSpace(v.UnverifiedAspects) &&
                v.RequirementHash == PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(r, PlanningJsonContext.Default.PlanningRequirement)))) ||
            !reviews.SelectMany(r => r.Obligations).ToHashSet(StringComparer.Ordinal).SetEquals(Obligations(scenario)))
            throw new PlanningConflictException("Explicit requirement reviews and independent scenario obligations are incomplete.");
        var tasks = Tasks(state.Plan ?? throw new PlanningConflictException("No reviewable TaskPlan.")).ToArray();
        if (document.Evidence.Tasks.Count != tasks.Length || document.Evidence.Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tasks.Length ||
            tasks.Any(t => !document.Evidence.Tasks.Any(v => v.Id == t.Id && v.Assessment == "reviewed" && !string.IsNullOrWhiteSpace(v.Evidence) &&
                v.TaskHash == PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(t, PlanningJsonContext.Default.PlanTask)))))
            throw new PlanningConflictException("The bounded task review evidence does not cover this exact artifact.");
        foreach (var task in tasks.Where(t => t.Operation is not null))
        {
            var capability = state.Catalog?.Capabilities.SingleOrDefault(c => (c.Operation?.Id ?? c.Id) == task.Operation)
                ?? throw new PlanningConflictException("A reviewed operation has no owned contract.");
            RequireTool(capability.Server ?? "", capability.Method ?? "", null, inspectOnly: true);
        }
        var confirmation = state.Graph?.Workflows.SingleOrDefault(w => w.Key == state.Graph.Entrypoint)?.Steps
            .SingleOrDefault(s => s.Key == PlanningConfirmationGuards.Confirm && s.InternalRole == "confirmation");
        _confirmationPrompt = confirmation is null ? null : state.Graph!.Summary;
        _confirmationStep = confirmation is null ? null : "/workflow/main/step/n_" + PlanningGraphCompiler.Fingerprint(PlanningConfirmationGuards.Confirm)[..16];
        _runtimeInvocationSuffixes = state.Graph?.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Where(n => n.Type == "mcp.call" && state.Catalog?.Capabilities.Any(c => c.Id == n.CapabilityId &&
                c.Server == "GnOuGo.GithubCopilot.Mcp" && c.Method == "code_agent_edit") == true)
            .Select(n => "/step/n_" + PlanningGraphCompiler.Fingerprint(n.Key)[..16]).Distinct(StringComparer.Ordinal).ToList() ?? [];
        _proofReadValidated = scenario == PlanningBenchmarkCases.ReadGroupProofCase;
    }

    internal void RequireProofWorkspace(string workspace)
    {
        if (!_proofReadValidated || Path.GetFullPath(workspace) != Path.GetFullPath(root))
            throw new PlanningConflictException("The seed-read runtime is outside its reviewed isolated workspace.");
        RequirePath("seed.txt", "seed.txt", exact: true);
    }

    internal PlanningCommand Command() => new() { Kind = "approve", ExpectedRevision = Receipt.ReviewedRevision,
        ArtifactHash = Receipt.ArtifactHash, ReviewedRequirementIds = [.. Receipt.ReviewedRequirementIds], ApprovalReviewId = Receipt.Id };

    internal async Task PinAsync(BenchmarkCampaign owner, CancellationToken ct)
    {
        await Pin("authorization", JsonSerializer.SerializeToNode(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization)!.AsObject());
        await Pin("review:" + Receipt.Id, JsonSerializer.SerializeToNode(document, DelegatedTestJsonContext.Default.DelegatedTestReviewDocument)!.AsObject());
        async Task Pin(string suffix, JsonObject value)
        {
            var key = run + ":" + suffix;
            var retained = await owner.LoadAsync("planning-evaluation-delegated-reviews", key, ct);
            if (retained is not null && !JsonNode.DeepEquals(retained, value))
                throw new PlanningConflictException("A retained test authorization or review cannot be replaced.");
            if (retained is null) await owner.SaveAsync("planning-evaluation-delegated-reviews", key, value, ct);
        }
    }

    internal static bool AllowsTool(string scenario, string server, string tool) => scenario switch
    {
        "amazon" => server == "GnOuGo.Browser.Mcp" && tool is "browser_get_content" or "browser_close" ||
            server == "GnOuGo.Document.Mcp" && tool == "document_write",
        "code" => server == "GnOuGo.Git.Mcp" && tool is "git_clone" or "git_repository_info" or "git_status" or "git_diff" or "git_compare_refs" or "git_log" or "git_branches" or "git_checkout" or "git_fetch" ||
            server == "GnOuGo.GithubCopilot.Mcp" && tool is "code_project_summary" or "code_read_file" or "code_search_text" or "code_agent_edit" ||
            server == "GnOuGo.Cmd.Mcp" && tool == "cmd_run" || server == "GnOuGo.Document.Mcp" && tool == "document_write",
        "synthetic" => false, // Synthetic cohorts use local fixtures; no external producer is authorized here.
        PlanningBenchmarkCases.ReadGroupProofCase => server == "filesystem" && tool == "cmd_run",
        _ => false
    };

    internal void RequireTool(string server, string tool, JsonNode? arguments, bool inspectOnly = false)
    {
        if (!AllowsTool(scenario, server, tool)) throw new PlanningConflictException("The operation is outside delegated isolated-test authority.");
        if (inspectOnly) return;
        if (arguments is not JsonObject args) throw new PlanningConflictException("Concrete operation arguments are required.");
        if (scenario == PlanningBenchmarkCases.ReadGroupProofCase)
        {
            if (!_proofReadValidated || args.Count != 2 || args["commandName"]?.ToString() != "cat_file" ||
                args["parameters"] is not JsonObject parameters || parameters.Count != 1 || parameters["path"]?.ToString() != "seed.txt")
                throw new PlanningConflictException("Only the reviewed literal seed read is delegated; no other command, argument or path is permitted.");
            RequirePath("seed.txt", "seed.txt", exact: true);
            return;
        }
        var relative = "workflows/" + campaign + "/" + run;
        if (tool == "document_write") RequirePath(args["filePath"]?.ToString(), relative + (scenario == "amazon" ? "/products.xlsx" : "/review.json"), exact: true);
        if (tool == "browser_close") ClearBrowser();
        if (tool == "browser_get_content")
        {
            if (args["url"] is { } url)
            {
                if (!AmazonUrl(url.ToString()) || args["cursor"] is not null)
                    throw new PlanningConflictException("Only authorized product/search reads are permitted.");
                ClearBrowser(); // New navigation invalidates every previously issued cursor before transport.
            }
            else
            {
                lock (_browserCursors)
                    if (_browserUrl is null || args["cursor"] is not { } cursor ||
                        !_browserCursors.TryGetValue(cursor.ToString(), out var snapshot) || snapshot != _browserSnapshot ||
                        args["format"]?.ToString() is not ("observation" or "observation_pages") ||
                        args.Any(p => p.Key is not ("format" or "cursor") && p.Value is not null))
                        throw new PlanningConflictException("A URL-less read requires an exact cursor issued by the current authorized Amazon observation.");
            }
        }
        if (scenario == "code")
        {
            if (tool == "git_clone")
            {
                if (args["remoteUrl"]?.ToString() is not ("https://github.com/AxaFrance/SmartGuide" or "https://github.com/AxaFrance/SmartGuide.git") ||
                    args["branch"]?.ToString() != LiveWorkflowEvaluation.Head)
                    throw new PlanningConflictException("The repository is not the frozen review target.");
                RequirePath(args["targetDirectory"]?.ToString(), relative + "/repository", exact: true);
            }
            else if (tool == "cmd_run")
            {
                if (args["commandName"]?.ToString() != "delete_directory") throw new PlanningConflictException("Only confined clone cleanup is delegated to Cmd.");
                RequirePath(args["parameters"]?["path"]?.ToString(), relative + "/repository", exact: true);
            }
            else if (tool != "document_write") RequirePath(args["projectRoot"]?.ToString(), relative + "/repository", exact: true);
            if (tool == "git_checkout" && (args["branchOrCommit"]?.ToString() != LiveWorkflowEvaluation.Head || args["createBranch"]?.GetValue<bool>() == true))
                throw new PlanningConflictException("Checkout must identify the frozen review head.");
            if (tool == "git_fetch" && args["remoteName"]?.ToString() is not (null or "origin"))
                throw new PlanningConflictException("Fetch must use the frozen clone's origin remote.");
        }
    }

    internal void ObserveTool(string server, string tool, JsonNode? arguments, McpCallResult? result)
    {
        if (server != "GnOuGo.Browser.Mcp" || tool is not ("browser_get_content" or "browser_close")) return;
        if (tool == "browser_close" || result is null || result.IsError || result.Content?["success"]?.GetValue<bool>() != true)
        { ClearBrowser(); return; }
        var content = result.Content!;
        var url = content["url"]?.ToString();
        var snapshot = content["observationManifest"]?["id"]?.ToString() ?? content["observation"]?["id"]?.ToString();
        lock (_browserCursors)
        {
            if (url is null || !AmazonUrl(url) || content["acquisition"]?["navigation"]?["url"] is { } final && final.ToString() != url ||
                arguments?["url"] is null && (url != _browserUrl || snapshot != _browserSnapshot))
            { ClearBrowser(); throw new PlanningConflictException("Browser producer evidence left the authorized URL or observation generation."); }
            _browserUrl = url; _browserSnapshot = snapshot;
            if (string.IsNullOrWhiteSpace(snapshot)) return;
            if (content["observationManifest"]?["pages"] is JsonArray pages)
                foreach (var page in pages)
                    if (page?["cursor"]?.ToString() is { Length: > 0 } cursor) _browserCursors[cursor] = snapshot;
            if (content["observation"]?["nextCursor"]?.ToString() is { Length: > 0 } next) _browserCursors[next] = snapshot;
        }
    }

    private void ClearBrowser() { lock (_browserCursors) { _browserCursors.Clear(); _browserUrl = null; _browserSnapshot = null; } }
    private static bool AmazonUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme != "https" || address.UserInfo.Length != 0 ||
            address.Host is not ("www.amazon.fr" or "amazon.fr")) return false;
        var path = address.AbsolutePath; var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path == "/s" || path.StartsWith("/dp/", StringComparison.Ordinal) || path.StartsWith("/gp/product/", StringComparison.Ordinal) ||
            parts.Length >= 3 && parts[1] == "dp" && parts[0] != "gp";
    }

    internal bool RequireRuntimeRequest(HumanInputRequest request)
    {
        if (_confirmationStep is not null && request.RunId == run && request.StepId == _confirmationStep &&
            request.ParentInvocationId is null && request.Mode == HumanInputContract.ModeConfirm && request.Prompt == _confirmationPrompt &&
            request.Context is null && request.Fields is null && request.AllowAbandon &&
            request.Choices is { Count: 2 } choices && choices.SequenceEqual(["approve", "reject"]))
            return true; // The unmodified host-owned gate for this reviewed artifact and single execution.
        if (request.RunId != run || scenario != "code" || string.IsNullOrWhiteSpace(request.ParentInvocationId) ||
            !_runtimeInvocationSuffixes.Any(s => request.ParentInvocationId.EndsWith(s, StringComparison.Ordinal)) ||
            !request.StepId.StartsWith(request.ParentInvocationId + "/human/", StringComparison.Ordinal) ||
            request.StepId.Length <= request.ParentInvocationId.Length + "/human/".Length || !Active(request) ||
            request.Context?["mcp_server"]?.ToString() != "GnOuGo.GithubCopilot.Mcp" ||
            request.Context?["mcp_method"]?.ToString() != "code_agent_edit" ||
            request.Fields is not { Count: 1 } fields || fields[0].Name != "answer" ||
            fields[0].Options is not { } options || !options.Contains("Allow once", StringComparer.Ordinal) ||
            !options.Contains("Refuse", StringComparer.Ordinal) || string.IsNullOrWhiteSpace(fields[0].Description) ||
            fields[0].Description!.StartsWith("MCP tool:", StringComparison.Ordinal) ||
            fields[0].Description!.StartsWith("Custom tool:", StringComparison.Ordinal) ||
            fields[0].Description!.StartsWith("Permission kind:", StringComparison.Ordinal) ||
            fields[0].Description!.Contains("Sandbox bypass requested: yes", StringComparison.OrdinalIgnoreCase))
            throw new PlanningConflictException("This runtime request is outside the delegated one-operation review boundary.");
        return false;

        bool Active(HumanInputRequest input)
        {
            lock (_activeCalls)
                return _activeCalls.Count == 1 && _activeCalls.TryGetValue(input.ParentInvocationId!, out var call) &&
                    call.TenantId == "benchmark" && call.RunId == input.RunId &&
                    call.ServerName == input.Context?["mcp_server"]?.ToString() && call.MethodName == input.Context?["mcp_method"]?.ToString();
        }
    }

    internal sealed class Input(BenchmarkCampaign campaign, string run, DelegatedTestReviewAuthority authority) : IHumanInputProvider
    {
        public async Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            var artifactGate = authority.RequireRuntimeRequest(request);
            var payload = HumanInputContract.BuildRequestPayload(request);
            var hash = PlanningGraphCompiler.Fingerprint(payload.ToJsonString());
            var key = run + ":delegated-dialog:" + hash;
            var record = new JsonObject { ["origin"] = "delegated_test", ["reviewer"] = authority.Receipt.Reviewer,
                ["authorization_hash"] = authority.AuthorizationHash, ["artifact_hash"] = authority.Receipt.ArtifactHash,
                ["request_hash"] = hash, ["request"] = payload };
            if (await campaign.LoadAsync("planning-evaluation-dialogs", key, ct) is not null)
                throw new PlanningConflictException("A delegated runtime response cannot be replayed after interruption.");
            await campaign.SaveAsync("planning-evaluation-dialogs", key, record, ct);
            Console.WriteLine(artifactGate
                ? "Delegated isolated-test reviewer: confirm this exact reviewed artifact for one execution; respond with a Boolean response."
                : "Delegated isolated-test reviewer: inspect this exact operation and its destination; only Allow once or Refuse is accepted.");
            Console.WriteLine(payload.ToJsonString());
            var response = await LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload, Console.In, Console.Out, ct);
            if (artifactGate ? response?["response"] is not JsonValue value || !value.TryGetValue<bool>(out _) :
                response?["answer"]?.ToString() is not ("Allow once" or "Refuse"))
                throw new PlanningConflictException("Delegation never grants reusable, broad or sandbox-bypass permission.");
            record["answer"] = response.DeepClone();
            await campaign.SaveAsync("planning-evaluation-dialogs", key, record, CancellationToken.None);
            return response;
        }
    }

    private void RequirePath(string? path, string expected, bool exact)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new PlanningConflictException("A confined destination is required.");
        var actual = Path.GetFullPath(path, root); var target = Path.GetFullPath(expected, root);
        if (actual != target && (exact || !actual.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new PlanningConflictException("The operation destination is outside this isolated test.");
        for (var current = new DirectoryInfo(Path.GetDirectoryName(actual)!); current is not null && current.FullName.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal); current = current.Parent)
            if (current.LinkTarget is not null) throw new PlanningConflictException("A symbolic-link destination cannot establish confinement.");
        if (File.Exists(actual) && new FileInfo(actual).LinkTarget is not null || Directory.Exists(actual) && new DirectoryInfo(actual).LinkTarget is not null)
            throw new PlanningConflictException("A symbolic-link destination cannot establish confinement.");
    }

    private static IEnumerable<PlanTask> Tasks(TaskPlan plan) => Scope(plan.Root).Concat(plan.Groups.SelectMany(g => Scope(g.Body)));
    private static IEnumerable<PlanTask> Scope(TaskScope scope) => scope.Tasks.Concat(scope.Always).SelectMany(t => new[] { t }
        .Concat(t.Body is null ? [] : Scope(t.Body)).Concat(t.Otherwise is null ? [] : Scope(t.Otherwise)).Concat(t.Branches.SelectMany(Scope)));

    internal sealed class Runtime(IPlanningRuntime inner, DelegatedTestReviewAuthority authority) : IPlanningRuntime, IPlanningApprovalReviewProvider, IPlanningConstructionAdmission
    {
        public Task AdmitConstructionAsync(LLMRequest request, int inputTokenCeiling, IReadOnlyList<int> outputTokenCeilings, CancellationToken ct)
            => inner is IPlanningConstructionAdmission admission ? admission.AdmitConstructionAsync(request, inputTokenCeiling, outputTokenCeilings, ct) : Task.CompletedTask;
        public ICapabilityCatalog Capabilities => inner.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => inner.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) => inner.CallAsync(request, purpose, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => inner.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => inner.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => inner.CheckpointAsync(session, ct);
        public Task<PlanningApprovalReview> ResolveApprovalReviewAsync(PlanningSession session, string reviewId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); authority.Validate(session);
            if (reviewId != authority.Receipt.Id) throw new PlanningConflictException("The test review identity changed.");
            return Task.FromResult(authority.Receipt);
        }
    }
}
