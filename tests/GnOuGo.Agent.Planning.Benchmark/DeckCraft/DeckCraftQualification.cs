using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Core.Planning;

internal static class DeckCraftQualification
{
    internal const string Version = "deckcraft-eight-slides-v1";
    private const string Source = "4a980c33ddf37eb64157777fcaeab6d19baa5878";
    private static string Fixture(string name) => Path.Combine("tests", "GnOuGo.Agent.Planning.Benchmark", "DeckCraft", name);
    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    internal static string PrepareDirectory(string workspace, string campaign, string run)
    {
        var path = Path.GetFullPath(Path.Combine(workspace, "workflows", campaign, run));
        var directory = Directory.CreateDirectory(path);
        for (var p = directory; p is not null; p = p.Parent)
            if (p.LinkTarget is not null) throw new PlanningConflictException("A linked DeckCraft workspace is outside the run grant.");
        var source = Fixture("panel-k.png");
        var owned = Path.Combine(path, "illustration.png");
        if (!File.Exists(owned)) File.Copy(source, owned, false);
        if (Hash(owned) != Hash(source)) throw new PlanningConflictException("The illustration changed.");
        return path;
    }

    internal static string Brief(string directory, string relative) => File.ReadAllText(Fixture("scenario.md")) +
        $"\nThe run owns {directory}. Use absolute file arguments under that directory. The supplied file is illustration.png. " +
        $"Declare a caller output named presentationPath with the delivered workspace path {relative}/after.pptx. " +
        "The pinned registry publishes slide.notes (text, zero-based index) and chart.data (categories, series). Use list_commands filtered by the exact ID before run_command or batch. " +
        "The specialized insert_table accepts rows, cols and data as rows of strings. " +
        "The configured business agent receives each successful render_slide image in its next multimodal decision. " +
        "Its mcp.result evidence pins the approved tool ID and request; the projected image block contains image.sha256. " +
        "The runner can return that observed digest and a structured visual assessment. Use the configured model's input reservation when budgeting image decisions. " +
        "Keep before.pptx as evidence. Review renders in batches of at most eight. Return a structured findings array, empty if clear. " +
        "Include each reviewed image digest and slide number. Finish with eight reviewed renders from the reopened delivery.";

    internal static Dictionary<string, McpServerOptions> Configuration(string? directory)
    {
        var binary = Environment.GetEnvironmentVariable("GNOUGO_DECKCRAFT_CLI") ?? throw new InvalidOperationException("Set GNOUGO_DECKCRAFT_CLI to the qualified binary.");
        var settings = DeckCraftMcp.ForWorkspace(binary, directory ?? throw new InvalidOperationException("DeckCraft requires a run directory."));
        _ = Capture(settings);
        return new(StringComparer.Ordinal) { ["DeckCraft"] = settings };
    }

