using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server;
using GnOuGo.Agent.Server.Components;
using GnOuGo.Agent.Server.Configuration;
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
        foreach (var name in new[] { "recommended", "custom", "text", "cancel" })
        {
            var state = new PlanningSession { Request = new() { TenantId = "planning-tests", Name = "Browser " + name,
                // Match the lifecycle fixture's pricing metadata; all dispatches use the injected Model below.
                Prompt = name == "text" ? "A missing fact" : "An ambiguous caller interface", Options = new() { ["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" } } } };
            var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model }, (_, _) => Task.CompletedTask);
            state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, ct);
            Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.True(await fixture.Store.TrySaveAsync(state, null, ct));
            cases[name] = state.Request.SessionId;
        }
        var serverRoot = Path.Combine(repository.FullName, "src", "GnOuGo.Agent.Server");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(App).Assembly.GetName().Name,
            ContentRootPath = serverRoot, WebRootPath = Path.Combine(serverRoot, "wwwroot"), EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.Configuration[WebHostDefaults.StaticWebAssetsKey] = Path.Combine(AppContext.BaseDirectory, "GnOuGo.Agent.Server.staticwebassets.runtime.json");
        builder.WebHost.UseStaticWebAssets(); builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders(); builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning); builder.Services.AddRazorComponents().AddInteractiveServerComponents();
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
            Assert.Null(state.ApprovedHash); Assert.Equal(name == "cancel" ? 1 : 2, state.ModelCalls);
        }
        Assert.Equal(7, model.Calls); // Four initial questions, three explicit continuations.
    }

    private sealed class Model : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var start = request.Prompt.IndexOf("\n{", StringComparison.Ordinal); var context = JsonNode.Parse(request.Prompt[(start + 1)..])!;
            var proposal = context["userAnswers"] is null
                ? new PlanningProposal { Clarifications = context["request"]!.ToString() == "A missing fact"
                    ? [new("resource", "Which existing resource should be used?", [], null)]
                    : [new("interface", "Which caller interface should this workflow expose?", [new("compact", "Reference and checklist: derive technical details"), new("explicit", "Separate coordinates: caller supplies technical details")], "compact")] }
                : new PlanningProposal { Requirements = PlanningCorpus.Requirements("local"), Plan = PlanningCorpus.LiteralResult() };
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal),
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()), Usage = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 30, ["total_tokens"] = 50 } });
        }
    }
}
