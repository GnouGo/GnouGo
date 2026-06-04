using System.Diagnostics;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Server.Components.Tracing;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Agent.Shared;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ChatTraceSidebarTests : TestContext
{
    [Fact]
    public async Task SwitchingToLogs_ThenClosingDuringRefresh_DoesNotThrowAndClosesPanel()
    {
        var settings = new OpenTelemetrySettings
        {
            Enabled = true,
            ServiceName = "GnOuGo.Agent.Server"
        };

        var localStore = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(settings));
        var eventBus = new TelemetryEventBus();
        var scopeFactory = Services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var traceDebug = new TraceDebugService(
            scopeFactory,
            localStore,
            eventBus,
            new TestOptionsMonitor<OpenTelemetrySettings>(settings),
            NullLogger<TraceDebugService>.Instance);

        Services.AddSingleton(traceDebug);

        using var chatActivity = new Activity("chat.message")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        chatActivity.SetTag(AgentOTelTelemetry.CorrelationIdTagName, "corr-sidebar");
        localStore.Track(chatActivity);
        localStore.Complete(chatActivity);
        var traceId = chatActivity.TraceId;
        var spanId = chatActivity.SpanId;

        var message = new ChatMessageDto(
            Role: "assistant",
            Content: "hello",
            MessageId: "msg-1",
            CorrelationId: "corr-sidebar",
            TraceId: traceId.ToHexString());

        var closed = false;
        var cut = RenderComponent<ChatTraceSidebar>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsOpen, true)
            .Add(p => p.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        cut.WaitForAssertion(() => Assert.Contains("Live trace for this answer", cut.Markup));

        var logsTab = cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Logs", StringComparison.Ordinal));
        logsTab.Click();

        cut.WaitForAssertion(() => Assert.Contains("Live logs for this answer", cut.Markup));

        var refreshTask = Task.Run(async () =>
        {
            await Task.Delay(25);

            using var workflowActivity = new Activity("workflow.execute")
                .SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(traceId, spanId, ActivityTraceFlags.Recorded)
                .Start();
            workflowActivity.SetTag(AgentOTelTelemetry.CorrelationIdTagName, "corr-sidebar");
            localStore.Track(workflowActivity);
            localStore.Complete(workflowActivity);
            eventBus.NotifyFlushed(spanCount: 1, logCount: 0);
        });

        var closeButton = cut.Find("button[aria-label='Close trace panel']");
        closeButton.Click();

        Assert.True(closed);

        cut.SetParametersAndRender(parameters => parameters
            .Add(p => p.Message, null)
            .Add(p => p.IsOpen, false)
            .Add(p => p.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        await refreshTask;
        await Task.Delay(150);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Select a GnOuGo message", cut.Markup);
            Assert.DoesNotContain("Live logs for this answer", cut.Markup);
        });
    }
}



