using System.Text.Json.Nodes;

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
