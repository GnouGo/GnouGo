using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;
using GnOuGo.Workspace;

/// <summary>Explicitly invoked, three-identity diagnosis. Never approves or executes an artifact.</summary>
internal static class RealContractPlanningProbe
{
    private const string Collection = "planning-product-diagnosis";
    private const string Cohort = "real-contracts-20260926";
    public static async Task RunAsync(string[] args)
    {
        var phase = args.ElementAtOrDefault(1) ?? "inspect";
        if (phase is not ("capture" or "capture-after" or "inspect" or "candidate-before" or "main" or "candidate-after")) throw new ArgumentException("Unknown diagnosis phase.");
        var root = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root);
        var campaign = new BenchmarkCampaign(records, "flow-v9-112");
        if (phase == "inspect")
        {
            foreach (var label in new[] { "candidate-before", "main", "candidate-after" })
                Console.WriteLine((await campaign.LoadAsync(Collection, Cohort + ":" + label))?["result"]?.ToJsonString() ?? label + ": absent");
            Console.WriteLine((await BenchmarkHttpJournal.AccountingAsync(campaign)).ToJsonString()); return;
        }
        var leasePath = GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/planning-evaluation/flow-v9-112.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        await using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var metadataKey = Cohort + (phase is "capture-after" or "candidate-after" ? ":metadata-after" : ":metadata");
        var frozen = await campaign.LoadAsync(Collection, metadataKey);
        if (phase is "capture" or "capture-after")
        {
            if (frozen is null)
            {
                var metadata = RealProductContracts.Capture(new DocumentPolicy(new DocumentServerSettings(), root));
                frozen = new() { ["metadata"] = metadata, ["hash"] = PlanningGraphCompiler.Fingerprint(metadata.ToJsonString()), ["prompt"] = RealProductContracts.Prompt };
                await campaign.SaveAsync(Collection, metadataKey, frozen);
            }
            Console.WriteLine("Frozen metadata " + frozen["hash"]); return;
        }
        if (frozen is null || frozen["prompt"]?.ToString() != RealProductContracts.Prompt) throw new InvalidOperationException("Freeze the exact prompt and producer metadata first.");
        var key = Cohort + ":" + phase;
        var retained = await campaign.LoadAsync(Collection, key);
        var resumeUndispatched = args.Contains("--resume-undispatched-main", StringComparer.Ordinal);
        if (retained is not null && !resumeUndispatched) throw new InvalidOperationException("This identity already exists. Inspect its evidence; do not restart it.");
        if (resumeUndispatched && (phase != "main" || retained is null || retained["resume_count"] is not null ||
            retained["requests"]!.AsArray().Count != 0 || retained["failure"]?.ToString().StartsWith("System.InvalidOperationException: The node already has a parent.", StringComparison.Ordinal) != true ||
            (await BenchmarkHttpJournal.AccountingAsync(campaign, retained["session_id"] + ":"))["session_calls"]!.GetValue<long>() != 0))
            throw new InvalidOperationException("Only the verified undispatched checkpoint-copy failure can resume.");
        var harnessSource = Git("rev-parse", "HEAD");
        var source = phase == "main" ? "bf90eb6d5048bd80ba83cb70df69b841007d4d59" : harnessSource;
        if (Git("status", "--porcelain").Length != 0) throw new InvalidOperationException("Commit the harness before dispatch.");
        using var model = await KeyVaultBenchmarkModel.CreateAsync("OpenAi", "gpt-5.5-2026-04-24", campaign, root, CancellationToken.None);
        var id = PlanningGraphCompiler.Fingerprint("flow-v9-112:" + key);
        var run = retained ?? new JsonObject { ["source"] = source, ["harness_source"] = harnessSource, ["phase"] = phase, ["metadata_hash"] = frozen["hash"]!.DeepClone(),
            ["session_id"] = id, ["requests"] = new JsonArray(), ["history"] = new JsonArray(), ["prompt"] = RealProductContracts.Prompt };
        if (resumeUndispatched)
        {
            await campaign.SaveAsync(Collection, key + ":undispatched-harness-failure", run.DeepClone().AsObject());
            run["resume_count"] = 1; run["resume_harness_source"] = harnessSource;
            run.Remove("failure"); run.Remove("result");
        }
        await campaign.SaveAsync(Collection, key, run);
        var clock = Stopwatch.StartNew();
        var measured = new MeasuredModel(model, run);
        try
        {
            if (phase == "main")
            {
                var executable = args.ElementAtOrDefault(2) ?? throw new ArgumentException("Supply the isolated main harness assembly.");
                await Parent(executable, frozen["metadata"]!.AsObject(), id, measured, CheckpointRaw, retained?["session"] as JsonObject);
            }
            else
            {
                var factory = new RealProductContracts.Factory(frozen["metadata"]!.DeepClone().AsObject());
                var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = measured, LlmDefaults = new() { Model = model.Model, Provider = model.Provider } };
                var runtime = new WorkflowPlanningRuntime(engine, (s, _) => CheckpointRaw(JsonSerializer.SerializeToNode(s, PlanningJsonContext.Default.PlanningSession)!.AsObject()));
                var state = new PlanningSession { Request = new() { TenantId = "benchmark", SessionId = id, Mode = PlanningMode.Auto,
                    Name = "product-query", Prompt = RealProductContracts.Prompt, Generation = new() { MaxInputTokensPerRequest = 24_000, MaxOutputTokens = 8_192, Reasoning = "medium" } } };
                for (var advance = 0; advance < 40 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
                {
                    state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
                    await runtime.CheckpointAsync(state, CancellationToken.None);
                    if (campaign.StopReason is not null) break;
                }
                run["invocation_attempts"] = factory.InvocationAttempts;
                run["discovery_reads"] = JsonSerializer.SerializeToNode(factory.DiscoveryReads);
            }
        }
        catch (Exception ex)
        {
            // Full exceptions remain encrypted; stdout never contains provider bodies.
            run["failure"] = ex.ToString();
        }
        run["elapsed_ms"] = clock.ElapsedMilliseconds;
        var s = run["session"];
        run["result"] = new JsonObject { ["phase"] = phase, ["source"] = source, ["status"] = s?["status"]?.DeepClone(),
            ["calls"] = s?["modelCalls"]?.DeepClone(), ["repairs"] = s?["replanAttempts"]?.DeepClone(),
            ["diagnostics"] = s?["diagnostics"]?.DeepClone(), ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["failure"] = run["failure"] is not null, ["termination_reason"] = campaign.StopReason,
            ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(campaign, id + ":", ct: CancellationToken.None) };
        await campaign.SaveAsync(Collection, key, run);
        Console.WriteLine(run["result"]!.ToJsonString());

