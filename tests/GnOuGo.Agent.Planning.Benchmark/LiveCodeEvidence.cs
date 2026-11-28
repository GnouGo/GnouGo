using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;

// Independent evidence for the authorized pinned checkout. No commands
// are executed here except read-only git identity checks; workflow observations
// must establish dependency installation and the repository's own checks.
internal static class LiveCodeEvidence
{
    private static readonly CopilotCoreJsonContext TerminalJson = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict });

    internal static async Task ObserveCheckoutAsync(string root, JsonObject run)
    {
        var repository = Path.GetFullPath(Path.Combine(root, run["workspace_relative"]!.ToString(), "repository"));
        if (!Directory.Exists(Path.Combine(repository, ".git"))) return;
        var expectedBase = run["target_base"]?.ToString() ?? LiveWorkflowEvaluation.Base;
        var expectedHead = run["target_head"]?.ToString() ?? LiveWorkflowEvaluation.Head;
        run["checkout_head"] = await Git(repository, "rev-parse", "HEAD");
        run["checkout_base"] = await Git(repository, "rev-parse", expectedBase + "^{commit}");
        if (PublishedReviewQualification.UsesPublicationChecks(run) &&
            run["checkout_head"]?.ToString() == expectedHead && run["tracked_package_manifests"] is null &&
            await Git(repository, "ls-tree", "-rz", "--name-only", expectedHead) is { } paths)
            run["tracked_package_manifests"] = new JsonArray(paths.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(path => path == "package.json" || path.EndsWith("/package.json", StringComparison.Ordinal))
                .Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        if (run["repository_manifest"] is not null) return;
        var manifest = new JsonObject();
        if (run["oracle_version"]?.ToString() == PublishedReviewQualification.LocalKindVersion)
        {
            if (run["checkout_head"]?.ToString() != expectedHead) return;
            foreach (var file in SlimFaasReviewProfile.ManifestPaths)
            {
                var revision = expectedHead + ":" + file;
                if (await Git(repository, "rev-parse", "--verify", revision) is { Length: 40 } blob &&
                    await Git(repository, "cat-file", "blob", blob) is { } content)
                    manifest[file] = new JsonObject { ["git_blob"] = blob, ["content"] = content };
            }
            run["repository_manifest"] = manifest;
            return;
        }
        foreach (var file in new[] { "AGENTS.md", ".nvmrc", ".python-version", "package.json", "pyproject.toml", ".github/workflows/quality_pipeline.yml" })
        {
            var path = Path.Combine(repository, file);
            if (File.Exists(path)) manifest[file] = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), ["content"] = await File.ReadAllTextAsync(path) };
        }
        // Only pin instructions from the requested head, never from an initial
        // default branch that the workflow has not checked out yet.
        if (run["checkout_head"]?.ToString() == expectedHead) run["repository_manifest"] = manifest;
    }

    internal static async Task<bool> IsPinnedReviewCheckoutAsync(string repository, string head, string notesDirectory) =>
        await Git(repository, "rev-parse", "HEAD") == head &&
        await Git(repository, "ls-tree", head, "--", notesDirectory) == "" &&
        await Git(repository, "status", "--porcelain=v1", "--untracked-files=no") == "";

    internal static JsonArray Commands(JsonObject run, string repository, HashSet<string>? inconclusive = null)
    {
        var commands = new JsonArray();
        var publication = PublishedReviewQualification.UsesPublicationChecks(run);
        foreach (var evt in run["events"]!.AsArray().Where(evt => !publication ||
            evt?["server"]?.ToString() == "GnOuGo.GithubCopilot.Mcp" && evt["tool"]?.ToString() == "code_agent_edit"))
        {
            if (evt?["result"]?["toolExecutions"] is not JsonArray calls) continue;
            IReadOnlyList<CopilotToolExecutionObservation>? observations = null;
            for (var index = 0; index < calls.Count; index++)
            {
                if (calls[index] is not JsonObject call || call["argumentsJson"]?.ToString() is not { } raw) continue;
                JsonNode? arguments;
                try { arguments = JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { continue; }
                var command = (arguments?["command"] ?? arguments?["cmd"] ?? arguments?["script"])?.ToString();
                if (command is null) continue;
                var terminals = call["terminals"] as JsonArray;
                if (publication && (call["isShellCommand"]?.GetValue<bool>() == true ||
                    terminals?.OfType<JsonObject>().Any(terminal => terminal["sourceToolCallId"] is not null) == true))
                {
                    observations ??= ReadCopilotObservations(calls);
                    var verified = observations.Count == calls.Count ? observations[index].GetVerifiedTerminals(observations) : null;
                    terminals = verified is null ? null : JsonSerializer.SerializeToNode(verified, CopilotCoreJsonContext.Default.IReadOnlyListCopilotTerminalObservation)?.AsArray();
                }
                if (call["completionObserved"]?.GetValue<bool>() != true || call["conflictingCompletion"]?.GetValue<bool>() != false ||
                    terminals is not { Count: > 0 })
                {
                    inconclusive?.Add(command);
                    continue;
                }
                foreach (var terminal in terminals.OfType<JsonObject>())
                {
                    if (terminal["workingDirectory"]?.ToString() is not { } cwd || !Path.IsPathFullyQualified(cwd) ||
                        !long.TryParse(terminal["exitCode"]?.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exit))
                    {
                        inconclusive?.Add(command);
                        continue;
                    }
                    var directory = Path.GetFullPath(cwd);
                    if (directory == repository || publication &&
                        directory.StartsWith(Path.TrimEndingDirectorySeparator(repository) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        commands.Add(new JsonObject { ["command"] = command, ["cwd"] = cwd, ["exit_code"] = exit,
                            ["output"] = terminal["text"]?.DeepClone(), ["tool_call_id"] = call["toolCallId"]?.DeepClone() });
                }
            }
        }
        return commands;
    }

    private static IReadOnlyList<CopilotToolExecutionObservation> ReadCopilotObservations(JsonArray calls)
    {
        try
        {
            var observations = calls.Deserialize(TerminalJson.IReadOnlyListCopilotToolExecutionObservation);
            if (observations is null || observations.Any(call => call?.Terminals is null || string.IsNullOrWhiteSpace(call.ToolCallId) || call.Terminals.Any(terminal => terminal is null)) ||
                observations.DistinctBy(call => (call.CopilotSessionId, call.ToolCallId)).Count() != observations.Count) return [];
            return observations;
        }
        catch (JsonException) { return []; }
    }

    internal static JsonObject Verify(JsonObject run, string repository)
    {
        var inconclusive = new HashSet<string>(StringComparer.Ordinal);
        var commands = Commands(run, repository, inconclusive);
        bool Executed(params string[] accepted) => commands.Any(c => accepted.Contains(c!["command"]!.ToString(), StringComparer.Ordinal));
        bool Installed(params string[] accepted) => commands.Any(c => c!["exit_code"]!.GetValue<long>() == 0 && accepted.Contains(c["command"]!.ToString(), StringComparer.Ordinal));
        bool Version(string command, string expected) => commands.Any(c => c!["command"]!.ToString() == command && c["exit_code"]!.GetValue<long>() == 0 && c["output"]?.ToString().Trim().TrimStart('v') == expected);
        var checks = new JsonObject
        {
            ["pinned_head"] = run["checkout_head"]?.ToString() == (run["target_head"]?.ToString() ?? LiveWorkflowEvaluation.Head),
            ["pinned_base"] = run["checkout_base"]?.ToString() == (run["target_base"]?.ToString() ?? LiveWorkflowEvaluation.Base),
            ["manifest_observed"] = run["repository_manifest"] is not null,
        };
        if (run["oracle_version"]?.ToString() == PublishedReviewQualification.LocalKindVersion)
            SlimFaasReviewProfile.Verify(run, repository, commands, inconclusive, checks);
        else if (PublishedReviewQualification.UsesPublicationChecks(run))
        {
            var package = run["repository_manifest"]?["package.json"]?["content"]?.ToString();
            var scripts = package is null ? null : JsonNode.Parse(package)?["scripts"];
            var packageManifests = (run["tracked_package_manifests"] as JsonArray)?.Select(path => path!.ToString()).ToHashSet(StringComparer.Ordinal);
            var failures = new JsonObject();
            bool Observed(string name, params string[] accepted)
            {
                var rootScript = name is "node_tests" or "integration_tests";
                bool Matches(string command) => accepted.Contains(rootScript ? WorkspaceCommand(command) : command, StringComparer.Ordinal);
                var matching = commands.OfType<JsonObject>().Where(c => Matches(c["command"]!.ToString())).ToArray();
                var failed = matching.Where(c => c["exit_code"]!.GetValue<long>() != 0).ToArray();
                if (failed.Length > 0) failures[name] = new JsonArray(failed.Select(c => (JsonNode)new JsonObject {
                    ["command"] = c["command"]!.DeepClone(), ["exit_code"] = c["exit_code"]!.DeepClone() }).ToArray());
                return matching.Any(c => !rootScript || RunsRootScript(c)) && !inconclusive.Any(Matches);
            }
            bool RunsRootScript(JsonObject receipt)
            {
                var command = receipt["command"]!.ToString();
                var relative = Path.GetRelativePath(repository, receipt["cwd"]!.ToString()).Replace(Path.DirectorySeparatorChar, '/');
                if (relative == "." || command != WorkspaceCommand(command)) return true;
                if (packageManifests?.Contains("package.json") != true) return false;
                while (relative.Length > 0)
                {
                    if (packageManifests.Contains(relative + "/package.json")) return false;
                    var separator = relative.LastIndexOf('/');
                    if (separator < 0) break;
                    relative = relative[..separator];
                }
                return true;
            }
            static string WorkspaceCommand(string command)
            {
                const string longFlag = "pnpm --workspace-root ", shortFlag = "pnpm -w ";
                if (command.StartsWith(longFlag, StringComparison.Ordinal)) return "pnpm " + command[longFlag.Length..];
                return command.StartsWith(shortFlag, StringComparison.Ordinal) ? "pnpm " + command[shortFlag.Length..] : command;
            }
            checks["node_dependencies"] = Observed("node_dependencies", "pnpm i", "pnpm install", "pnpm install --frozen-lockfile");
            checks["python_dependencies"] = Observed("python_dependencies", "uv sync --group tests", "uv sync --frozen --group tests",
                "uv sync --native-tls --group tests --group ci", "uv sync --native-tls --group tests --group ci --extra jobs",
                "UV_HTTP_TIMEOUT=1000 uv sync --native-tls --group tests --group ci", "UV_HTTP_TIMEOUT=1000 uv sync --native-tls --group tests --group ci --extra jobs");
            checks["python_lint"] = Observed("python_lint", "uv run ruff check .", "uv run --frozen ruff check .", "uv tool run ruff==0.15.22 check .", "ruff check .");
            checks["python_tests"] = Observed("python_tests", "uv run pytest", "uv run --frozen pytest", "uv run --frozen pytest tests", "uv run pytest tests",
                "uv run pytest -v", "uv run pytest --junitxml=test-results.xml",
                "uv run coverage run -m pytest --junitxml=dist/test-results.xml -W ignore::DeprecationWarning || exit 1",
                "uv run coverage run --omit=*.venv*,config.py,config-3.py,_remote_module_non_scriptable.py -m pytest --junitxml=dist/test-results.xml -W ignore::DeprecationWarning || exit 1");
            checks["node_tests"] = scripts?["test"] is not null && Observed("node_tests", "pnpm test", "pnpm run test", "pnpm recursive run test");
            checks["integration_tests"] = scripts?["e2e"] is not null && Observed("integration_tests", "pnpm e2e", "pnpm run e2e", "pnpm exec playwright test");
            run["required_check_failures"] = failures;
        }
        else
        {
            checks["node_toolchain"] = Version("node --version", "24.20.0");
            checks["pnpm_toolchain"] = Version("pnpm --version", "10.34.5");
            checks["python_toolchain"] = Version("python --version", "Python 3.11.13") || Version("python3 --version", "Python 3.11.13");
            checks["node_dependencies"] = Installed("pnpm i", "pnpm install", "pnpm install --frozen-lockfile");
            checks["python_dependencies"] = Installed("uv sync --group tests", "uv sync --group tests --system-certs", "uv sync --group tests --system-certs --python 3.11.13");
            checks["python_lint"] = Executed("uv run ruff check .", "uv tool run ruff==0.15.22 check .");
            checks["python_tests"] = Executed("uv run pytest", "uv run pytest -v", "uv run pytest --junitxml=test-results.xml");
            checks["node_tests"] = Executed("pnpm test", "pnpm recursive run test");
        }
        run["observed_commands"] = commands; run["verified_checks"] = checks;
        run["required_checks_verified"] = checks.All(p => p.Value!.GetValue<bool>());
        return checks;
    }

    internal static string? SubmissionFinding(JsonObject run, JsonObject submission)
    {
        if (run["required_checks_verified"]?.GetValue<bool>() != true || run["required_check_failures"] is not JsonObject failures)
            return "Required repository checks are inconclusive.";
        var verdict = submission["event"]?.ToString();
        if (verdict == "APPROVE") return failures.Count == 0 ? null : "Approval requires all mandatory checks to pass.";
        if (verdict != "REQUEST_CHANGES") return "Unknown review verdict.";
        var body = submission["body"]?.ToString() ?? "";
        var explanation = body;
        foreach (var (name, receipts) in failures)
        {
            var references = receipts!.AsArray().OfType<JsonObject>().Select(c => c["command"]!.ToString())
                .Append(name.Replace('_', ' ')).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(reference => reference.Length).ToArray();
            if (!references.Any(reference => body.Contains(reference, StringComparison.OrdinalIgnoreCase)))
                return "The review must identify and explain the observed failure of " + name + ".";
            foreach (var reference in references)
                explanation = explanation.Replace(reference, "", StringComparison.OrdinalIgnoreCase);
        }
        if (failures.Count > 0 && !explanation.Any(char.IsLetterOrDigit))
            return "The review identifies failed checks but needs an explanation of their failures.";
        return null;
    }

    // Compare the actual local report with independent terminal completions.
    // Optional provenance claims must be truthful; no new report fields beyond
    // the requested commands and exit codes are imposed.
    internal static JsonObject VerifyReport(JsonObject run, JsonNode? review, string repository, string reportPath)
    {
        var findings = new List<string>();
        var actual = Commands(run, repository).OfType<JsonObject>().ToList();
        var unmatched = actual.ToList();
        if (review is not JsonObject report || report["commands"] is not JsonArray reported || reported.Count == 0)
            findings.Add("review_command_proofs_missing");
        else
        {
            foreach (var entry in reported)
            {
                if (entry is not JsonObject command || Text(command["command"]) is not { Length: > 0 } text ||
                    !ExitCode(command["exitCode"] ?? command["exit_code"], out var exit) ||
                    command["exitCode"] is not null && command["exit_code"] is not null &&
                    (!ExitCode(command["exit_code"], out var other) || exit != other))
                { findings.Add("review_command_proof_malformed"); continue; }
                var match = unmatched.FindIndex(observed => observed["command"]!.ToString() == text &&
                    ExitCode(observed["exit_code"], out var code) && code == exit &&
                    MatchesOptional(command, "toolCallId", observed["tool_call_id"]?.ToString()) &&
                    MatchesOptional(command, "tool_call_id", observed["tool_call_id"]?.ToString()) &&
                    MatchesOptional(command, "output", observed["output"]?.ToString()) &&
                    MatchesOptional(command, "cwd", repository) && MatchesOptional(command, "workingDirectory", repository));
                if (match < 0) findings.Add("review_command_proof_not_observed");
                else unmatched.RemoveAt(match);
            }
            if (unmatched.Count != 0) findings.Add("review_observed_commands_omitted");
            if (!MatchesOptional(report, "head", run["checkout_head"]?.ToString()) ||
                !MatchesOptional(report, "base", run["checkout_base"]?.ToString()) ||
                !MatchesOptional(report, "pullRequestUrl", run["inputs"]?["pullRequestUrl"]?.ToString()))
                findings.Add("review_provenance_claim_not_observed");
        }
        var events = run["events"]!.AsArray().OfType<JsonObject>().ToArray();
        var clones = events.Where(e => e["tool"]?.ToString() == "git_clone").ToArray();
        var relative = run["workspace_relative"]?.ToString();
        if (clones.Length != 1 || clones[0]["error"]?.GetValue<bool>() != false ||
            clones[0]["result"]?["success"]?.GetValue<bool>() != true ||
            clones[0]["arguments"]?["branch"]?.ToString() != LiveWorkflowEvaluation.Head ||
            clones[0]["arguments"]?["remoteUrl"]?.ToString() is not ("https://github.com/AxaFrance/SmartGuide" or "https://github.com/AxaFrance/SmartGuide.git") ||
            clones[0]["arguments"]?["targetDirectory"]?.ToString() is not { } target ||
            target != repository && target != relative + "/repository")
            findings.Add("single_pinned_clone_not_observed");
        if (!events.Any(e => e["tool"]?.ToString() == "document_write" && e["error"]?.GetValue<bool>() == false &&
            e["result"]?["success"]?.GetValue<bool>() == true && e["result"]?["filePath"]?.ToString() == reportPath))
            findings.Add("review_writer_receipt_missing");
        return new JsonObject { ["version"] = LiveWorkflowOracles.CodeBusinessVersion, ["passed"] = findings.Count == 0,
            ["observed_command_count"] = actual.Count,
            ["findings"] = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };

        static bool MatchesOptional(JsonObject value, string key, string? expected)
            => !value.ContainsKey(key) || Text(value[key]) is { } text && text == expected;
        static string? Text(JsonNode? value) => value is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;
        static bool ExitCode(JsonNode? value, out long result)
        {
            result = 0;
            return value is JsonValue && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
        }
    }

    private static async Task<string?> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await error; return process.ExitCode == 0 ? (await output).TrimEnd('\r', '\n') : null;
    }
}
