using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserActionReferenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("activate", false)]
    [InlineData("follow", true)]
    public async Task ObservedLinkCannotSatisfyControlActivation(string action, bool succeeds)
    {
        await using var fixture = new Fixture();
        var records = await fixture.Capture(); var link = records.Single(r => r.Selector == "#info");
        Assert.Contains("follow", link.Actions!); Assert.DoesNotContain("activate", link.Actions!);
        var result = await fixture.Tools.ClickTargetAsync(reference: link.Reference, requestedAction: action, cancellationToken: Ct);
        Assert.True(succeeds == result.Success, $"Expected success={succeeds}; actual={result.Success}; {result.ErrorCode}: {result.ErrorMessage}");
        Assert.Equal(link.Reference, result.Target!.Reference);
        Assert.Equal(action, result.Target.RequestedAction);
        if (!succeeds)
        {
            Assert.Equal("ACTION_MISMATCH", result.ErrorCode); Assert.False(result.TriggeredNavigation);
            Assert.Empty(await fixture.Page!.EvaluateAsync<string[]>("window.actions"));
            Assert.DoesNotContain("/help", fixture.Visits);
            var control = records.Single(r => r.Selector == "#accept");
            Assert.True((await fixture.Tools.ClickTargetAsync(reference: control.Reference, requestedAction: "activate", cancellationToken: Ct)).Success);
            Assert.Equal(new[] { "accept" }, await fixture.Page.EvaluateAsync<string[]>("window.actions"));
        }
        else { Assert.Equal("#info", result.Selector); Assert.Contains("/help", fixture.Visits); }
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("text")]
    [InlineData("fill_submit")]
    [InlineData("press")]
    public async Task NavigatingActionsWaitWithoutRepeatingInteraction(string action)
    {
        await using var fixture = new Fixture();
        var records = await fixture.Capture();
        var field = records.Single(r => r.Selector == "#query");
        if (action == "press")
        {
            var result = await fixture.Tools.PressTargetAsync("Enter", reference: field.Reference, cancellationToken: Ct);
            Assert.True(result.Success, result.ErrorCode + ": " + result.ErrorMessage);
            Assert.True(result.TriggeredNavigation);
        }
        else
        {
            var result = action switch
            {
                "selector" => await fixture.Tools.ClickTargetAsync(selector: "#info", cancellationToken: Ct),
                "text" => await fixture.Tools.ClickTextAsync("Accept choice information", cancellationToken: Ct),
                _ => await fixture.Tools.FillTargetAsync("observed query", submit: true, reference: field.Reference, cancellationToken: Ct)
            };
            Assert.True(result.Success, result.ErrorCode + ": " + result.ErrorMessage);
            Assert.True(result.TriggeredNavigation);
        }
        Assert.Equal(1, fixture.Visits.Count(p => p == (action is "selector" or "text" ? "/help" : "/submitted")));
        Assert.Equal("complete", await fixture.Page!.EvaluateAsync<string>("document.readyState"));
    }

    [Theory]
    [InlineData("#accept", "activate")]
    [InlineData("#aria", "activate")]
    [InlineData("#button-link", "activate")]
    [InlineData("#input", "fill")]
    [InlineData("#editable", "fill")]
    [InlineData("#select", "select")]
    [InlineData("#input", "press")]
    public async Task CompatibleReferenceUsesExactObservedElement(string selector, string action)
    {
        await using var fixture = new Fixture();
        var record = (await fixture.Capture()).Single(r => r.Selector == selector);
        Assert.Contains(action, record.Actions!);
        switch (action)
        {
            case "activate": Assert.True((await fixture.Tools.ClickTargetAsync(reference: record.Reference, requestedAction: action, cancellationToken: Ct)).Success); break;
            case "fill": Assert.True((await fixture.Tools.FillTargetAsync("observed input", reference: record.Reference, cancellationToken: Ct)).Success); break;
            case "press": Assert.True((await fixture.Tools.PressTargetAsync("Enter", reference: record.Reference, cancellationToken: Ct)).Success); break;
            case "select": Assert.True((await fixture.Tools.SelectTargetAsync("two", reference: record.Reference, cancellationToken: Ct)).Success); break;
        }
        Assert.NotEmpty(await fixture.Page!.EvaluateAsync<string[]>("window.actions"));
        var stale = await fixture.Tools.ClickTargetAsync(reference: record.Reference, requestedAction: "activate", cancellationToken: Ct);
        Assert.Equal("SNAPSHOT_EXPIRED", stale.ErrorCode);
    }

    [Theory]
    [InlineData("#disabled", "activate")]
    [InlineData("#readonly", "fill")]
    [InlineData("#info", "fill")]
    public async Task IncompatibleTargetsPerformNoInteraction(string selector, string action)
    {
        await using var fixture = new Fixture();
        var record = (await fixture.Capture()).Single(r => r.Selector == selector);
        var result = action == "fill"
            ? await fixture.Tools.FillTargetAsync("never entered", reference: record.Reference, cancellationToken: Ct)
            : await fixture.Tools.ClickTargetAsync(reference: record.Reference, requestedAction: action, cancellationToken: Ct);
        Assert.Equal("ACTION_MISMATCH", result.ErrorCode); Assert.Empty(await fixture.Page!.EvaluateAsync<string[]>("window.actions"));
    }

    [Theory]
    [InlineData("document.querySelector('#accept').replaceWith(document.querySelector('#accept').cloneNode(true))")]
    [InlineData("document.querySelector('#accept').remove()")]
    [InlineData("document.querySelector('#accept').textContent='Different objective'")]
    [InlineData("document.querySelector('#accept').disabled=true")]
    [InlineData("document.querySelector('#accept').setAttribute('role','link')")]
    public async Task ChangedOrReplacedElementNeverRetargets(string mutation)
    {
        await using var fixture = new Fixture();
        var record = (await fixture.Capture()).Single(r => r.Selector == "#accept");
        await fixture.Page!.EvaluateAsync(mutation);
        var result = await fixture.Tools.ClickTargetAsync(reference: record.Reference, requestedAction: "activate", cancellationToken: Ct);
        Assert.Equal("REFERENCE_CHANGED", result.ErrorCode); Assert.Empty(await fixture.Page.EvaluateAsync<string[]>("window.actions"));
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("replacement")]
    [InlineData("closure")]
    public async Task InvalidatedReferencesRetainCause(string cause)
    {
        await using var fixture = new Fixture();
        var record = (await fixture.Capture()).Single(r => r.Selector == "#accept");
        if (cause == "navigation") await fixture.Page!.ReloadAsync();
        else if (cause == "replacement") await fixture.Capture();
        else await fixture.Tools.CloseAsync(Ct);
        var result = await fixture.Tools.ClickTargetAsync(reference: record.Reference, requestedAction: "activate", cancellationToken: Ct);
        Assert.Equal("SNAPSHOT_EXPIRED", result.ErrorCode); Assert.Contains(cause, result.ErrorMessage);
    }

    [Fact]
    public async Task UnknownCrossSessionConflictingAndUndeliveredReferencesFailClosed()
    {
        await using var fixture = new Fixture();
        var record = (await fixture.Capture()).Single(r => r.Selector == "#accept");
        foreach (var reference in new[] { "invented", Guid.NewGuid().ToString("N") + ":record:0", record.Reference!.Split(':')[0] + ":record:9999" })
            Assert.Equal("INVALID_REFERENCE", (await fixture.Tools.ClickTargetAsync(reference: reference, requestedAction: "activate", cancellationToken: Ct)).ErrorCode);
        Assert.Equal("INVALID_REFERENCE", (await fixture.Tools.ClickTargetAsync(selector: "#accept", reference: record.Reference, requestedAction: "activate", cancellationToken: Ct)).ErrorCode);
        Assert.Equal("ACTION_MISMATCH", (await fixture.Tools.ClickTargetAsync(reference: record.Reference, cancellationToken: Ct)).ErrorCode);
        await using var other = new Fixture(); await other.Capture();
        Assert.Equal("INVALID_REFERENCE", (await other.Tools.ClickTargetAsync(reference: record.Reference, requestedAction: "activate", cancellationToken: Ct)).ErrorCode);
        var manifest = await fixture.Tools.GetContentAsync(format: "observation_pages", maxRecords: 1, cancellationToken: Ct);
        Assert.Equal("INVALID_REFERENCE", (await fixture.Tools.ClickTargetAsync(reference: manifest.ObservationManifest!.Id + ":record:0", requestedAction: "activate", cancellationToken: Ct)).ErrorCode);
        Assert.Empty(await fixture.Page!.EvaluateAsync<string[]>("window.actions"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;
        internal readonly PlaywrightBrowserHost Host;
        internal readonly BrowserTools Tools;
        internal IPage? Page;
        internal readonly List<string> Visits = [];
        private readonly string _origin;
        internal Fixture()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            _origin = "http://127.0.0.1:" + port; _listener.Prefixes.Add(_origin + "/"); _listener.Start();
            _serving = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var request = await _listener.GetContextAsync(); Visits.Add(request.Request.Url!.AbsolutePath);
                        request.Response.ContentType = "text/html; charset=utf-8";
                        await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""
                            <html><body><script>window.actions=[];</script>
                            <footer><a id="info" href="/help">Accept choice information</a></footer>
                            <dialog open><button id="accept" onclick="actions.push('accept')">Accept choice</button></dialog>
                            <span id="aria" role="button" onclick="actions.push('aria')">Activate</span>
                            <a id="button-link" role="button" href="#" onclick="event.preventDefault();actions.push('button-link')">Activate</a>
                            <input id="input" oninput="actions.push(this.value)" onkeydown="actions.push(event.key)">
                            <div id="editable" contenteditable="true" oninput="actions.push(this.textContent)">Editable</div>
                            <select id="select" onchange="actions.push(this.value)"><option value="one">One</option><option value="two">Two</option></select>
                            <button id="disabled" disabled>Disabled</button><input id="readonly" readonly>
                            <form action="/submitted"><input id="query" name="query"><button>Submit query</button></form>
                            </body></html>
                            """), Ct);
                        request.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            }, Ct);
            Host = new(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
            { ObservationCheckpoint = (page, _, _) => { Page = page; return Task.CompletedTask; } };
            Tools = new(Host, NullLogger<BrowserTools>.Instance);
        }
        internal async Task<BrowserObservationRecord[]> Capture()
        {
            var result = await Tools.GetContentAsync(url: Page is null ? _origin : null, format: "observation_complete", cancellationToken: Ct);
            Assert.True(result.Success, result.ErrorMessage);
            return result.ObservationSnapshot!.Pages.SelectMany(p => p.Records).ToArray();
        }
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); _listener.Close(); await _serving; }
    }
}