    internal static JsonObject Capture(McpServerOptions config)
    {
        var binary = config.Command!;
        var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(binary)!, "installation.json")))!;
        var digest = Hash(binary);
        if (receipt["source"]?.ToString() != Source || receipt["cli_sha256"]?.ToString() != digest ||
            receipt["patch_sha256"]?.ToString() != Hash(Path.Combine("packaging", "deckcraft", "editable-column-chart.patch")))
            throw new PlanningConflictException("The DeckCraft binary or workbook patch differs from its build receipt.");
        return new() { ["transport"] = "stdio", ["source"] = Source, ["patch_sha256"] = receipt["patch_sha256"]!.DeepClone(),
            ["binary_sha256"] = digest, ["target"] = receipt["target"]!.DeepClone() };
    }

    internal static bool Allows(string tool) => tool is "new_presentation" or "open_presentation" or "save_presentation" or
        "inspect_document" or "inspect_slide" or "render_slide" or "add_slide" or "go_to_slide" or "add_shape" or
        "add_text_box" or "set_text" or "insert_picture" or "insert_table" or "insert_chart" or "list_commands" or "batch" or "run_command";

    internal static void CheckArguments(string tool, JsonObject args, string root)
    {
        static void Deny() => throw new PlanningConflictException("The DeckCraft arguments exceed the approved presentation scope.");
        if (tool == "list_commands" && string.IsNullOrWhiteSpace(args["filter"]?.ToString())) Deny();
        if (tool is "open_presentation" or "save_presentation" or "insert_picture")
        {
            var path = args["path"]?.ToString();
            if (path is null || !Path.IsPathFullyQualified(path) || new FileInfo(path).LinkTarget is not null) Deny();
            var allowed = tool == "insert_picture" ? new[] { "illustration.png" } : ["before.pptx", "after.pptx"];
            if (!allowed.Any(name => Path.GetFullPath(path!) == Path.GetFullPath(Path.Combine(root, name)))) Deny();
        }
        if (tool == "render_slide" && args["path"] is not null) Deny();
        if (tool == "run_command") CheckCommand(args);
        if (tool == "batch")
        {
            if (args["commands"] is not JsonArray { Count: > 0 and <= 128 } commands) throw new PlanningConflictException("Invalid presentation batch.");
            foreach (var item in commands) CheckCommand(item);
        }
        static void CheckCommand(JsonNode? item)
        {
            if (item?["command"]?.ToString() is not ("slide.notes" or "chart.data" or
                "table.style" or "table.cellFill" or "shape.fill" or "shape.move" or "shape.resize" or
                "text.size" or "text.color" or "text.bold" or "slide.background")) Deny();
        }
    }

    internal static JsonNode? Observe(McpCallResult result)
    {
        if (result.ContentBlocks?.OfType<JsonObject>().FirstOrDefault(b => b["type"]?.ToString() == "image") is not { } block)
            return result.Content?.DeepClone();
        var bytes = Convert.FromBase64String(block["data"]!.GetValue<string>());
        return new JsonObject { ["mimeType"] = block["mimeType"]?.DeepClone(), ["size"] = bytes.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    internal static async Task<JsonObject> VerifyAsync(JsonObject report, string workspace, string evidenceDirectory, WorkflowRun journal, CancellationToken ct)
    {
        var folder = Path.Combine(workspace, report["workspace_relative"]!.ToString());
        var evidence = new JsonObject { ["version"] = Version, ["passed"] = false };
        var events = report["events"]!.AsArray().OfType<JsonObject>().Where(e => e["server"]?.ToString() == "DeckCraft" && e["error"]?.GetValue<bool>() == false).ToArray();
        var archive = new JsonArray();
        IWorkflowImageStore imageStore = LiveWorkflowEvaluation.ExecutionStore(workspace);
        foreach (var render in events.Where(e => e["tool"]?.ToString() == "render_slide"))
        {
            if (render["result"]?["sha256"]?.ToString() is not { Length: 64 } digest || !digest.All(char.IsAsciiHexDigitLower)) continue;
            var bytes = await imageStore.GetAsync(journal.TenantId, journal.RunId, digest, ct);
            if (bytes is null || Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest) continue;
            var name = "renders/" + digest + ".png";
            Directory.CreateDirectory(Path.Combine(evidenceDirectory, "renders"));
            await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, name), bytes, ct);
            archive.Add(new JsonObject { ["slide"] = render["arguments"]?["slide"]?.DeepClone(),
                ["started"] = render["started"]?.DeepClone(), ["sha256"] = digest, ["file"] = name });
        }
        evidence["renders"] = archive;
        if (journal.FinalizationCompleted)
            evidence["staged_illustration_released"] = ReleaseIllustration(report, workspace, folder);
        if (journal.Result?.Success != true || !journal.FinalizationCompleted) return evidence;
        var reopened = Array.FindLastIndex(events, e => e["tool"]?.ToString() == "open_presentation" &&
            e["arguments"]?["path"]?.ToString() is { } path && Path.GetFullPath(path) == Path.GetFullPath(Path.Combine(folder, "after.pptx")));
        var finalRenders = events.Skip(reopened + 1).Where(e => e["tool"]?.ToString() == "render_slide" && e["result"]?["sha256"] is not null).ToArray();
        var coverage = new HashSet<int>();
        int? selected = null;
        foreach (var evt in events.Skip(reopened + 1))
        {
            switch (evt["tool"]?.ToString())
            {
                case "go_to_slide": selected = evt["arguments"]?["index"]?.GetValue<int>(); break;
                case "inspect_document": selected = evt["result"]?["selection"]?["slide"]?.GetValue<int>() ?? selected; break;
                case "render_slide" when (evt["arguments"]?["slide"]?.GetValue<int>() ?? selected) is int slide:
                    if (evt["result"]?["sha256"] is not null) coverage.Add(slide);
                    break;
                case "new_presentation" or "open_presentation" or "add_slide" or "batch" or "run_command": selected = null; break;
            }
        }
        var reviews = report["vision_receipts"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var initialRenders = events.Take(Math.Max(reopened, 0)).Where(e => e["tool"]?.ToString() == "render_slide" && e["result"]?["sha256"] is not null).ToArray();
        var expected = report["workspace_relative"]!.ToString() + "/after.pptx";
        var caller = report["caller_response"];
        var correctOutput = journal.Result.Outputs?["presentationPath"]?.ToString() == expected &&
            caller?["run"]?.ToString() == journal.RunId && caller["agent"]?.ToString() == report["run"]?.ToString() &&
            CallerPath(caller["text"]?.ToString()) == expected;
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Fixture("verify-deck.py")); start.ArgumentList.Add(folder);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var err = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); } catch { if (!process.HasExited) process.Kill(true); throw; }
        evidence["structural"] = process.ExitCode == 0 ? JsonNode.Parse(await output) : JsonValue.Create(await err);
        evidence["caller_output_matches"] = correctOutput;
        evidence["reopened_slide_coverage"] = coverage.SetEquals(Enumerable.Range(0, 8)) && reopened >= 0;
        evidence["initial_vision_observed"] = initialRenders.Length > 0 && reopened >= 0 && initialRenders.Any(e =>
            reviews.Any(r => Reviewed(e, r) && Timestamp(r, "received") <= Timestamp(events[reopened], "started")));
        evidence["final_vision_coverage"] = finalRenders.Length >= 8 && finalRenders.All(e => reviews.Any(r => Reviewed(e, r)));
        var cycles = CorrectionCycles(events, reviews);
        evidence["correction_cycles"] = cycles;
        var archived = archive.OfType<JsonObject>().Select(r => r["sha256"]!.ToString()).ToHashSet();
        evidence["passed"] = process.ExitCode == 0 && correctOutput && cycles <= 2 &&
            evidence["staged_illustration_released"]?.GetValue<bool>() == true &&
            finalRenders.All(e => archived.Contains(e["result"]!["sha256"]!.ToString())) && evidence["reopened_slide_coverage"]!.GetValue<bool>() &&
            evidence["initial_vision_observed"]!.GetValue<bool>() && evidence["final_vision_coverage"]!.GetValue<bool>() &&
            finalRenders.All(e => reviews.Any(r => Reviewed(e, r) && Findings(r) is { Count: 0 }));
        return evidence;
    }

    private static bool ReleaseIllustration(JsonObject report, string workspace, string folder)
    {
        var campaign = report["campaign"]!.ToString();
        var run = report["run"]!.ToString();
        if (new[] { campaign, run }.Any(s => s.Length == 0 || !s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) ||
            Path.GetFullPath(folder) != Path.GetFullPath(Path.Combine(workspace, "workflows", campaign, run))) return false;
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null) return false;
        var file = new FileInfo(Path.Combine(folder, "illustration.png"));
        if (file.LinkTarget is not null) return false;
        if (!file.Exists) return true;
        if (Hash(file.FullName) != Hash(Fixture("panel-k.png"))) return false;
        file.Delete();
        return !File.Exists(file.FullName);
    }

    internal sealed class CorrectionLimit
    {
        private readonly object _gate = new();
        private bool _pending;
        private int _passes;

        internal void Observe(JsonObject review)
        {
            if (Findings(review) is { Count: > 0 }) lock (_gate) _pending = true;
        }

        internal void BeforeTool(string tool)
        {
            if (!IsMutation(tool)) return;
            lock (_gate)
            {
                if (!_pending) return;
                if (_passes >= 2) throw new PlanningConflictException("The two visual correction passes are exhausted.");
                _passes++;
                _pending = false;
            }
        }
    }

    private static bool IsMutation(string? tool) => tool is "run_command" or "batch" or "add_slide" or
        "set_text" or "add_text_box" or "add_shape" or "insert_table" or "insert_chart" or "insert_picture";

    internal static int CorrectionCycles(JsonObject[] events, JsonObject[] reviews)
    {
        // Several findings from the same review pass can precede a single edit pass.
        var findings = reviews.Where(r => Findings(r) is { Count: > 0 }).Select(r => Timestamp(r, "received"))
            .Where(t => t.HasValue).Order().ToArray();
        var mutations = events.Where(e => IsMutation(e["tool"]?.ToString()))
            .Select(e => Timestamp(e, "started")).Where(t => t.HasValue).Order().ToArray();
        var passes = 0;
        var next = 0;
        foreach (var mutation in mutations)
        {
            if (next == findings.Length || findings[next] > mutation) continue;
            passes++;
            while (next < findings.Length && findings[next] <= mutation) next++;
        }
        return passes;
    }

    private static string? CallerPath(string? text)
    {
        if (text is null || !text.TrimStart().StartsWith('{')) return text;
        try { return JsonNode.Parse(text)?["presentationPath"]?.ToString(); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static DateTimeOffset? Timestamp(JsonObject receipt, string field) =>
        DateTimeOffset.TryParse(receipt[field]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var timestamp) ? timestamp : null;

    private static bool Reviewed(JsonObject render, JsonObject review) =>
        Timestamp(review, "started") >= Timestamp(render, "started") && Findings(review) is not null &&
        review["images"]?.AsArray().Any(i => i?.ToString() == render["result"]!["sha256"]!.ToString()) == true;

    private static JsonArray? Findings(JsonObject review)
    {
        var response = review["response"] as JsonObject;
        if (response?["findings"] is JsonArray findings) return findings;
        if (response?["action"]?.ToString() != "finish" || response["payload"] is not JsonValue payload ||
            !payload.TryGetValue<string>(out var json)) return null;
        try { return (JsonNode.Parse(json) as JsonObject)?["findings"] as JsonArray; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
