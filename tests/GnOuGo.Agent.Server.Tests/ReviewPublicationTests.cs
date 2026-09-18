using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Reviews;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ReviewPublicationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Head = new('b', 40);
    private static ReviewDraftRequest Request() => new("owner", "repository", 1, new(
        new(new string('a', 40), Head, [], new(1, 1, 0, 0, [], []), [], "No findings") { Complete = true },
        Path.GetFullPath("workspace"), [new("Unit tests", true, ExpectedArgumentsJson: "{\"command\":\"test\"}")],
        [new("Unit tests", ReviewCheckStatus.Passed, "Observed process result", true,
            new("tool-1", null, "terminal", "{\"command\":\"test\"}", true, true, false, [new(Path.GetFullPath("workspace"), 0, "passed")], null))]));
    private static ReviewPublicationService Service(IKeyVaultRecordStore records, string tenant = "test") => new(records, new(), Options.Create(new ReviewPublicationSettings()), Options.Create(new OpenTelemetrySettings { TenantId = tenant }));
    private static async Task Capture(ReviewPublicationService service, ReviewDraftRequest request, string run)
    {
        await service.CaptureAsync(run, "copilot_review", JsonSerializer.SerializeToNode(request.Evaluation.Review, CopilotCoreJsonContext.Default.CopilotReviewResult), Ct);
        var result = new CopilotSendResult("handle", "session", "claimed success", "model", []) { ToolExecutions = request.Evaluation.Checks.Where(c => c.Execution is not null).Select(c => c.Execution!).ToArray() };
        await service.CaptureAsync(run, "copilot_one_shot", JsonSerializer.SerializeToNode(result, CopilotCoreJsonContext.Default.CopilotSendResult), Ct);
    }

    [Fact]
    public async Task PublicationShowsExactDraftThenReadsHeadThenWritesOnceAndReplaysAfterRestart()
    {
        var records = new Records(); var service = Service(records); var run = Guid.NewGuid().ToString(); var request = Request();
        await Capture(service, request, run); var draft = await service.EvaluateAsync(run, request, Ct);
        var order = new List<string>();
        var github = new FakeMcpSession("github").OnTool("pull_request_read", (_, _) => { order.Add("head"); return Task.FromResult(new McpCallResult { Content = new JsonObject { ["head"] = new JsonObject { ["sha"] = Head } } }); })
            .OnTool("pull_request_review_write", (args, _) =>
            {
                order.Add("write"); Assert.Equal("APPROVE", args!["event"]!.ToString()); Assert.Equal(Head, args["commitID"]!.ToString());
                Assert.Equal(draft.Evaluation.Body, args["body"]!.ToString()); return Task.FromResult(new McpCallResult { Content = new JsonObject { ["id"] = 42 } });
            });
        var confirmation = new Confirm((prompt, _) => { order.Add("confirm"); Assert.Contains(draft.Evaluation.Body, prompt.Prompt); return Task.FromResult<JsonNode?>(JsonValue.Create(true)); });
        Assert.Equal("published", (await service.PublishAsync(run, "publish", draft.DraftId, github, Ct, confirmation)).Status);
        Assert.Equal(new[] { "confirm", "head", "write" }, order);
        var restarted = Service(records);
        Assert.Equal("published", (await restarted.PublishAsync(run, "publish", draft.DraftId, github, Ct, confirmation)).Status);
        Assert.Equal(3, order.Count);
        Assert.Equal(draft.DraftId, (await restarted.EvaluateAsync(run, request, Ct)).DraftId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task RejectionAndAbandonmentPreventAnyGitHubCall(bool? accepted)
    {
        var service = Service(new Records()); var run = Guid.NewGuid().ToString(); var request = Request();
        await Capture(service, request, run); var draft = await service.EvaluateAsync(run, request, Ct);
        var result = await service.PublishAsync(run, "publish", draft.DraftId, new FakeMcpSession("github"), Ct,
            new Confirm((_, _) => Task.FromResult<JsonNode?>(accepted is null ? null : JsonValue.Create(accepted.Value))));
        Assert.Equal("rejected", result.Status);
    }

    [Fact]
    public async Task HeadChangingWhileHumanReviewsPreventsPublication()
    {
        var service = Service(new Records()); var run = Guid.NewGuid().ToString(); var request = Request();
        await Capture(service, request, run); var draft = await service.EvaluateAsync(run, request, Ct);
        var current = Head;
        var github = new FakeMcpSession("github").OnTool("pull_request_read", (_, _) => Task.FromResult(new McpCallResult { Content = new JsonObject { ["head"] = new JsonObject { ["sha"] = current } } }));
        var result = await service.PublishAsync(run, "publish", draft.DraftId, github, Ct, new Confirm((_, _) =>
        { current = new('c', 40); return Task.FromResult<JsonNode?>(JsonValue.Create(true)); }));
        Assert.Equal("stale", result.Status);
    }

    [Fact]
    public async Task LostWriteResponseStopsWithoutDuplicateOnRestart()
    {
        var records = new Records(); var service = Service(records); var run = Guid.NewGuid().ToString(); var request = Request();
        await Capture(service, request, run); var draft = await service.EvaluateAsync(run, request, Ct);
        var writes = 0;
        var github = new FakeMcpSession("github").OnTool("pull_request_read", new JsonObject { ["head"] = new JsonObject { ["sha"] = Head } })
            .OnTool("pull_request_review_write", (_, _) => { writes++; throw new IOException("Lost response after dispatch"); });
        await Assert.ThrowsAsync<IOException>(() => service.PublishAsync(run, "publish", draft.DraftId, github, Ct, new Confirm()));
        var replay = await Service(records).PublishAsync(run, "publish", draft.DraftId, github, Ct, new Confirm());
        Assert.Equal("dispatching", replay.Status); Assert.Equal(1, writes);
    }

    [Fact]
    public async Task CancellationBeforeDispatchCanResumeButRequiresFreshConfirmation()
    {
        var service = Service(new Records()); var run = Guid.NewGuid().ToString(); var request = Request();
        await Capture(service, request, run); var draft = await service.EvaluateAsync(run, request, Ct);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PublishAsync(run, "publish", draft.DraftId, new FakeMcpSession("github"), cancelled.Token,
            new Confirm((_, _) => { cancelled.Cancel(); return Task.FromResult<JsonNode?>(JsonValue.Create(true)); })));
        Assert.Equal("rejected", (await service.PublishAsync(run, "publish", draft.DraftId, new FakeMcpSession("github"), Ct,
            new Confirm((_, _) => Task.FromResult<JsonNode?>(JsonValue.Create(false))))).Status);
    }

    [Fact]
    public async Task ForgedEvidenceForeignTenantAndAnotherRunAreRejected()
    {
        var records = new Records(); var service = Service(records); var run = Guid.NewGuid().ToString(); var request = Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAsync(run, request, Ct));
        await Capture(service, request, run);
        var altered = request with { Evaluation = request.Evaluation with { Review = request.Evaluation.Review with { Summary = "altered" } } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAsync(run, altered, Ct));
        var check = request.Evaluation.Checks[0];
        altered = request with { Evaluation = request.Evaluation with { Checks = [check with { Execution = check.Execution! with { ToolSucceeded = false } }] } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAsync(run, altered, Ct));
        var draft = await service.EvaluateAsync(run, request, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(records, "another").PublishAsync(run, "publish", draft.DraftId, new FakeMcpSession("github"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync("another-run", "publish", draft.DraftId, new FakeMcpSession("github"), Ct));
    }

    [Fact]
    public async Task RuntimeAdvertisesReviewOperationsAndRejectsRawWriteAndBodyOverrides()
    {
        var service = Service(new Records());
        var upstream = new FakeMcpSession("github").OnTool("pull_request_review_write", new JsonObject())
            .OnTool("unrecognized_future_write", new JsonObject()).OnTool("pull_request_read", new JsonObject { ["read"] = true });
        (await upstream.ListToolsAsync(Ct)).Single(t => t.Name == "pull_request_read").EffectKind = "read";
        await using var factory = new ReviewMcpClientFactory(new FakeMcpClientFactory(upstream), service);
        Assert.Contains(factory.ServerMetadata, s => s.Name == ReviewMcpClientFactory.Server);
        using var scope = factory.BeginCall(new(new() { TenantId = "test", RunId = "run", StepId = "publish" }, _ => { }, _ => { }));
        await using var github = await factory.GetClientAsync("github", Ct);
        Assert.DoesNotContain(await github.ListToolsAsync(Ct), t => t.Name == "pull_request_review_write");
        await Assert.ThrowsAsync<InvalidOperationException>(() => github.CallToolAsync("pull_request_review_write", new JsonObject(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => github.CallToolAsync("unrecognized_future_write", new JsonObject(), Ct));
        Assert.True((await github.CallToolAsync("pull_request_read", null, Ct)).Content!["read"]!.GetValue<bool>());
        await using var review = await factory.GetClientAsync(ReviewMcpClientFactory.Server, Ct);
        var tools = await review.ListToolsAsync(Ct); Assert.Equal(2, tools.Count);
        foreach (var tool in tools)
        {
            Assert.Empty(PlanningContractValidation.ValidateSchema(tool.InputSchema!.AsObject()));
            Assert.Empty(PlanningContractValidation.ValidateSchema(tool.OutputSchema!.AsObject()));
            Assert.True(tool.OutputContract!.Authoritative);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => review.CallToolAsync("review_publish", new JsonObject { ["draftId"] = new string('a', 64), ["body"] = "override" }, Ct));
    }

    [Fact]
    public async Task HostToolFlowCapturesOriginalResultsAndSurfacesConfirmationThroughRuntimeSignals()
    {
        var human = new AgentHumanInputProvider(); var records = new Records();
        var service = new ReviewPublicationService(records, human, Options.Create(new ReviewPublicationSettings()), Options.Create(new OpenTelemetrySettings { TenantId = "test" }));
        var request = Request();
        var send = new CopilotSendResult("handle", "session", "summary", "model", []) { ToolExecutions = [request.Evaluation.Checks[0].Execution!] };
        var copilot = new FakeMcpSession("GnOuGo.GithubCopilot.Mcp")
            .OnTool("copilot_review", JsonSerializer.SerializeToNode(request.Evaluation.Review, CopilotCoreJsonContext.Default.CopilotReviewResult)!.AsObject())
            .OnTool("copilot_one_shot", JsonSerializer.SerializeToNode(send, CopilotCoreJsonContext.Default.CopilotSendResult)!.AsObject());
        var writes = 0;
        var github = new FakeMcpSession("github").OnTool("pull_request_read", new JsonObject { ["head"] = new JsonObject { ["sha"] = Head } })
            .OnTool("pull_request_review_write", (_, _) => { writes++; return Task.FromResult(new McpCallResult { Content = new JsonObject { ["id"] = 1 } }); });
        await using var factory = new ReviewMcpClientFactory(new FakeMcpClientFactory(copilot, github), service);
        var signals = new List<McpHumanInputSignalPhase>(); var run = Guid.NewGuid().ToString();
        using var scope = factory.BeginCall(new(new() { TenantId = "test", RunId = "ui-run", ExecutionId = run, StepId = "publish" }, _ => { }, s => signals.Add(s.Phase)));
        await using var producer = await factory.GetClientAsync(copilot.ServerName, Ct);
        await producer.CallToolAsync("copilot_review", null, Ct); await producer.CallToolAsync("copilot_one_shot", null, Ct);
        await using var review = await factory.GetClientAsync(ReviewMcpClientFactory.Server, Ct);
        var evaluated = await review.CallToolAsync("review_evaluate", JsonSerializer.SerializeToNode(request, ReviewPublicationJsonContext.Default.ReviewDraftRequest), Ct);
        var id = evaluated.Content!["draftId"]!.GetValue<string>();
        var publishing = review.CallToolAsync("review_publish", new JsonObject { ["draftId"] = id }, Ct);
        var prompt = await human.PendingRequests.ReadAsync(Ct);
        Assert.Equal("ui-run", prompt.RunId); Assert.Equal(0, writes); Assert.Equal(new[] { McpHumanInputSignalPhase.Waiting }, signals);
        Assert.Contains("Unit tests: Passed", prompt.Prompt);
        Assert.True(human.TrySubmitResponse(prompt.RunId, prompt.StepId, JsonValue.Create(true)));
        Assert.Equal("published", (await publishing).Content!["status"]!.ToString());
        Assert.Equal(1, writes); Assert.Equal(new[] { McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Resumed }, signals);
    }

    [Fact]
    public async Task ConcurrentPublicationCannotOpenAnotherConfirmationOrWrite()
    {
        var service = Service(new Records()); var run = Guid.NewGuid().ToString(); var request = Request();
        await Capture(service, request, run); var draft = await service.EvaluateAsync(run, request, Ct);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.PublishAsync(run, "first", draft.DraftId, new FakeMcpSession("github"), Ct, new Confirm((_, _) => { waiting.SetResult(); return answer.Task; }));
        await waiting.Task;
        try { await Assert.ThrowsAsync<IOException>(() => service.PublishAsync(run, "second", draft.DraftId, new FakeMcpSession("github"), Ct)); }
        finally { answer.TrySetResult(JsonValue.Create(false)); }
        Assert.Equal("rejected", (await first).Status);
    }

    private sealed class Confirm(Func<HumanInputRequest, CancellationToken, Task<JsonNode?>>? answer = null) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => answer?.Invoke(request, ct) ?? Task.FromResult<JsonNode?>(JsonValue.Create(true));
    }
    private sealed class Records : IKeyVaultRecordStore
    {
        private readonly Dictionary<(string Collection, string Tenant, string Key), KeyVaultRecordValue> _data = [];
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
            => Task.FromResult(_data.GetValueOrDefault((collection, tenantId, key)));
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
        {
            var row = new KeyVaultRecordValue(collection, tenantId, key, value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _data[(collection, tenantId, key)] = row; return Task.FromResult(row);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_data.Values.Where(v => v.Collection == collection && v.TenantId == tenantId).ToArray());
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => Task.FromResult(_data.Remove((collection, tenantId, key)));
    }
}
