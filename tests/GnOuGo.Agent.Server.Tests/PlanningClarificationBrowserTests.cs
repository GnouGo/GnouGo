using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server;
using GnOuGo.Agent.Server.Components;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningClarificationBrowserTests
{
    [Fact]
    public async Task DesignerBrowserUsesRealPersistedQuestionsAndExplicitSubmission()
    {
        var module = Environment.GetEnvironmentVariable("PLAYWRIGHT_MODULE_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(module), "Set PLAYWRIGHT_MODULE_PATH to run the isolated deterministic browser smoke.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "GnOuGo.Agent.Server"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); var model = new Model();
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model);
        var cases = new JsonObject();
        foreach (var name in new[] { "recommended", "custom", "text", "cancel", "recovery" })
        {
            var state = new PlanningSession { Request = new() { TenantId = "planning-tests", Name = "Browser " + name,
                // Match the lifecycle fixture's pricing metadata; all dispatches use the injected Model below.
                Prompt = name == "text" ? "A missing fact" : "An ambiguous caller interface", Options = new() { ["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" } } } };
            PlanningSession? issued = null;
            var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model }, (checkpoint, _) =>
            { if (checkpoint.PendingCall is not null) issued = JsonSerializer.SerializeToNode(checkpoint, PlanningJsonContext.Default.PlanningSession)!.Deserialize(PlanningJsonContext.Default.PlanningSession); return Task.CompletedTask; });
            var planner = new HybridWorkflowPlanner();
            for (var advance = 0; advance < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
                state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, ct);
            Assert.Equal(PlanningStatus.Clarification, state.Status);
            if (name == "recovery")
            {
                state = issued!; state.Status = PlanningStatus.Stopped;
                state.Diagnostics = [new("LLM_BUDGET_UNVERIFIABLE", "/", "Retained interruption")];
                var key = state.Request.SessionId + ":" + state.PendingCall!.Id;
                await using (var db = fixture.CreateDbContext())
                {
                    db.Calls.Add(new() { TenantId = "planning-tests", SessionId = state.Request.SessionId, RequestHash = state.PendingCall.Id, PayloadKey = key });
                    await db.SaveChangesAsync(ct);
                }
                await fixture.Records.UpsertAsync(PlanningModelJournal.RequestCollection, "planning-tests", key, JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest), EfPlanningSessionStore.Author, ct);
                await fixture.Records.UpsertAsync(PlanningModelJournal.Collection, "planning-tests", key, JsonSerializer.Serialize(model.LastResponse!, PlanningJsonContext.Default.LLMResponse), EfPlanningSessionStore.Author, ct);
                var budget = new LLMUsageBudgetScope(new() { MaxCalls = state.Request.MaxModelCalls, MaxEstimatedCost = new(5, "EUR") },
                    sink: new PlanningBudgetSink(fixture.Records, "planning-tests", state.Request.SessionId), exchangeRateProvider: new TestExchangeRateProvider());
                foreach (var original in model.Requests.Where(request => request.ClientRequestId!.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal)))
                {
                    await budget.ReserveJournaledAttemptAsync(original.ClientRequestId + ":1", original, new RecoveryEstimator(),
                        16_000, original.MaxTokens ?? 8192, [], "planning", ct);
                    await budget.SettleJournaledAttemptAsync(original.ClientRequestId + ":1", original, new RecoveryEstimator(),
                        new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }, "planning", ct);
                }
            }
            Assert.True(await fixture.Store.TrySaveAsync(state, null, ct));
            cases[name] = state.Request.SessionId;
        }
        var serverRoot = Path.Combine(repository.FullName, "src", "GnOuGo.Agent.Server");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(App).Assembly.GetName().Name,
            ContentRootPath = serverRoot, WebRootPath = Path.Combine(serverRoot, "wwwroot"), EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.Configuration[WebHostDefaults.StaticWebAssetsKey] = Path.Combine(AppContext.BaseDirectory, "GnOuGo.Agent.Server.staticwebassets.runtime.json");
        builder.WebHost.UseStaticWebAssets(); builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders(); builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning); builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = true);
        builder.Services.AddSingleton(new AppVersionInfo()); builder.Services.AddSingleton(service); builder.Services.AddHostedService(_ => service);
        builder.Services.AddHttpClient(); builder.Services.Configure<OpenTelemetrySettings>(s => s.TenantId = "planning-tests");
        builder.Services.Configure<TraceDebugSettings>(_ => { });
        builder.Services.AddSingleton<GnOuGo.Agent.Server.Telemetry.LocalTraceDebugStore>(); builder.Services.AddSingleton<GnOuGo.Agent.Server.Telemetry.TraceDebugService>();
        await using var app = builder.Build();
        Assert.True(app.Environment.WebRootFileProvider.GetFileInfo("_framework/blazor.web.js").Exists, "Load the host's static assets in the isolated browser test.");
        app.UseDeveloperExceptionPage(); app.UseStaticFiles(); app.UseAntiforgery();
        app.MapStaticAssets(Path.Combine(AppContext.BaseDirectory, "GnOuGo.Agent.Server.staticwebassets.endpoints.json"));
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); await app.StartAsync(ct);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repository.FullName };
        start.ArgumentList.Add(Path.Combine(repository.FullName, "scripts", "smoke-planning-clarification.mjs")); start.ArgumentList.Add(app.Urls.Single()); start.ArgumentList.Add(cases.ToJsonString());
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(90), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
        foreach (var (name, id) in cases)
        {
            var state = (await service.GetAsync(id!.ToString(), ct))!;
            Assert.Equal(name == "cancel" ? PlanningStatus.Cancelled : PlanningStatus.FinalReview, state.Status);
            Assert.Null(state.ApprovedHash); Assert.Equal(name == "cancel" ? 2 : 7, state.ModelCalls);
            if (name != "cancel")
            {
                Assert.True(state.Construction!.Complete);
                Assert.Single(state.Construction.Checkpoints);
                Assert.Single(state.Construction.Checkpoints[0].Reviews);
                PlanningArtifactApproval.Verify(state);
            }
        }
        Assert.Equal(30, model.Calls); // Five classifications/questions plus four complete current continuations; receipt replay dispatches nothing.
    }

    private sealed class RecoveryEstimator : IModelUsageCostEstimator
    { public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0; }

    private sealed class Model : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls;
        internal LLMResponse? LastResponse;
        internal List<LLMRequest> Requests { get; } = [];
        private readonly Dictionary<string, CurrentPlanningFixture.LiteralModel> _scripts = new(StringComparer.Ordinal);
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        private JournaledPlanningModel? _transport;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) =>
            (_transport ??= new JournaledPlanningModel(new Script(this))).CallAsync(request, ct);
        private async Task<LLMResponse> RespondAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Requests.Add(request);
            var session = request.ClientRequestId!.Split(':')[0];
            if (!_scripts.TryGetValue(session, out var script))
            {
                var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
                var text = context["sources"]![0]!["text"]!.ToString();
                IReadOnlyList<PlanningQuestion> questions = text == "A missing fact"
                    ? [new("resource", "Which existing resource should be used?", [], null)]
                    : [new("interface", "Which caller interface should this workflow expose?", [new("compact", "Reference and checklist: derive technical details"), new("explicit", "Separate coordinates: caller supplies technical details")], "compact")];
                _scripts[session] = script = new(questions: questions);
            }
            return LastResponse = await script.CallAsync(request, ct);
        }
        private sealed class Script(Model owner) : ILLMClient
        { public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => owner.RespondAsync(request, ct); }
    }
}
