using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

// Independent evidence for the authorized pinned public checkout. No commands
// are executed here except read-only git identity checks; workflow observations
// must establish dependency installation and the repository's own checks.
internal static class LiveCodeEvidence
{
    internal static async Task ObserveCheckoutAsync(string root, JsonObject run)
    {
        var repository = Path.GetFullPath(Path.Combine(root, run["workspace_relative"]!.ToString(), "repository"));
        if (!Directory.Exists(Path.Combine(repository, ".git"))) return;
        run["checkout_head"] = await Git(repository, "rev-parse", "HEAD");
        run["checkout_base"] = await Git(repository, "rev-parse", LiveWorkflowEvaluation.Base + "^{commit}");
        if (run["repository_manifest"] is not null) return;
        var manifest = new JsonObject();
        foreach (var file in new[] { "AGENTS.md", ".nvmrc", ".python-version", "package.json", "pyproject.toml", ".github/workflows/quality_pipeline.yml" })
        {
            var path = Path.Combine(repository, file);
            if (File.Exists(path)) manifest[file] = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), ["content"] = await File.ReadAllTextAsync(path) };
        }
        // Only pin instructions from the requested head, never from an initial
        // default branch that the workflow has not checked out yet.
        if (run["checkout_head"]?.ToString() == LiveWorkflowEvaluation.Head) run["repository_manifest"] = manifest;
    }

    internal static JsonArray Commands(JsonObject run, string repository)
    {
        var commands = new JsonArray();
        foreach (var evt in run["events"]!.AsArray())
            foreach (var call in (evt?["result"]?["toolExecutions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (call["completionObserved"]?.GetValue<bool>() != true || call["conflictingCompletion"]?.GetValue<bool>() != false ||
                    call["argumentsJson"]?.ToString() is not { } raw) continue;
                JsonNode? arguments;
                try { arguments = JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { continue; }
                var command = (arguments?["command"] ?? arguments?["cmd"] ?? arguments?["script"])?.ToString();
                if (command is null) continue;
                foreach (var terminal in (call["terminals"] as JsonArray ?? []).OfType<JsonObject>())
                    if (terminal["exitCode"] is not null && terminal["workingDirectory"]?.ToString() is { } cwd && Path.GetFullPath(cwd) == repository)
                        commands.Add(new JsonObject { ["command"] = command, ["exit_code"] = terminal["exitCode"]!.DeepClone(),
                            ["output"] = terminal["text"]?.DeepClone(), ["tool_call_id"] = call["toolCallId"]?.DeepClone() });
            }
        return commands;
    }

    internal static JsonObject Verify(JsonObject run, string repository)
    {
        var commands = Commands(run, repository);
        bool Executed(params string[] accepted) => commands.Any(c => accepted.Contains(c!["command"]!.ToString(), StringComparer.Ordinal));
        bool Installed(params string[] accepted) => commands.Any(c => c!["exit_code"]!.GetValue<long>() == 0 && accepted.Contains(c["command"]!.ToString(), StringComparer.Ordinal));
        bool Version(string command, string expected) => commands.Any(c => c!["command"]!.ToString() == command && c["exit_code"]!.GetValue<long>() == 0 && c["output"]?.ToString().Trim().TrimStart('v') == expected);
        var checks = new JsonObject
        {
            ["pinned_head"] = run["checkout_head"]?.ToString() == LiveWorkflowEvaluation.Head,
            ["pinned_base"] = run["checkout_base"]?.ToString() == LiveWorkflowEvaluation.Base,
            ["manifest_observed"] = run["repository_manifest"] is not null,
            ["node_toolchain"] = Version("node --version", "24.20.0"),
            ["pnpm_toolchain"] = Version("pnpm --version", "10.34.5"),
            ["python_toolchain"] = Version("python --version", "Python 3.11.13") || Version("python3 --version", "Python 3.11.13"),
            ["node_dependencies"] = Installed("pnpm i", "pnpm install", "pnpm install --frozen-lockfile"),
            ["python_dependencies"] = Installed("uv sync --group tests", "uv sync --group tests --system-certs", "uv sync --group tests --system-certs --python 3.11.13"),
            ["python_lint"] = Executed("uv run ruff check .", "uv tool run ruff==0.15.22 check ."),
            ["python_tests"] = Executed("uv run pytest", "uv run pytest -v", "uv run pytest --junitxml=test-results.xml"),
            ["node_tests"] = Executed("pnpm test", "pnpm recursive run test")
        };
        run["observed_commands"] = commands; run["verified_checks"] = checks;
        run["required_checks_verified"] = checks.All(p => p.Value!.GetValue<bool>());
        return checks;
    }

    private static async Task<string?> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await error; return process.ExitCode == 0 ? (await output).Trim() : null;
    }
}
