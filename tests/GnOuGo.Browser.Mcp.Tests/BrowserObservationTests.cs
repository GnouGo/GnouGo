using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserObservationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("observation")]
    [InlineData("observation_pages")]
    public async Task PageInitiatedNavigationInvalidatesUnconsumedSnapshot(string format)
    {
        var ct = TestContext.Current.CancellationToken;
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using var site = new HttpListener(); site.Prefixes.Add($"http://127.0.0.1:{port}/"); site.Start();
        var origin = $"http://127.0.0.1:{port}";
        var navigate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            try
            {
                while (site.IsListening)
                {
                    var context = await site.GetContextAsync();
                    var path = context.Request.Url!.AbsolutePath;
                    if (path == "/advance") await navigate.Task.WaitAsync(ct);
                    var html = path == "/settled" ? "<html><body><main id='settled'>Fresh observation</main></body></html>"
                        : path == "/advance" ? ""
                        : "<html><body><main><p>First observation</p><p>Second observation</p></main><script>fetch('/advance').then(() => location.replace('/settled'));</script></body></html>";
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html), ct);
                    context.Response.Close();
                    if (path == "/settled") arrived.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        }, ct);
        try
        {
            await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
            var captured = await host.GetContentAsync(origin, "domcontentloaded", null, "main", format, null, false, ct, maxRecords: 1);
            var cursor = format == "observation_pages" ? captured.ObservationManifest!.Pages[0].Cursor : captured.Observation!.NextCursor!;
            Assert.False(string.IsNullOrEmpty(cursor));
            navigate.SetResult();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            // This ordinary read waits for the new document without creating a replacement snapshot.
            var current = await host.GetContentAsync(null, "load", 10000, "#settled", "text", null, false, ct);
            Assert.Equal("Fresh observation", current.Content);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetContentAsync(null, "load", null, null, format, null, false, ct, cursor));
            var fresh = await host.GetContentAsync(null, "load", null, "#settled", "observation", null, false, ct);
            Assert.False(fresh.Truncated);
            Assert.Equal("Fresh observation", Assert.Single(fresh.Observation!.Records).Text);
            await host.CloseAsync(ct);
        }
        finally { navigate.TrySetResult(); site.Stop(); await serve; }
    }

    [Fact]
    public async Task CompactSnapshotPreservesObservedGroupsAndLinksWithBoundedContinuation()
    {
        var ct = TestContext.Current.CancellationToken;
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using var site = new HttpListener(); site.Prefixes.Add($"http://127.0.0.1:{port}/"); site.Start();
        var origin = $"http://127.0.0.1:{port}";
        var html = "<html><head><title>Observed catalogue</title><style>p { color: black }</style></head><body><main>" +
            string.Concat(Enumerable.Range(0, 24).Select(i => $"<article id='entry-{i}' data-noise='{new string('x', 4000)}'><h2>Observed {i}</h2><a href='/item/{i}'>Visit {i}</a><p>Description {i}</p></article>")) +
            "<p hidden>HIDDEN</p><script>const secret='SCRIPT';</script><input type='password' value='PASSWORD'><button id='change'>Continue</button><dialog open id='dialog'><section><span role='button' id='allow'>Accept cookies</span></section></dialog></main></body></html>";
        var serve = Task.Run(async () =>
        {
            try
            {
                while (site.IsListening)
                {
                    var context = await site.GetContextAsync();
                    var served = context.Request.Url!.AbsolutePath == "/capture" ? "<html><body><p>" + new string('x', 2_000_100) + "</p><main id='complete'><a href='/item'>Observed item</a></main><section id='empty'></section></body></html>" : html;
                    var bytes = Encoding.UTF8.GetBytes(served);
                    context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.OutputStream.WriteAsync(bytes, ct); context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        }, ct);
        try
        {
            await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"], MaxObservationRecords = 12 }), NullLogger<PlaywrightBrowserHost>.Instance);
            var page = await host.GetContentAsync(origin, "domcontentloaded", null, "main", "observation", null, false, ct);
            var all = new List<BrowserObservationRecord>(); var serializedCharacters = 0; string? cursor;
            do
            {
                var serialized = JsonSerializer.Serialize(page, BrowserMcpJsonContext.Default.BrowserContentResult);
                Assert.True(serialized.Length <= 24_000); Assert.InRange(page.Observation!.Records.Count, 1, 12);
                Assert.DoesNotContain("HIDDEN", serialized); Assert.DoesNotContain("SCRIPT", serialized); Assert.DoesNotContain("PASSWORD", serialized);
                Assert.False(page.Observation.CaptureTruncated);
                Assert.Equal(string.Join('\n', page.Observation.Records.Select(r => r.Text).Where(t => t.Length > 0)), page.Content);
                all.AddRange(page.Observation.Records); serializedCharacters += serialized.Length; cursor = page.Observation.NextCursor;
                if (cursor is not null) { Assert.True(page.Truncated); page = await host.GetContentAsync(null, "load", null, null, "observation", null, false, ct, cursor); }
            } while (cursor is not null);
            Assert.False(page.Truncated);
            Assert.Equal(24, all.Count(r => r.Kind == "link"));
            Assert.Contains(all, r => r.Kind == "control" && r.Selector == "#allow" && r.Group == "#dialog" && r.Text == "Accept cookies" && r.Role == "button");
            for (var i = 0; i < 24; i++)
            {
                Assert.Contains(all, r => r.Kind == "heading" && r.Text == "Observed " + i && r.Group == "#entry-" + i);
                Assert.Contains(all, r => r.Kind == "link" && r.Href == origin + "/item/" + i && r.Group == "#entry-" + i);
                Assert.Contains(all, r => r.Text == "Description " + i && r.Group == "#entry-" + i);
            }
            output.WriteLine($"OBSERVATION measured: {serializedCharacters} serialized characters versus {html.Length} source HTML characters; {all.Count} records.");
            Assert.True(serializedCharacters < html.Length / 2, $"Observed {serializedCharacters} versus {html.Length} HTML characters.");
            var manifestResult = await host.GetContentAsync(null, "load", null, "main", "observation_pages", 2400, false, ct, maxRecords: 8);
            var manifest = Assert.IsType<BrowserObservationManifest>(manifestResult.ObservationManifest);
            Assert.False(manifestResult.Truncated); Assert.False(manifest.ManifestTruncated); Assert.False(manifest.CaptureTruncated);
            Assert.True(manifest.Pages.Count > 3); Assert.Empty(manifestResult.Content); Assert.Null(manifestResult.Observation);
            Assert.Equal(manifest.Pages.Count, manifest.Pages.Select(p => p.Cursor).Distinct().Count());
            var pagedRecords = new List<BrowserObservationRecord>();
            var compactSize = 0;
            foreach (var descriptor in manifest.Pages)
            {
                var chunk = await host.GetContentAsync(null, "load", null, null, "observation_pages", null, false, ct, descriptor.Cursor);
                Assert.Equal(descriptor.RecordCount, chunk.Observation!.Records.Count); Assert.Empty(chunk.Content);
                var serialized = JsonSerializer.Serialize(chunk, BrowserMcpJsonContext.Default.BrowserContentResult);
                Assert.InRange(serialized.Length, 1, 2400); compactSize += serialized.Length;
                Assert.Equal(serialized, JsonSerializer.Serialize(await host.GetContentAsync(null, "load", null, null, "observation_pages", null, false, ct, descriptor.Cursor), BrowserMcpJsonContext.Default.BrowserContentResult));
                pagedRecords.AddRange(chunk.Observation.Records);
            }
            Assert.Equal(manifest.RecordCount, pagedRecords.Count); Assert.Equal(all, pagedRecords);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetContentAsync(null, "load", null, null, "observation_pages", 2400, false, ct, manifest.Pages[0].Cursor));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetContentAsync(null, "load", null, null, "observation_pages", null, false, ct, manifest.Id + ":page:999"));
            output.WriteLine($"PAGED: {manifest.Pages.Count} frozen pages; {compactSize} response characters; exact {pagedRecords.Count} records.");
            await host.ClickAsync("#change", "domcontentloaded", 1000, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetContentAsync(null, "load", null, null, "observation_pages", null, false, ct, manifest.Pages[0].Cursor));
            var first = await host.GetContentAsync(null, "load", null, "main", "observation", 2400, false, ct, maxRecords: 2);
            var continuation = Assert.IsType<string>(first.Observation!.NextCursor);
            await host.ClickAsync("#change", "domcontentloaded", 1000, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetContentAsync(null, "load", null, null, "observation", null, false, ct, continuation));
            var recaptured = await host.GetContentAsync(null, "load", null, "main", "observation", null, false, ct);
            await host.GetContentAsync(origin, "domcontentloaded", null, null, "text", 1000, false, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetContentAsync(null, "load", null, null, "observation", null, false, ct, recaptured.Observation!.NextCursor));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.GetContentAsync(null, "load", null, null, "observation", null, false, cancelled.Token));
            var limited = await host.GetContentAsync(origin + "/capture", "domcontentloaded", null, null, "observation", null, false, ct);
            Assert.True(limited.Truncated); Assert.True(limited.Observation!.CaptureTruncated);
            Assert.Null(limited.Observation.NextCursor); Assert.Empty(limited.Observation.Records);
            // No cursor is not completeness: a capture limit requires a fresh narrower read.
            var narrowed = await host.GetContentAsync(null, "load", null, "#complete", "observation", null, false, ct);
            Assert.False(narrowed.Truncated); Assert.False(narrowed.Observation!.CaptureTruncated);
            Assert.Equal("Observed item", Assert.Single(narrowed.Observation.Records).Text);
            var empty = await host.GetContentAsync(null, "load", null, "#empty", "observation", null, false, ct);
            Assert.False(empty.Truncated); Assert.Null(empty.Observation!.NextCursor); Assert.Empty(empty.Observation.Records);
            var emptyManifest = await host.GetContentAsync(null, "load", null, "#empty", "observation_pages", null, false, ct);
            Assert.False(emptyManifest.Truncated); Assert.Empty(emptyManifest.ObservationManifest!.Pages);
            var partialManifest = await host.GetContentAsync(origin + "/capture", "domcontentloaded", null, null, "observation_pages", null, false, ct);
            Assert.True(partialManifest.Truncated); Assert.True(partialManifest.ObservationManifest!.CaptureTruncated);
            await using var bounded = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"], MaxObservationPages = 2 }), NullLogger<PlaywrightBrowserHost>.Instance);
            var boundedResult = await bounded.GetContentAsync(origin, "domcontentloaded", null, "main", "observation_pages", null, false, ct, maxRecords: 2);
            Assert.True(boundedResult.Truncated); Assert.True(boundedResult.ObservationManifest!.ManifestTruncated);
            Assert.False(boundedResult.ObservationManifest.CaptureTruncated); Assert.Equal(2, boundedResult.ObservationManifest.Pages.Count);
            var last = await bounded.GetContentAsync(null, "load", null, null, "observation_pages", null, false, ct, boundedResult.ObservationManifest.Pages[^1].Cursor);
            Assert.True(last.Truncated); Assert.Null(last.Observation!.NextCursor);
            await host.CloseAsync(ct);
        }
        finally { site.Stop(); await serve; }
    }
}