        async Task CheckpointRaw(JsonObject state)
        {
            run["session"] = state.DeepClone();
            run["history"]!.AsArray().Add(new JsonObject { ["revision"] = state["revision"]?.DeepClone(), ["status"] = state["status"]?.DeepClone(), ["diagnostics"] = state["diagnostics"]?.DeepClone() });
            await campaign.SaveAsync(Collection, key, run);
            Console.WriteLine("checkpoint " + phase + " " + state["revision"] + " " + state["status"]);
        }
    }

    private sealed class MeasuredModel(KeyVaultBenchmarkModel inner, JsonObject run) : ILLMClient
    {
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (request.MaxTokens is null or <= 0 or > 8192 || request.Tools is { Count: > 0 }) throw new InvalidOperationException("Unexpected diagnosis request scope.");
            run["requests"]!.AsArray().Add(new JsonObject { ["id"] = request.ClientRequestId,
                ["prompt_bytes"] = Encoding.UTF8.GetByteCount(request.Prompt ?? ""), ["schema_bytes"] = Encoding.UTF8.GetByteCount(request.StructuredOutputSchema?.ToJsonString() ?? "") });
            return await inner.CallAsync(request, ct);
        }
    }
    private static async Task Parent(string assembly, JsonObject metadata, string id, ILLMClient model, Func<JsonObject, Task> checkpoint, JsonObject? retained)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(assembly);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Parent harness did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(new JsonObject { ["metadata"] = metadata.DeepClone(), ["sessionId"] = id, ["prompt"] = RealProductContracts.Prompt, ["session"] = retained?.DeepClone() }.ToJsonString());
            await process.StandardInput.FlushAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var message = JsonNode.Parse(line)!;
                if (message["kind"]?.ToString() == "checkpoint") await checkpoint(message["session"]!.AsObject());
                else if (message["kind"]?.ToString() == "request")
                {
                    var request = JsonSerializer.Deserialize(message["request"], PlanningJsonContext.Default.LLMRequest)!;
                    var response = await model.CallAsync(request, CancellationToken.None);
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse));
                    await process.StandardInput.FlushAsync();
                }
                else throw new InvalidOperationException("Unexpected parent message.");
            }
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Parent harness failed: " + await errors);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    private static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var result = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot identify revision."); return result;
    }
}
