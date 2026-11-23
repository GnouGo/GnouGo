using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CodeReportOracleTests
{
    private static readonly string Repository = Path.Combine(Path.GetTempPath(), "oracle-review", "repository");
    private static readonly string ReportPath = Path.Combine(Path.GetTempPath(), "oracle-review", "review.json");

    [Fact]
    public void ExactCommandsAndOptionalOriginalProvenancePassWithoutInventingRequiredFields()
    {
        var run = Run(); var report = Report();
        AssertPass(run, report);
        report["head"] = LiveWorkflowEvaluation.Head;
        report["base"] = LiveWorkflowEvaluation.Base;
        report["pullRequestUrl"] = run["inputs"]!["pullRequestUrl"]!.DeepClone();
        report["commands"]![0]!["toolCallId"] = "call-1";
        report["commands"]![0]!["output"] = "10.34.5";
        report["commands"]![0]!["cwd"] = Repository;
        AssertPass(run, report);
    }

    [Theory]
    [InlineData("missing", "review_command_proofs_missing")]
    [InlineData("empty", "review_command_proofs_missing")]
    [InlineData("omitted", "review_observed_commands_omitted")]
    [InlineData("invented", "review_command_proof_not_observed")]
    [InlineData("false_exit", "review_command_proof_not_observed")]
    [InlineData("duplicate", "review_command_proof_not_observed")]
    [InlineData("string_exit", "review_command_proof_malformed")]
    [InlineData("conflicting_exit", "review_command_proof_malformed")]
    [InlineData("invented_output", "review_command_proof_not_observed")]
    [InlineData("wrong_call", "review_command_proof_not_observed")]
    [InlineData("wrong_cwd", "review_command_proof_not_observed")]
    [InlineData("wrong_head", "review_provenance_claim_not_observed")]
    [InlineData("wrong_pr", "review_provenance_claim_not_observed")]
    [InlineData("duplicate_clone", "single_pinned_clone_not_observed")]
    [InlineData("wrong_clone", "single_pinned_clone_not_observed")]
    [InlineData("missing_writer", "review_writer_receipt_missing")]
    [InlineData("unknown_completion", "review_command_proof_not_observed")]
    [InlineData("conflicting_completion", "review_command_proof_not_observed")]
    public void RejectsOmittedOrUnsupportedReportProofs(string variant, string finding)
    {
        var run = Run(); var report = Report(); var commands = report["commands"]!.AsArray();
        switch (variant)
        {
            case "missing": report.Remove("commands"); break;
            case "empty": commands.Clear(); break;
            case "omitted": commands.RemoveAt(1); break;
            case "invented": commands[0]!["command"] = "echo success"; break;
            case "false_exit": commands[1]!["exitCode"] = 0; break;
            case "duplicate": commands.Add(commands[0]!.DeepClone()); break;
            case "string_exit": commands[0]!["exitCode"] = "0"; break;
            case "conflicting_exit": commands[0]!["exit_code"] = 1; break;
            case "invented_output": commands[0]!["output"] = "invented"; break;
            case "wrong_call": commands[0]!["toolCallId"] = "invented"; break;
            case "wrong_cwd": commands[0]!["cwd"] = Path.GetTempPath(); break;
            case "wrong_head": report["head"] = "invented"; break;
            case "wrong_pr": report["pullRequestUrl"] = "https://github.com/other/project/pull/1"; break;
            case "duplicate_clone": run["events"]!.AsArray().Add(run["events"]![0]!.DeepClone()); break;
            case "wrong_clone": run["events"]![0]!["arguments"]!["branch"] = "main"; break;
            case "missing_writer": run["events"]!.AsArray().RemoveAt(2); break;
            case "unknown_completion": run["events"]![1]!["result"]!["toolExecutions"]![0]!["completionObserved"] = false; break;
            case "conflicting_completion": run["events"]![1]!["result"]!["toolExecutions"]![0]!["conflictingCompletion"] = true; break;
        }
        var result = LiveCodeEvidence.VerifyReport(run, report, Repository, ReportPath);
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Contains(finding, result["findings"]!.AsArray().Select(f => f!.ToString()));
    }

    [Fact]
    public void RepeatedActualCommandsRequireTheirSeparateObservedOccurrences()
    {
        var run = Run(); var report = Report();
        run["events"]![1]!["result"]!["toolExecutions"]!.AsArray().Add(Call("call-3", "pnpm --version", 0, "10.34.5"));
        var result = LiveCodeEvidence.VerifyReport(run, report, Repository, ReportPath);
        Assert.Contains("review_observed_commands_omitted", result["findings"]!.AsArray().Select(f => f!.ToString()));
        report["commands"]!.AsArray().Add(report["commands"]![0]!.DeepClone());
        AssertPass(run, report);
    }

    [Fact]
    public void SnakeCaseExitCodesRemainSupported()
    {
        var report = Report();
        foreach (var command in report["commands"]!.AsArray().OfType<JsonObject>())
        { command["exit_code"] = command["exitCode"]!.DeepClone(); command.Remove("exitCode"); }
        AssertPass(Run(), report);
    }

    [Theory]
    [InlineData(LiveWorkflowOracles.CodeBusinessVersion, true)]
    [InlineData("real-workflows-v1", false)]
    public async Task ActualParsedReportIsRetainedOnlyForTheCapturedNewOracle(string version, bool retained)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-report-oracle-" + Guid.NewGuid().ToString("N"));
        const string relative = "workflows/test/report-proof";
        var folder = Path.Combine(root, relative);
        Directory.CreateDirectory(folder);
        try
        {
            var actual = new JsonObject { ["decision"] = "request_changes",
                ["findings"] = new JsonArray("Independently read file content."), ["commands"] = new JsonArray() };
            var path = Path.Combine(folder, "review.json");
            await File.WriteAllTextAsync(path, actual.ToJsonString(), TestContext.Current.CancellationToken);
            var run = new JsonObject { ["workspace_relative"] = relative, ["oracle_version"] = version,
                ["execution"] = new JsonObject { ["success"] = true },
                ["events"] = new JsonArray((JsonNode)new JsonObject { ["tool"] = "document_write", ["error"] = false,
                    ["arguments"] = new JsonObject { ["content"] = "Writer arguments do not define actual report proof." },
                    ["result"] = new JsonObject { ["success"] = true, ["filePath"] = path } }) };
            _ = await LiveWorkflowOracles.VerifyAsync("code", root, run, null!);
            if (retained)
            {
                Assert.True(JsonNode.DeepEquals(actual, run["review_document"]));
                Assert.Equal(PlanningGraphCompiler.Fingerprint(actual.ToJsonString()), run["review_document_sha256"]!.ToString());
                // Later disk changes and run serialization do not replace the
                // already observed document; the existing encrypted Save stores
                // these fields with the rest of the run.
                await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
                var restored = JsonNode.Parse(run.ToJsonString())!;
                Assert.True(JsonNode.DeepEquals(actual, restored["review_document"]));
                Assert.Equal(run["review_document_sha256"]!.ToString(), restored["review_document_sha256"]!.ToString());
            }
            else
            {
                Assert.False(run.ContainsKey("review_document"));
                Assert.False(run.ContainsKey("review_document_sha256"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("pnpm run e2e", "browser", true)]
    [InlineData("echo pnpm run e2e", "browser", false)]
    [InlineData("pnpm run e2e", "../unrelated", false)]
    [InlineData("pnpm run e2e --help", "browser", false)]
    public void PublishedReviewUsesThePinnedProjectsChecksAndCompletedCommands(string integration, string directory, bool expected)
    {
        var run = PublishedRun();
        var calls = run["events"]![1]!["result"]!["toolExecutions"]!.AsArray();
        var last = Call("integration", integration, 0, "Completed.");
        last["terminals"]![0]!["workingDirectory"] = Path.GetFullPath(Path.Combine(Repository, directory));
        calls.Add(last);
        LiveCodeEvidence.Verify(run, Repository);
        Assert.Equal(expected, run["required_checks_verified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("pnpm run test", "packages/member", false)]
    [InlineData("pnpm run test", "packages/member/tests", false)]
    [InlineData("pnpm run test", "packages/member-other", true)]
    [InlineData("pnpm --workspace-root run test", "packages/member", true)]
    public void APackageLocalTestRunCannotApproveTheRootWorkspaceSuite(string command, string workingDirectory, bool expected)
    {
        var run = PublishedRun();
        var calls = run["events"]![1]!["result"]!["toolExecutions"]!.AsArray();
        calls.RemoveAt(calls.Count - 1);
        var test = Call("unit", command, 0, "Completed.");
        test["terminals"]![0]!["workingDirectory"] = Path.Combine(Repository, workingDirectory);
        calls.Add(test);
        calls.Add(Call("integration", "pnpm run e2e", 0, "Completed."));
        LiveCodeEvidence.Verify(run, Repository);
        Assert.Equal(expected, run["verified_checks"]!["node_tests"]!.GetValue<bool>());
        Assert.Equal(expected, LiveCodeEvidence.SubmissionFinding(run, new() { ["event"] = "APPROVE", ["body"] = "Verified." }) is null);
    }

    [Fact]
    public async Task PackageBoundariesComeFromThePinnedGitTree()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var run = PublishedRun();
            run.Remove("tracked_package_manifests");
            var path = Path.Combine(root, run["workspace_relative"]!.ToString(), "repository");
            var member = Path.Combine(path, "packages", "member with spaces");
            Directory.CreateDirectory(member);
            LibGit2Sharp.Repository.Init(path);
            string head;
            using (var repository = new LibGit2Sharp.Repository(path))
            {
                await File.WriteAllTextAsync(Path.Combine(path, "package.json"), "{}", TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(member, "package.json"), "{}", TestContext.Current.CancellationToken);
                LibGit2Sharp.Commands.Stage(repository, "*");
                var author = new LibGit2Sharp.Signature("Fixture", "fixture@example.test", DateTimeOffset.UnixEpoch.AddDays(1));
                head = repository.Commit("Declare workspace and member", author, author, new LibGit2Sharp.CommitOptions()).Sha;
            }
            var temporaryPackage = Directory.CreateDirectory(Path.Combine(path, "temporary")).FullName;
            await File.WriteAllTextAsync(Path.Combine(temporaryPackage, "package.json"), "{}", TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(member, "package.json"));
            run["target_head"] = new string('0', 40);
            run["target_base"] = head;
            await LiveCodeEvidence.ObserveCheckoutAsync(root, run);
            Assert.Null(run["tracked_package_manifests"]);
            run["target_head"] = head;
            await LiveCodeEvidence.ObserveCheckoutAsync(root, run);
            Assert.Equal(new[] { "package.json", "packages/member with spaces/package.json" },
                run["tracked_package_manifests"]!.AsArray().Select(value => value!.ToString()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("passed", "APPROVE", "Verified.", true)]
    [InlineData("failed", "APPROVE", "All checks passed.", false)]
    [InlineData("failed", "REQUEST_CHANGES", "pnpm run e2e exited 1 when opening the checkout.", true)]
    [InlineData("failed", "REQUEST_CHANGES", "Looks fine.", false)]
    [InlineData("failed", "REQUEST_CHANGES", "pnpm run e2e", false)]
    [InlineData("two_failures", "REQUEST_CHANGES", "pnpm run test\npnpm run e2e", false)]
    [InlineData("two_failures", "REQUEST_CHANGES", "pnpm run test and pnpm run e2e both stopped because the test fixture was unavailable.", true)]
    [InlineData("missing", "APPROVE", "Verified.", false)]
    [InlineData("unknown", "APPROVE", "Verified.", false)]
    [InlineData("conflict_after_success", "APPROVE", "Verified.", false)]
    [InlineData("unrelated_failure", "APPROVE", "Verified.", true)]
    public void PublicationChecksTheVerdictBeforeConfirmationAndGitHubDispatch(string outcome, string verdict, string body, bool allowed)
    {
        var run = PublishedRun();
        var calls = run["events"]![1]!["result"]!["toolExecutions"]!.AsArray();
        var integration = Call("integration", "pnpm run e2e", outcome is "failed" or "two_failures" ? 1 : 0, "Test output");
        if (outcome == "unknown") integration["terminals"]![0]!.AsObject().Remove("exitCode");
        if (outcome != "missing") calls.Add(integration);
        if (outcome == "two_failures") calls.Add(Call("other-check", "pnpm run test", 1, "Test output"));
        if (outcome == "conflict_after_success")
        {
            var uncertain = integration.DeepClone();
            uncertain["conflictingCompletion"] = true;
            calls.Add(uncertain);
        }
        if (outcome == "unrelated_failure") calls.Add(Call("probe", "which optional-utility", 1, ""));
        LiveCodeEvidence.Verify(run, Repository);
        var finding = LiveCodeEvidence.SubmissionFinding(run, new() { ["event"] = verdict, ["body"] = body });
        Assert.Equal(allowed, finding is null);
    }

    [Theory]
    [InlineData(false, false, null, false)]
    [InlineData(true, false, null, true)]
    [InlineData(false, true, null, false)]
    [InlineData(true, true, null, true)]
    [InlineData(true, false, "other_session", false)]
    [InlineData(true, true, "duplicate_source", false)]
    [InlineData(true, true, "string_exit", false)]
    [InlineData(true, true, "failed_check", false)]
    public void PublicationRequiresTheOriginalCopilotTerminalCompletion(bool sourcePresent, bool deferred, string? change, bool allowApproval)
    {
        var run = PublishedRun();
        var calls = run["events"]![1]!["result"]!["toolExecutions"]!.AsArray();
        var check = Call("integration", "pnpm run e2e", 0, "Integration complete.");
        check["copilotSessionId"] = "checks";
        check["startedSequence"] = 1;
        check["completedSequence"] = 2;
        check["shellId"] = "shell";
        check["isShellCommand"] = true;
        var terminal = check["terminals"]![0]!;
        terminal["shellId"] = "shell";
        var source = new JsonObject
        {
            ["toolCallId"] = "terminal-read", ["copilotSessionId"] = "checks", ["startedSequence"] = 3,
            ["completedSequence"] = 4, ["completionObserved"] = true, ["toolSucceeded"] = true,
            ["conflictingCompletion"] = false, ["terminals"] = new JsonArray(terminal.DeepClone())
        };
        if (deferred) terminal["exitCode"] = null;
        else terminal["sourceToolCallId"] = "terminal-read";
        calls.Add(check);
        if (sourcePresent) calls.Add(source);
        if (change == "other_session") source["copilotSessionId"] = "another-invocation";
        if (change == "duplicate_source") calls.Add(source.DeepClone());
        if (change == "string_exit") source["terminals"]![0]!["exitCode"] = "0";
        if (change == "failed_check") source["terminals"]![0]!["exitCode"] = 1;

        LiveCodeEvidence.Verify(run, Repository);
        Assert.Equal(allowApproval || change == "failed_check", run["required_checks_verified"]!.GetValue<bool>());
        Assert.Equal(allowApproval, LiveCodeEvidence.SubmissionFinding(run, new() { ["event"] = "APPROVE", ["body"] = "Verified." }) is null);
        if (change == "failed_check")
            Assert.Null(LiveCodeEvidence.SubmissionFinding(run, new() { ["event"] = "REQUEST_CHANGES",
                ["body"] = "pnpm run e2e exited 1 when opening the checkout." }));
    }

    [Theory]
    [InlineData("npm ci --ignore-scripts --offline --no-audit --no-fund", 1, "npm error code ENOTCACHED", "environment_blocked")]
    [InlineData("npm ci --ignore-scripts --prefer-offline --no-fund", 0, "installed", "passed")]
    [InlineData("npm ci --no-audit --offline", 1, "lockfile mismatch", "failed")]
    [InlineData("npm ci --offline --dry-run", 0, "installed", "not_executed")]
    [InlineData("npm ci --offline || true", 0, "installed", "not_executed")]
    [InlineData("npm ci --offline --prefix=somewhere-else", 0, "installed", "not_executed")]
    public void DashboardInstallUsesTheOriginalDirectoryCommandAndExitReceipt(string command, int exit, string output, string expected)
    {
        var run = PublishedRun();
        run["oracle_version"] = PublishedReviewQualification.LocalKindVersion;
        var original = "cd src/SlimFaas/ClientApp && " + command;
        var receipt = Call("npm-install", original, exit, output);
        run["events"]![1]!["result"]!["toolExecutions"]!.AsArray().Add(receipt);

        LiveCodeEvidence.Verify(run, Repository);

        Assert.Equal(expected, run["required_check_status"]!["dashboard_install"]!.ToString());
        Assert.Equal("not_executed", run["required_check_status"]!["mcp_dashboard_install"]!.ToString());
        if (expected is "failed" or "environment_blocked")
        {
            var failure = run["required_check_failures"]!["dashboard_install"]![0]!;
            Assert.Equal(original, failure["command"]!.ToString());
            Assert.Equal((long)exit, failure["exit_code"]!.GetValue<long>());
            Assert.Equal("npm-install", failure["tool_call_id"]!.ToString());
        }
    }

    [Theory]
    [InlineData("restore SlimFaas.sln -p:SkipClientAppBuild=true --verbosity minimal", "dotnet_restore", "passed")]
    [InlineData("build SlimFaas.sln --no-restore -p:SkipClientAppBuild=true --verbosity minimal", "dotnet_build", "passed")]
    [InlineData("test SlimFaas.sln --no-build --no-restore -p:SkipClientAppBuild=true --verbosity minimal", "dotnet_tests", "passed")]
    [InlineData("test other.sln --no-build", "dotnet_tests", "not_executed")]
    [InlineData("test SlimFaas.sln --filter TestName=One", "dotnet_tests", "not_executed")]
    [InlineData("build SlimFaas.sln || true", "dotnet_build", "not_executed")]
    [InlineData("restore SlimFaas.sln --no-dependencies", "dotnet_restore", "not_executed")]
    public void SolutionChecksRecognizeOnlyTheExpectedSolutionAndScope(string arguments, string check, string expected)
    {
        var run = PublishedRun();
        run["oracle_version"] = PublishedReviewQualification.LocalKindVersion;
        run["events"]![1]!["result"]!["toolExecutions"]!.AsArray().Clear();
        run["events"]![1]!["result"]!["toolExecutions"]!.AsArray().Add(Call("solution-check", "dotnet " + arguments, 0, "Completed"));

        LiveCodeEvidence.Verify(run, Repository);

        Assert.Equal(expected, run["required_check_status"]![check]!.ToString());
    }

    [Fact]
    public void LocalKindReviewCannotApproveWithOnlyHistoricalProjectChecks()
    {
        var run = PublishedRun();
        run["oracle_version"] = PublishedReviewQualification.LocalKindVersion;
        run["events"]![1]!["result"]!["toolExecutions"]!.AsArray().Add(Call("historical", "pnpm run e2e", 0, "Passed"));
        LiveCodeEvidence.Verify(run, Repository);
        Assert.NotNull(LiveCodeEvidence.SubmissionFinding(run, new() { ["event"] = "APPROVE", ["body"] = "Verified." }));
        Assert.Equal("not_executed", run["required_check_status"]?["kind_integration"]?.ToString());
    }

    [Theory]
    [InlineData("correct", "passed")]
    [InlineData("other_cluster", "not_executed")]
    [InlineData("other_cwd", "not_executed")]
    [InlineData("unknown", "inconclusive")]
    [InlineData("failed", "failed")]
    [InlineData("unavailable", "environment_blocked")]
    [InlineData("masked", "not_executed")]
    public void KindCheckRequiresTheRunClusterKubeconfigAndTerminal(string variant, string expected)
    {
        var run = PublishedRun();
        run["oracle_version"] = PublishedReviewQualification.LocalKindVersion;
        var cluster = SlimFaasReviewProfile.Cluster(run["workspace_relative"]!.ToString());
        var command = SlimFaasReviewProfile.KindCommand(variant == "other_cluster" ? cluster + "-other" : cluster);
        var receipt = Call("kind-check", variant == "masked" ? command + " || true" : command,
            variant == "unavailable" ? 127 : variant == "failed" ? 1 : 0,
            variant == "unavailable" ? "python3: command not found" : "Test output");
        if (variant == "other_cwd") receipt["terminals"]![0]!["workingDirectory"] = Path.Combine(Repository, "nested");
        if (variant == "unknown") receipt["completionObserved"] = false;
        run["events"]![1]!["result"]!["toolExecutions"]!.AsArray().Add(receipt);
        LiveCodeEvidence.Verify(run, Repository);
        Assert.Equal(expected, run["required_check_status"]!["kind_integration"]!.ToString());
        Assert.Equal(expected is "passed" or "failed", run["verified_checks"]!["kind_integration"]!.GetValue<bool>());
        Assert.Equal(expected is "failed" or "environment_blocked", run["required_check_failures"]!["kind_integration"] is not null);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(127L, false)]
    public void SdkProbeChecksTheTerminalExitInItsOriginalIntegerRepresentation(long exit, bool expected)
    {
        var commands = new JsonArray((JsonNode)new JsonObject { ["command"] = "python3 probe.py", ["exit_code"] = exit });
        Assert.Equal(expected, LiveExecutionReadiness.ProbeCommandSucceeded(commands));
        commands.Add(commands[0]!.DeepClone());
        Assert.False(LiveExecutionReadiness.ProbeCommandSucceeded(commands));
    }

    [Fact]
    public void LocalToolchainIsProcessScopedAndRejectsChangedOrUnrelatedExecutables()
    {
        var bin = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var tool in new[] { "node", "npm", "pnpm" }) File.WriteAllText(Path.Combine(bin, tool), tool);
            var initial = Environment.GetEnvironmentVariable("PATH");
            var captured = SlimFaasReviewProfile.CaptureToolchain(bin);
            var options = new GnOuGo.AI.Core.McpServerOptions { EnvironmentVariables = new() { ["PATH"] = "previous" } };
            SlimFaasReviewProfile.ApplyToolchain(captured, options);
            Assert.Equal(bin + Path.PathSeparator + "previous", options.EnvironmentVariables["PATH"]);
            Assert.Equal(initial, Environment.GetEnvironmentVariable("PATH"));
            File.WriteAllText(Path.Combine(bin, "node"), "changed");
            Assert.Throws<InvalidDataException>(() => SlimFaasReviewProfile.ApplyToolchain(captured, options));
            File.WriteAllText(Path.Combine(bin, "git"), "other");
            Assert.Throws<ArgumentException>(() => SlimFaasReviewProfile.CaptureToolchain(bin));
        }
        finally { Directory.Delete(bin, recursive: true); }
    }

    private static JsonObject PublishedRun()
    {
        var run = Run();
        run["oracle_version"] = PublishedReviewQualification.Version;
        run["tracked_package_manifests"] = new JsonArray("package.json", "packages/member/package.json");
        run["repository_manifest"] = new JsonObject { ["package.json"] = new JsonObject {
            ["content"] = new JsonObject { ["scripts"] = new JsonObject { ["e2e"] = "playwright test", ["test"] = "pnpm recursive run test" } }.ToJsonString() } };
        run["events"]![1]!["server"] = "GnOuGo.GithubCopilot.Mcp";
        var calls = run["events"]![1]!["result"]!["toolExecutions"]!.AsArray();
        calls.Clear();
        foreach (var command in new[] { "pnpm install --frozen-lockfile", "uv sync --native-tls --group tests --group ci",
                     "uv tool run ruff==0.15.22 check .", "uv run --frozen pytest tests", "pnpm run test" })
            calls.Add(Call(command, command, 0, "Completed."));
        return run;
    }

    private static JsonObject Run() => new()
    {
        ["workspace_relative"] = "workflows/test",
        ["checkout_head"] = LiveWorkflowEvaluation.Head, ["checkout_base"] = LiveWorkflowEvaluation.Base,
        ["inputs"] = new JsonObject { ["pullRequestUrl"] = "https://github.com/AxaFrance/SmartGuide/pull/610" },
        ["events"] = new JsonArray(
            (JsonNode)new JsonObject { ["tool"] = "git_clone", ["error"] = false,
                ["arguments"] = new JsonObject { ["remoteUrl"] = "https://github.com/AxaFrance/SmartGuide", ["branch"] = LiveWorkflowEvaluation.Head,
                    ["targetDirectory"] = "workflows/test/repository" }, ["result"] = new JsonObject { ["success"] = true } },
            new JsonObject { ["tool"] = "code_agent_edit", ["result"] = new JsonObject { ["toolExecutions"] = new JsonArray(
                (JsonNode)Call("call-1", "pnpm --version", 0, "10.34.5"), Call("call-2", "pnpm test", 1, "A real failure")) } },
            new JsonObject { ["tool"] = "document_write", ["error"] = false,
                ["result"] = new JsonObject { ["success"] = true, ["filePath"] = ReportPath } })
    };
    private static JsonObject Call(string id, string command, int exit, string output) => new()
    {
        ["toolCallId"] = id, ["completionObserved"] = true, ["conflictingCompletion"] = false,
        ["argumentsJson"] = new JsonObject { ["command"] = command }.ToJsonString(),
        ["terminals"] = new JsonArray((JsonNode)new JsonObject { ["exitCode"] = exit, ["workingDirectory"] = Repository, ["text"] = output })
    };
    private static JsonObject Report() => new()
    {
        ["decision"] = "request_changes", ["findings"] = new JsonArray(),
        ["commands"] = new JsonArray((JsonNode)new JsonObject { ["command"] = "pnpm --version", ["exitCode"] = 0 },
            new JsonObject { ["command"] = "pnpm test", ["exitCode"] = 1 })
    };
    private static void AssertPass(JsonObject run, JsonObject report)
    {
        var result = LiveCodeEvidence.VerifyReport(run, report, Repository, ReportPath);
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
    }
}
