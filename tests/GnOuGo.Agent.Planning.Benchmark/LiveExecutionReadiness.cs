using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using GnOuGo.Flow.Core.Runtime;

// Disposable prerequisites only: no external page, provider dispatch or policy edit.
internal static class LiveExecutionReadiness
{
    internal static async Task VerifyAsync(IMcpClientFactory transport, string workspace)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        var marker = "readiness-" + Guid.NewGuid().ToString("N");
        var relative = "workflows/" + marker + "/probe.xlsx";
        var path = Path.Combine(workspace, relative);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var serve = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(ct);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
            var body = Encoding.UTF8.GetBytes("<html><body>" + marker + "</body></html>");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
            await stream.WriteAsync(body, ct);
        }, ct);
        var browser = await transport.GetClientAsync("GnOuGo.Browser.Mcp", ct);
        try
        {
            var page = await browser.CallToolAsync("browser_get_content", new JsonObject
                { ["url"] = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, ["format"] = "text", ["waitUntil"] = "domcontentloaded" }, ct);
            if (page.IsError || !page.Content!.ToJsonString().Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("The configured browser cannot read the disposable readiness page.");
            await serve;
            var document = await transport.GetClientAsync("GnOuGo.Document.Mcp", ct);
            var write = await document.CallToolAsync("document_write", new JsonObject { ["filePath"] = relative, ["content"] = "probe\n" + marker }, ct);
            if (write.IsError || !File.Exists(path)) throw new InvalidOperationException("The configured XLSX destination is not writable.");
            using var workbook = SpreadsheetDocument.Open(path, false);
            if (!workbook.WorkbookPart!.WorksheetParts.Any(p => p.Worksheet!.InnerText.Contains(marker, StringComparison.Ordinal)))
                throw new InvalidOperationException("The written XLSX does not contain the independently observed readiness value.");
        }
        finally
        {
            await browser.CallToolAsync("browser_close", new JsonObject(), CancellationToken.None);
            listener.Stop(); await timeout.CancelAsync();
            try { await serve; } catch (Exception e) when (e is OperationCanceledException or SocketException) { }
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!);
        }
        var after = await browser.CallToolAsync("browser_get_content", new JsonObject(), CancellationToken.None);
        if (!after.IsError || !after.Content!.ToJsonString().Contains("No active page", StringComparison.Ordinal))
            throw new InvalidOperationException("Browser cleanup did not release its active page.");
        Console.WriteLine("{\"browser_local_page\":true,\"browser_cleanup\":true,\"xlsx_independent_read\":true,\"model_calls\":0}");
    }
}
