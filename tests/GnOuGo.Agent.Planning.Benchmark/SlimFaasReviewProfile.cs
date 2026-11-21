using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// The campaign's independent check list for the local PR target. The planner
// receives the repository instructions and has no dependency on this verifier.
internal static class SlimFaasReviewProfile
{
    internal static readonly string[] ManifestPaths = ["AGENTS.md", "global.json", ".github/workflows/main.yml",
        ".github/workflows/dashboard.yml", ".github/workflows/SiteBuild.yml", ".agents/skills/slimfaas-build-test/SKILL.md",
        "src/SlimFaasSite/package.json", "src/SlimFaasPlanetSaver/package.json", "src/SlimFaas/ClientApp/package.json",
        "src/SlimFaasMcp/ClientApp/package.json", "client/python/slimfaas-client/pyproject.toml", ".bin/test-annotation-policy.py"];

    internal static string Cluster(string workspaceRelative) =>
        "gnougo-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRelative)))[..18];

    internal static string KindCommand(string cluster) =>
        $"KUBECONFIG=./{cluster}.kubeconfig KIND_EXPERIMENTAL_PROVIDER=podman python3 .bin/test-annotation-policy.py --cluster {cluster}";

    internal static async Task<JsonObject> ObserveClusterAsync(string relative, string workspace, CancellationToken ct)
    {
        var name = Cluster(relative);
        var config = Path.Combine(workspace, relative, "repository", name + ".kubeconfig");
        var evidence = new JsonObject { ["cluster"] = name, ["kubeconfig_absent"] = !Path.Exists(config) &&
            new FileInfo(config).LinkTarget is null };
        async Task<bool> Query(string executable, string[] arguments, Func<string, bool> absent)
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            start.Environment["KUBECONFIG"] = config;
            start.Environment["KIND_EXPERIMENTAL_PROVIDER"] = "podman";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                var result = process.ExitCode == 0 && absent(await stdout);
                evidence[executable] = new JsonObject { ["exit_code"] = process.ExitCode, ["absent"] = result };
                return result;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
            {
                evidence[executable] = new JsonObject { ["error"] = error.GetType().Name };
                return false;
            }
        }
        var clusters = await Query("kind", ["get", "clusters"], output => !output.Split('\n', StringSplitOptions.TrimEntries).Contains(name, StringComparer.Ordinal));
        var containers = await Query("podman", ["ps", "--all", "--filter", "label=io.x-k8s.kind.cluster=" + name, "--format", "{{.ID}}"], string.IsNullOrWhiteSpace);
        evidence["absent"] = clusters && containers && evidence["kubeconfig_absent"]!.GetValue<bool>();
        return evidence;
    }

    internal static void Verify(JsonObject run, string repository, JsonArray commands, HashSet<string> inconclusive, JsonObject checks)
    {
        var cluster = Cluster(run["workspace_relative"]!.GetValue<string>());
        var manifest = run["repository_manifest"] as JsonObject;
        checks["pinned_check_instructions"] = ManifestPaths.All(file => manifest?[file]?["git_blob"]?.ToString() is { Length: 40 });
        checks["kind_cluster_absent_before"] = run["kind_cluster_before"]?["absent"]?.GetValue<bool>() == true;
        var statuses = new JsonObject();
        var failures = new JsonObject();
        void Check(string name, string directory, params string[] candidates)
        {
            bool Matches(string command, string cwd) =>
                cwd == directory && candidates.Contains(command, StringComparer.Ordinal) ||
                cwd == "." && directory != "." && candidates.Any(c => command == $"cd {directory} && {c}");
            var matches = commands.OfType<JsonObject>().Where(c => Matches(c["command"]!.ToString(),
                Path.GetRelativePath(repository, c["cwd"]!.ToString()).Replace(Path.DirectorySeparatorChar, '/'))).ToArray();
            var failed = matches.Where(c => c["exit_code"]!.GetValue<long>() != 0).ToArray();
            var uncertain = inconclusive.Any(c => Matches(c, directory) || Matches(c, "."));
            var blocked = failed.Any(c => c["exit_code"]!.GetValue<long>() is 126 or 127 &&
                (c["output"]?.ToString().Contains("command not found", StringComparison.OrdinalIgnoreCase) == true ||
                 c["output"]?.ToString().Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true));
            statuses[name] = uncertain ? "inconclusive" : blocked ? "environment_blocked" :
                failed.Length > 0 ? "failed" : matches.Length == 0 ? "not_executed" : "passed";
            checks[name] = !uncertain && !blocked && matches.Length != 0;
            if (failed.Length > 0)
                failures[name] = new JsonArray(failed.Select(c => (JsonNode)new JsonObject { ["command"] = c["command"]!.DeepClone(),
                    ["exit_code"] = c["exit_code"]!.DeepClone(), ["tool_call_id"] = c["tool_call_id"]?.DeepClone() }).ToArray());
        }
        Check("dotnet_restore", ".", "dotnet restore");
        Check("dotnet_build", ".", "dotnet build", "dotnet build --no-restore", "dotnet build -p:SkipClientAppBuild=true");
        Check("dotnet_tests", ".", "dotnet test", "dotnet test --no-build", "dotnet test --collect \"Code Coverage;Format=cobertura\" --verbosity normal");
        Check("dotnet_client_tests", ".", "dotnet test client/dotnet/SlimFaasClient/tests/SlimFaasClient.Tests/SlimFaasClient.Tests.csproj");
        Check("python_client_install", "client/python/slimfaas-client", "uv sync --extra dev", "uv sync --frozen --extra dev");
        Check("python_client_tests", "client/python/slimfaas-client", "uv run pytest", "uv run --frozen pytest");
        foreach (var script in new[] { "raft-recovery-harness", "podman-compose-helper", "python-client-coverage",
                     "fibonacci-healthcheck", "memory-lab-report", "annotation-policy-cleanup", "status-stream-benchmark", "zlinq-benchmark" })
            Check(script.Replace('-', '_'), ".", $"python .bin/test-{script}.py", $"python3 .bin/test-{script}.py");
        foreach (var (prefix, directory, scripts) in new[] {
                     ("site", "src/SlimFaasSite", new[] { "lint", "typecheck", "test", "build" }),
                     ("planet", "src/SlimFaasPlanetSaver", new[] { "lint", "coverage" }) })
        {
            Check(prefix + "_install", directory, "pnpm install --frozen-lockfile", "pnpm i --frozen-lockfile");
            foreach (var script in scripts) Check(prefix + "_" + script, directory, "pnpm " + script, "pnpm run " + script);
        }
        foreach (var (prefix, directory) in new[] { ("dashboard", "src/SlimFaas/ClientApp"), ("mcp_dashboard", "src/SlimFaasMcp/ClientApp") })
        {
            Check(prefix + "_install", directory, "npm ci --ignore-scripts", "npm ci");
            Check(prefix + "_tests", directory, "npm test", "npm run test");
        }
        Check("traffic_integration", "src/SlimFaas/ClientApp", "npm run test:e2e");
        Check("native_publish", ".", "dotnet publish src/SlimFaas/SlimFaas.csproj -c Release -r " + RuntimeInformation.RuntimeIdentifier + " -o artifacts/native-local/runtime");
        Check("native_integration", ".", "python .bin/test-native-local.py --runtime artifacts/native-local/runtime/SlimFaas",
            "python3 .bin/test-native-local.py --runtime artifacts/native-local/runtime/SlimFaas");
        Check("kind_integration", ".", KindCommand(cluster));
        run["required_check_status"] = statuses;
        run["required_check_failures"] = failures;
    }
}
