using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;

// Disposable prerequisites only: no external page, provider dispatch or policy edit.
internal static class LiveExecutionReadiness
{
    internal static async Task CopilotAsync(string[] args, BenchmarkCampaign campaign, KeyVaultBenchmarkModel model, string root)
    {
        var label = SchemaPortabilityCampaign.Option(args, "--run") ?? throw new ArgumentException("Supply a fresh --run.");
        if (label.Length > 80 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid run identity.");
        const string collection = "planning-copilot-probes";
        if (await campaign.LoadAsync(collection, label) is not null) throw new InvalidOperationException("Probe already retained; never replay an external invocation.");
        var directory = Path.Combine(root, "workflows", campaign.Id, label);
        if (Directory.Exists(directory)) throw new InvalidOperationException("Probe workspace already exists.");
        var tenant = "probe-" + label;
        var state = new JsonObject { ["source_sha"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"), ["label"] = label,
            ["provider_hash"] = model.ConfigurationFingerprint, ["readiness"] = await model.ReadinessAsync(CancellationToken.None),
            ["accounting_before"] = await campaign.InspectAsync(), ["events"] = new JsonArray(), ["passed"] = false };
        await campaign.SaveAsync(collection, label, state);
        if (state["readiness"]?["ready"]?.GetValue<bool>() != true) throw new InvalidOperationException("Exact deployment allowances are required before paid probes.");
        await using var proxy = await CampaignInferenceProxy.StartAsync(model, label);
        var settings = LiveWorkflowEvaluation.Configuration(model.McpServers, "code", proxy.Endpoint);
        var human = new LiveWorkflowEvaluation.ConsoleHuman(campaign, label);
        // Expose only this producer; no Git or publication tools are connected.
        await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, GnOuGo.AI.Core.McpServerOptions>
            { ["GnOuGo.GithubCopilot.Mcp"] = settings["GnOuGo.GithubCopilot.Mcp"] }, human, model.Provider, model.Model);
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, async evt =>
        { state["events"]!.AsArray().Add(evt); await campaign.SaveAsync(collection, label, state); });
        var session = await observed.GetClientAsync("GnOuGo.GithubCopilot.Mcp", CancellationToken.None);
        var tools = await session.ListToolsAsync(CancellationToken.None);
        if (!proxy.Ready || !tools.Any(t => t.Name == "code_agent_edit")) throw new InvalidOperationException("Copilot inference interception or operation is unavailable.");
        Directory.CreateDirectory(directory);
        var seed = "observed-" + label;
        await File.WriteAllTextAsync(Path.Combine(directory, "seed.txt"), seed);
        await File.WriteAllTextAsync(Path.Combine(directory, "probe.py"), """
            import json
            from pathlib import Path
            value = Path('seed.txt').read_text()
            with Path('receipt.log').open('a') as log:
                log.write('command\n')
            Path('report.json').write_text(json.dumps({'observed': value, 'exit_code': 0}))
            print(value)
            """);
        var objective = "In this disposable project, run exactly `python3 probe.py` once, with this project as working directory. " +
            "Read report.json through project_read, then return a concise final report containing the observed value and actual exit code. " +
            "Do not modify source files, install dependencies, access networks or other directories, or repeat the command to retrieve its output. " +
            "If denied or unsuccessful, return a truthful final failure. All evidence stays local.";
        var request = new JsonObject { ["projectRoot"] = Path.GetRelativePath(root, directory), ["task"] = objective,
            ["provider"] = model.Provider, ["contextFiles"] = new JsonArray("probe.py") };
        var yaml = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: review\n        type: mcp.call\n        input:\n          server: GnOuGo.GithubCopilot.Mcp\n          method: code_agent_edit\n          request: " + request.ToJsonString() + "\n";
        state["artifact_hash"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(yaml);
        state["execution_started"] = DateTimeOffset.UtcNow.ToString("O"); await campaign.SaveAsync(collection, label, state);
        var store = EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: root);
        var engine = new WorkflowEngine { McpClientFactory = observed, HumanInputProvider = human, RunStore = store,
            Limits = new() { TenantId = tenant, RunId = label, AgentId = label, AgentName = "Disposable completion probe" } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            proxy.ExecutionEnabled = true;
            var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], new JsonObject(), deadline.Token);
            proxy.ExecutionEnabled = false;
            state["execution_success"] = result.Success; state["error"] = result.Error?.Code;
            var journal = await store.ReadAsync(tenant, label);
            var verified = journal?.Invocations.Values.SingleOrDefault(i => i.StepType == "mcp.call")?.ExternalCompletionObserved == true;
            var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root);
            var receipts = (await records.ListAsync("github-copilot.logical-tasks-v1", tenant, "GnOuGo.GithubCopilot.Mcp"))
                .Select(r => JsonNode.Parse(r.Value)!.AsObject()).Where(r => r["operation"]?["agentId"]?.ToString() == label).ToArray();
            state["completion_verified"] = verified;
            state["task_receipts"] = new JsonArray(receipts.Select(r => (JsonNode)r.DeepClone()).ToArray());
            var commandCount = File.Exists(Path.Combine(directory, "receipt.log")) ? (await File.ReadAllLinesAsync(Path.Combine(directory, "receipt.log"))).Length : 0;
            var report = File.Exists(Path.Combine(directory, "report.json")) ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json"))) : null;
            state["report"] = report?.DeepClone(); state["command_count"] = commandCount;
            var response = state["events"]!.AsArray().LastOrDefault(e => e?["tool"]?.ToString() == "code_agent_edit")?["result"];
            var commands = LiveCodeEvidence.Commands(state, directory);
            state["observed_commands"] = commands;
            var exit = commands.Count == 1 && commands[0]?["command"]?.ToString() == "python3 probe.py" && commands[0]?["exit_code"]?.GetValue<int>() == 0;
            var durable = receipts.Length == 1 && receipts[0]["operation"]?["completionVerified"]?.GetValue<bool>() == true && receipts[0]["task"]?["result"] is not null;
            // A new encrypted reader verifies receipt persistence without reconnecting or dispatching the SDK.
            var restarted = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root);
            var reread = (await restarted.ListAsync("github-copilot.logical-tasks-v1", tenant, "GnOuGo.GithubCopilot.Mcp")).Select(r => JsonNode.Parse(r.Value)).ToArray();
            var retained = durable && reread.Any(r => JsonNode.DeepEquals(r?["task"]?["result"], receipts[0]["task"]?["result"]));
            state["receipt_reloaded"] = retained;
            state["passed"] = result.Success && verified && retained && exit && commandCount == 1 &&
                report?["observed"]?.ToString() == seed && report?["exit_code"]?.GetValue<int>() == 0 && response?["summary"]?.ToString().Contains(seed, StringComparison.Ordinal) == true;
            await campaign.SaveAsync(collection, label, state);
            if (verified && durable) { Directory.Delete(directory, true); state["cleanup"] = !Directory.Exists(directory); }
            else state["cleanup"] = false;
            state["passed"] = state["passed"]!.GetValue<bool>() && state["cleanup"]!.GetValue<bool>();
        }
        catch (Exception ex) { state["failure_type"] = ex.GetType().Name; state["failure"] = ex.Message; }
        finally
        {
            proxy.ExecutionEnabled = false; state["execution_ms"] = clock.ElapsedMilliseconds; state["accounting"] = await campaign.InspectAsync();
            await campaign.SaveAsync(collection, label, state);
            Console.WriteLine(new JsonObject { ["run"] = label, ["passed"] = state["passed"]!.DeepClone(),
                ["error"] = state["error"]?.DeepClone(), ["failure"] = state["failure"]?.DeepClone(), ["cleanup"] = state["cleanup"]?.DeepClone() }.ToJsonString());
            if (state["passed"]?.GetValue<bool>() != true) Environment.ExitCode = 1;
        }
    }
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
