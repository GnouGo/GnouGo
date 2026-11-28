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
            if (item?["command"]?.ToString() is not ("slide.notes" or "chart.data" or "table.setCell" or
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

    internal static async Task<JsonObject> VerifyAsync(JsonObject report, string workspace, WorkflowRun journal, CancellationToken ct)
    {
        var folder = Path.Combine(workspace, report["workspace_relative"]!.ToString());
        var evidence = new JsonObject { ["version"] = Version, ["passed"] = false };
        if (journal.Result?.Success != true || !journal.FinalizationCompleted) return evidence;
        var events = report["events"]!.AsArray().OfType<JsonObject>().Where(e => e["server"]?.ToString() == "DeckCraft" && e["error"]?.GetValue<bool>() == false).ToArray();
        var reopened = Array.FindLastIndex(events, e => e["tool"]?.ToString() == "open_presentation" &&
            e["arguments"]?["path"]?.ToString() == Path.Combine(folder, "after.pptx"));
        var finalRenders = events.Skip(reopened + 1).Where(e => e["tool"]?.ToString() == "render_slide" && e["result"]?["sha256"] is not null).ToArray();
        var coverage = finalRenders.Select(e => e["arguments"]?["slide"]?.GetValue<int>() ?? 0).ToHashSet();
        var reviews = report["vision_receipts"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var inspected = reviews.Where(r => r["response"] is not null).SelectMany(r => r["images"]!.AsArray()).Select(n => n!.ToString()).ToHashSet();
        var correctOutput = journal.Result.Outputs?["presentationPath"]?.ToString() == report["workspace_relative"]!.ToString() + "/after.pptx";
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Fixture("verify-deck.py")); start.ArgumentList.Add(folder);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var err = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); } catch { if (!process.HasExited) process.Kill(true); throw; }
        evidence["structural"] = process.ExitCode == 0 ? JsonNode.Parse(await output) : JsonValue.Create(await err);
        evidence["caller_output_matches"] = correctOutput;
        evidence["reopened_slide_coverage"] = coverage.SetEquals(Enumerable.Range(0, 8)) && reopened >= 0;
        evidence["final_vision_coverage"] = finalRenders.Length >= 8 && finalRenders.All(e => inspected.Contains(e["result"]!["sha256"]!.ToString()));
        evidence["passed"] = process.ExitCode == 0 && correctOutput && evidence["reopened_slide_coverage"]!.GetValue<bool>() &&
            evidence["final_vision_coverage"]!.GetValue<bool>() && FinalReviewsClear(finalRenders, reviews);
        return evidence;
    }

    private static bool FinalReviewsClear(JsonObject[] renders, JsonObject[] reviews) => renders.All(render =>
        reviews.Any(review => review["images"]!.AsArray().Any(i => i?.ToString() == render["result"]!["sha256"]!.ToString()) &&
            review["response"]?["findings"] is JsonArray { Count: 0 }));
}
