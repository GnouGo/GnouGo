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

public sealed class ChatTraceSidebarTests : BunitContext
{
    [Fact]
    public void Logs_RenderDetailsLazily_AndKeepParentActionsClickable()
    {
        var logs = new List<TraceLogDto>
        {
            new(
                ReceivedUtc: DateTimeOffset.Parse("2026-06-06T10:00:00+00:00"),
                TraceId: "11111111111111111111111111111111",
                SpanId: "2222222222222222",
                SeverityNumber: 9,
                SeverityText: "Information",
                Body: "{\"large\":\"payload\"}",
                ServiceName: "GnOuGo.Agent.Server",
                Attributes: new Dictionary<string, object?> { ["large"] = "{\"nested\":true}" },
                Resource: new Dictionary<string, object?> { ["service.name"] = "GnOuGo.Agent.Server" },
                Scope: new Dictionary<string, object?> { ["name"] = "tests" })
        };

        var cut = Render<ChatTraceLogs>(parameters => parameters
            .Add(p => p.Logs, logs)
            .Add(p => p.RenderFormatted, true));

        Assert.DoesNotContain("Log identifiers", cut.Markup);

        cut.Find("button.gnougo-trace-log__summary").Click();

        cut.WaitForAssertion(() => Assert.Contains("Log identifiers", cut.Markup));

        cut.Find("button.gnougo-trace-log__summary").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Log identifiers", cut.Markup));
    }

    [Fact]
    public void Logs_RenderDuplicateTelemetryRows_WithoutDuplicateBlazorKeys()
    {
        var receivedUtc = DateTimeOffset.Parse("2026-06-06T10:00:00+00:00");
        var logs = new List<TraceLogDto>
        {
            CreateDuplicateLog(receivedUtc),
            CreateDuplicateLog(receivedUtc)
        };

        var cut = Render<ChatTraceLogs>(parameters => parameters
            .Add(p => p.Logs, logs)
            .Add(p => p.RenderFormatted, true));

        Assert.Equal(2, cut.FindAll("section.gnougo-trace-log").Count);

        var buttons = cut.FindAll("button.gnougo-trace-log__summary");
        buttons[0].Click();

        cut.WaitForAssertion(() => Assert.Contains("Log identifiers", cut.Markup));
    }

    [Fact]
    public async Task SwitchingToLogs_ThenClosingDuringRefresh_DoesNotThrowAndClosesPanel()
    {
        var settings = new OpenTelemetrySettings
        {
            Enabled = true,
            ServiceName = "GnOuGo.Agent.Server"
        };

        var localStore = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(settings));
        var scopeFactory = Services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var traceDebug = new TraceDebugService(
            new StaticHttpClientFactory("event: init\ndata: []\n\n"),
            scopeFactory,
            localStore,
            new TestOptionsMonitor<TraceDebugSettings>(new TraceDebugSettings
            {
                BaseUrl = "http://localhost:4318",
                ServiceName = settings.ServiceName
            }),
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
        var cut = Render<ChatTraceSidebar>(parameters => parameters
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
        });

        var closeButton = cut.Find("button[aria-label='Close trace panel']");
        closeButton.Click();

        Assert.True(closed);

        cut.Render(parameters => parameters
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

    private sealed class StaticHttpClientFactory(string streamContent) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticSseHandler(streamContent))
            {
                BaseAddress = new Uri("http://localhost:4318"),
                Timeout = Timeout.InfiniteTimeSpan
            };
    }

    private static TraceLogDto CreateDuplicateLog(DateTimeOffset receivedUtc)
        => new(
            ReceivedUtc: receivedUtc,
            TraceId: "11111111111111111111111111111111",
            SpanId: "2222222222222222",
            SeverityNumber: 9,
            SeverityText: "Information",
            Body: "repeated log message",
            ServiceName: "GnOuGo.Agent.Server",
            Attributes: new Dictionary<string, object?>(),
            Resource: new Dictionary<string, object?>(),
            Scope: new Dictionary<string, object?>());

    private sealed class StaticSseHandler(string streamContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(streamContent)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
