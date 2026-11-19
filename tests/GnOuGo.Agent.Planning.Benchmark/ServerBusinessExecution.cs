using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// Test-host dependencies for the ordinary Server approve/save, typed composer, and SmartFlow execution.
internal sealed class ServerBusinessExecution : IPlanningApprovalReviewProvider, IMcpClientFactory, IMcpExecutionHooks,
    IMcpSourceConfigurationFingerprint, ILLMClient
{
    internal ServerLiveDiagnostics.DelegatedPlanningDiscovery Discovery { private get; set; } = null!;
    internal ILLMClient PlanningModel { private get; set; } = null!;
    internal PublishedReviewQualification? Target { get; set; }
    private LiveWorkflowEvaluation.ObservedMcp? _execution;
    private ILLMClient? _executionModel;
    private DelegatedTestReviewAuthority? _authority;
    private PlanningSession? _saving;
    private string? _savedId;
    private string? _runId;
    private string? _agentName;
    internal bool AllowsAgentCall(string tool, JsonNode? args) =>
        tool == "agent_list" && _savedId is not null ||
        tool == "agent_add" && _saving is { } state && args is JsonObject { Count: 3 } &&
        args["name"]?.ToString() == state.Request.Name && args["workflow"]?.ToString() == state.Yaml &&
        args["originalPrompt"]?.ToString() == state.Request.Prompt;

    public Task<PlanningApprovalReview> ResolveApprovalReviewAsync(PlanningSession session, string reviewId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_authority is not { } authority || authority.Receipt.Id != reviewId ||
            session.Request.SessionId != authority.Receipt.SessionId || session.Request.TenantId != authority.Receipt.TenantId ||
            session.ComputeArtifactHash() != authority.Receipt.ArtifactHash)
            throw new PlanningConflictException("The requested artifact-bound review is unavailable.");
        return Task.FromResult(authority.Receipt);
    }

    internal ValueTask DisposeAsync() => Discovery?.DisposeAsync() ?? ValueTask.CompletedTask;
    public IReadOnlyList<McpServerMetadata> ServerMetadata => Discovery.ServerMetadata;
    public string SourceConfigurationFingerprint(string serverName) => Discovery.SourceConfigurationFingerprint(serverName);
    public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) =>
        _execution is { } active && serverName != "GnOuGo.Agent.Mcp" ? active.GetClientAsync(serverName, ct) : Discovery.GetClientAsync(serverName, ct);
    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => (_executionModel ?? PlanningModel).CallAsync(request, ct);
    public IDisposable BeginCall(McpCallExecutionContext context)
    {
        if (_execution is null || context.Correlation.TenantId != "benchmark" || _savedId is null ||
            context.Correlation.AgentId != _savedId || context.Correlation.AgentName != _agentName ||
            context.Correlation.RunId is not { Length: > 0 } runId)
            throw new PlanningConflictException("Business dispatch requires the saved, reviewed Server agent.");
        BindRun(runId);
        return _execution.BeginCall(context);
    }
    private void BindRun(string runId)
    {
        var previous = Interlocked.CompareExchange(ref _runId, runId, null);
        if (previous is not null && previous != runId) throw new PlanningConflictException("This artifact already started another run.");
        _authority!.BindServerRun(runId);
    }
    public string FormatFailureDiagnostics(string serverName, Exception error) =>
        _execution?.FormatFailureDiagnostics(serverName, error) ?? error.GetType().Name;

    internal static bool ReviewInstructionsObserved(JsonArray events, string instructions) =>
        !string.IsNullOrWhiteSpace(instructions) && events.OfType<JsonObject>().Any(e =>
            e["server"]?.ToString() == "GnOuGo.GithubCopilot.Mcp" && e["tool"]?.ToString() == "code_agent_edit" &&
            e["error"]?.GetValue<bool>() == false && e["arguments"]?["task"]?.ToString()?.Contains(instructions, StringComparison.Ordinal) == true);

    internal static void RequireInputs(TaskPlan? plan, JsonObject inputs)
    {
        if (plan is null || plan.Inputs.Count != inputs.Count || plan.Inputs.Any(i => !inputs.ContainsKey(i.Name) ||
            !i.Required || i.Type.Kind != "string" || i.Type.Nullable || i.Default is not null))
            throw new PlanningConflictException("The artifact does not accept the required typed caller inputs.");
        var root = System.Text.Json.JsonSerializer.SerializeToNode(plan.Root, PlanningJsonContext.Default.TaskScope);
        foreach (var input in inputs)
        {
            if (!UsesInput(root, input.Key))
                throw new PlanningConflictException("The generated root workflow ignores caller input " + input.Key + ".");
        }
        static bool UsesInput(JsonNode? node, string name) => node switch
        {
            JsonObject obj => obj["kind"]?.ToString() == "input" && obj["source"]?.ToString() == name ||
                obj.Any(p => UsesInput(p.Value, name)),
            JsonArray list => list.Any(item => UsesInput(item, name)),
            _ => false
        };
    }

    internal async Task ExecuteAsync(WebApplication app, HttpClient client, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model,
        PlanningSession state, DelegatedTestReviewAuthority authority, JsonObject report, string workspace, string output, CancellationToken ct)
    {
        var scenario = report["scenario"]!.ToString();
        var grant = scenario == "code" ? PublishedReviewQualification.Scenario : scenario;
        var source = report["source"]!.ToString();
        var configurations = LiveWorkflowEvaluation.Configuration(model.McpServers, grant, null, "Release",
            report["working_directory"]!.ToString(), report["browser_observation_policy"]?.ToString());
        if (!JsonNode.DeepEquals(LiveWorkflowEvaluation.CaptureMcpBuilds(configurations, source), report["mcp_builds"]))
            throw new PlanningConflictException("Execution requires the original tool builds.");
        var service = app.Services.GetRequiredService<PlanningSessionService>();
        Task Save() => campaign.SaveAsync("planning-evaluation-runs", report["run"]!.ToString(), report, CancellationToken.None);
        async Task<PlanningSession> CommandAsync(PlanningCommand command)
        {
            using var response = await client.PostAsJsonAsync($"/api/planning/{state.Request.SessionId}/commands", new
            {
                command.Kind, command.ExpectedRevision, command.ArtifactHash, command.ReviewedRequirementIds, command.ApprovalReviewId
            }, ct);
            response.EnsureSuccessStatusCode();
            return await service.GetAsync(state.Request.SessionId, ct) ?? throw new PlanningConflictException("The session disappeared.");
        }
        var inputs = report["inputs"]!.AsObject();
        RequireInputs(state.Plan, inputs);
        _authority = authority;
        var approved = await CommandAsync(authority.Command());
        PlanningArtifactApproval.Verify(approved, authority.Receipt);
        if (approved.Status != PlanningStatus.Approved) throw new PlanningConflictException("Approval did not complete.");
        _saving = approved;
        PlanningSession saved;
        try { saved = await CommandAsync(new() { Kind = "save", ExpectedRevision = approved.Revision, ArtifactHash = approved.ApprovedHash }); }
        finally { _saving = null; }
        if (saved.Status != PlanningStatus.Saved || saved.SavedAgentId is null || saved.Yaml != state.Yaml)
            throw new PlanningConflictException("Saving the approved workflow did not complete.");
        _savedId = saved.SavedAgentId;
        _agentName = saved.Request.Name;
        report["saved_agent_id"] = _savedId;
        report["execution_artifact_hash"] = saved.ApprovedHash;
        report["review_origin"] = "delegated_test_reviewed";
        model.CaptureTokenPolicy(state.Request);
        await using var proxy = await CampaignInferenceProxy.StartAsync(model, state.Request.SessionId,
            report["sdk_chat_tools_reasoning_effort"]?.ToString(), report["sdk_wire_api"]?.ToString());
        if (scenario == "code")
        {
            configurations["GnOuGo.GithubCopilot.Mcp"].EnvironmentVariables!["Code__Copilot__InferenceProxyEndpoint"] = proxy.Endpoint;
            CampaignInferenceProxy.ConfigureWireApi(configurations["GnOuGo.GithubCopilot.Mcp"], model.Provider, report["sdk_wire_api"]?.ToString());
        }
        var human = new DelegatedTestReviewAuthority.Input(campaign, state.Request.SessionId, authority);
        await using var transport = new ConfiguredMcpClientFactory(configurations, human, model.Provider, model.Model);
        var events = new JsonArray();
        report["events"] = events;
        using var eventLock = new SemaphoreSlim(1);
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, async evt =>
        {
            await eventLock.WaitAsync(CancellationToken.None);
            try
            {
                events.Add(evt);
                if (scenario == "code") await LiveCodeEvidence.ObserveCheckoutAsync(workspace, report);
                await Save();
            }
            finally { eventLock.Release(); }
        }, authority, grant, async (server, tool, args, token) =>
        {
            if (server == "Github")
            {
                if (tool != "pull_request_read" && string.IsNullOrWhiteSpace(_runId))
                    throw new PlanningConflictException("A publication requires a bound Server execution.");
                if (tool == "pull_request_review_write" && args["method"]?.ToString() == "submit_pending")
                {
                    if (!ReviewInstructionsObserved(events, inputs["reviewText"]!.GetValue<string>()))
                        throw new PlanningConflictException("The requested review instructions did not reach a completed Copilot operation.");
                    var confirmation = new JsonObject { ["mode"] = "confirm", ["prompt"] = "Submit this exact review for " + inputs["pullRequestUrl"],
                        ["choices"] = new JsonArray("approve", "reject"), ["review"] = args.DeepClone(), ["head"] = Target!.Head,
                        ["inline"] = new JsonArray(events.OfType<JsonObject>().Where(e => e["tool"]?.ToString() == "add_comment_to_pending_review")
                            .Select(e => e["arguments"]!.DeepClone()).ToArray()) };
                    Console.WriteLine(confirmation.ToJsonString());
                    var answer = await LiveWorkflowEvaluation.ReadHumanAnswerAsync(confirmation, Console.In, Console.Out, token);
                    if (answer?["response"]?.GetValue<bool>() != true)
                        throw new PlanningConflictException("The concrete GitHub review was declined.");
                    await campaign.SaveAsync("planning-evaluation-dialogs", state.Request.SessionId + ":github-submit", new()
                    { ["payload"] = confirmation, ["answer"] = answer.DeepClone(), ["artifact_hash"] = saved.ApprovedHash, ["execution_id"] = _runId }, token);
                }
                await Target!.CheckWriteAsync(tool, args, events, token);
            }
        });
        foreach (var name in configurations.Keys) await (await observed.GetClientAsync(name, ct)).ListToolsAsync(ct);
        if (scenario == "code" && !proxy.Ready) throw new PlanningConflictException("SDK inference admission is unavailable.");
        report["execution_started"] = DateTimeOffset.UtcNow;
        await Save();
        proxy.ExecutionEnabled = true;
        _executionModel = new LiveWorkflowEvaluation.ExecutionModel(model, state.Request.SessionId);
        _execution = observed;
        using var browser = ServerLiveDiagnostics.StartObserver(app, report["working_directory"]!.ToString(), client.BaseAddress!.ToString(),
            state.Request.SessionId, report["run"]!.ToString(), _agentName, report["inputs"]!.AsObject());
        try
        {
            var runs = app.Services.GetRequiredService<WorkflowRunService>();
            var receiptPath = Path.Combine(output, "form-invocation.json");
            while (!File.Exists(receiptPath) && !browser.HasExited) await Task.Delay(300, ct);
            if (!File.Exists(receiptPath)) throw new PlanningConflictException("The typed chat form did not start a workflow.");
            var submit = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath, ct))!;
            var id = submit["run"]!.GetValue<string>();
            if (id.Length != 32 || !id.All(char.IsAsciiHexDigit) || submit["agent"]?.ToString() != _agentName)
                throw new PlanningConflictException("The form submission does not identify the reviewed agent.");
            BindRun(id);
            report["execution_run_id"] = id;
            WorkflowRun? journal;
            do
            {
                journal = await runs.ReadAsync(id, ct);
                if (journal is not null)
                {
                    if (journal.Limits.AgentId != _savedId || journal.WorkflowYaml != saved.Yaml ||
                        !report["inputs"]!.AsObject().All(p => JsonNode.DeepEquals(p.Value, journal.Inputs?[p.Key])))
                        throw new PlanningConflictException("The journal does not match the approved workflow and submitted form inputs.");
                    if (journal.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Cancelled or WorkflowRunStatus.NeedsReconciliation or WorkflowRunStatus.Failed) break;
                    foreach (var wait in journal.Invocations.Values.Where(i => i.Status == "waiting_for_human" && i.ParentInvocationId is null))
                    {
                        if (wait.ResolvedInput is not JsonObject input || input["fields"] is not null || input["context"] is not null)
                            throw new PlanningConflictException("The pending prompt differs from the reviewed confirmation.");
                        var request = new HumanInputRequest { RunId = id, StepId = wait.Id, Mode = input["mode"]?.ToString() ?? "",
                            Prompt = input["prompt"]?.ToString() ?? "", Choices = input["choices"]?.AsArray().Select(c => c!.ToString()).ToList(),
                            AllowAbandon = input["allow_abandon"]?.GetValue<bool>() ?? false };
                        if (!authority.RequireRuntimeRequest(request)) throw new PlanningConflictException("Unexpected confirmation.");
                        var answer = await human.RequestInputAsync(request, ct);
                        await runs.CommandAsync(id, "answer", new(journal.Revision, wait.Id, Response: answer), ct);
                    }
                }
                if (browser.HasExited && journal is null) throw new PlanningConflictException("No Server execution journal was created.");
                await Task.Delay(750, ct);
            } while (true);
            await browser.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(45), ct);
            if (browser.ExitCode != 0) throw new PlanningConflictException("The workflow form reported a browser error.");
            report["finished_utc"] = DateTimeOffset.UtcNow;
            report["execution"] = new JsonObject { ["success"] = journal.Status == WorkflowRunStatus.Completed && journal.Result?.Success == true,
                ["status"] = journal.Status, ["finalization_completed"] = journal.FinalizationCompleted };
            report["oracle"] = await LiveWorkflowOracles.VerifyAsync(scenario, workspace, report, observed, observed.ProbeBrowserClosureAsync);
            report["result"]!["execution_correct"] = report["oracle"]!["passed"]!.DeepClone();
            report["result"]!["execution_status"] = journal.Status;
            report["passed"] = report["oracle"]!["passed"]!.DeepClone();
            if (!report["passed"]!.GetValue<bool>()) Environment.ExitCode = 1;
        }
        finally
        {
            proxy.ExecutionEnabled = false;
            _execution = null;
            _executionModel = null;
            if (!browser.HasExited) browser.Kill(entireProcessTree: true);
            await Save();
        }
    }
}
