using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;

internal static class SchemaPortabilityCampaign
{
    internal const string Model = "gpt-5.5-2026-04-24";
    private const string OriginalSession = "a425b85bb133470cbab4e5f201b9d729";
    internal const string Collection = "planning-schema-portability";
    public static async Task RunAsync(string[] args)
    {
        var phase = args.ElementAtOrDefault(1) ?? "inspect";
        var root = Option(args, "--workspace") ?? GnOuGoWorkspace.ResolveDefaultWorkingDirectory();
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root);
        if (phase == "capture")
        {
            var folder = Option(args, "--output") ?? throw new ArgumentException("Supply --output.");
            var requests = (await records.ListAsync("agent-planning-model-requests-v10", "default", BenchmarkCampaign.Author))
                .Where(r => r.Key.StartsWith(OriginalSession + ":", StringComparison.Ordinal)).OrderBy(r => r.UpdatedAt).ToArray();
            var receipts = (await records.ListAsync("agent-planning-model-receipts-v10", "default", BenchmarkCampaign.Author))
                .Where(r => r.Key.StartsWith(OriginalSession + ":", StringComparison.Ordinal)).OrderBy(r => r.UpdatedAt).ToArray();
            var states = (await records.ListAsync("agent-planning-sessions-v10", "default", BenchmarkCampaign.Author))
                .Where(r => r.Key.StartsWith(OriginalSession, StringComparison.Ordinal)).OrderBy(r => r.UpdatedAt).ToArray();
            if (requests.Length != 2 || receipts.Length != 1 || states.Length == 0) throw new InvalidOperationException("The retained reproduction has changed.");
            var state = JsonNode.Parse(states[^1].Value)!.AsObject();
            var retained = new JsonObject
            {
                ["session"] = OriginalSession, ["origin"] = "Designer", ["modelCalls"] = state["modelCalls"]?.DeepClone(),
                ["repairs"] = state["replanAttempts"]?.DeepClone(), ["status"] = state["status"]?.DeepClone(),
                ["diagnostics"] = state["diagnostics"]?.DeepClone(),
                ["schemas"] = new JsonArray(requests.Select(r => JsonNode.Parse(r.Value)!["structuredOutputSchema"]!.DeepClone()).ToArray()),
                ["discoveryResponse"] = JsonNode.Parse(receipts[0].Value)!["json"]?.DeepClone()
            };
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "retained-schema-rejection.json"), retained.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Captured two issued schemas, the discovery response and stopped-session diagnostics; request prompts and host configuration excluded.");
            return;
        }
        var campaignId = Option(args, "--campaign") ?? throw new ArgumentException("Supply a new --campaign.");
        var campaign = new BenchmarkCampaign(records, campaignId);
        if (phase == "inspect") { Console.WriteLine((await campaign.InspectAsync()).ToJsonString()); return; }
        var leasePath = GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/planning-evaluation/" + campaignId + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        await using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Git("status", "--porcelain").Length != 0) throw new InvalidOperationException("Commit the tested source and harness before paid dispatch.");
        using var model = await KeyVaultBenchmarkModel.CreateAsync("OpenAi", Model, campaign, root, CancellationToken.None);
        if (phase == "diagnose")
        {
            const string key = "original-rejection";
            if (await campaign.LoadAsync(Collection, key) is not null) throw new InvalidOperationException("Diagnostic identity already retained; do not repeat.");
            var saved = (await records.ListAsync("agent-planning-model-requests-v10", "default", BenchmarkCampaign.Author))
                .Where(r => r.Key.StartsWith(OriginalSession + ":2:", StringComparison.Ordinal)).Single();
            var request = JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMRequest)!;
            var originalHash = PlanningGraphCompiler.Fingerprint(saved.Value);
            request.ClientRequestId = "schema-diagnostic:1:" + originalHash;
            var result = new JsonObject { ["source"] = Git("rev-parse", "HEAD"), ["phase"] = "diagnostic", ["request_id"] = request.ClientRequestId,
                ["original_request_hash"] = originalHash, ["schema_hash"] = PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema!.ToJsonString()) };
            await campaign.SaveAsync(Collection, key, result);
            var timer = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await model.DiagnosticAsync(request, timeout.Token); result["provider_accepted"] = true; }
            catch (LLMClientException ex)
            {
                result["provider_accepted"] = false; result["status"] = ex.StatusCode; result["provider_code"] = ex.SafeProviderCode;
                if (ex.StatusCode == 400 && ex.SafeProviderCode == "invalid_json_schema")
                    await campaign.RetainSchemaRejectionAsync(request.ClientRequestId, CancellationToken.None);
            }
            catch (Exception ex) { result["exception_type"] = ex.GetType().Name; }
            result["latency_ms"] = timer.ElapsedMilliseconds;
            result["accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId);
            await campaign.SaveAsync(Collection, key, result);
            Console.WriteLine(result.ToJsonString()); return;
        }
        throw new ArgumentException("Unknown phase.");
    }

    internal static string? Option(string[] args, string name)
    { var index = Array.IndexOf(args, name); return index < 0 ? null : args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("Missing " + name); }
    internal static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var result = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot identify the source revision."); return result;
    }
}
