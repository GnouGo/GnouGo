using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Configuration;

internal static class LocalReviewQualification
{
    internal const string Scenario = "code-local", Version = "local-sdk-review-v2";

    internal const string UnitCheck = "check_annotation_cleanup";
    internal const string UnitScript = ".bin/test-annotation-policy-cleanup.py";
    internal const string PolicyScript = ".bin/test-annotation-policy.py";
    internal const int MaxUnitPathLength = 240;

    internal static void ConfigureCommands(IDictionary<string, string?> environment)
    {
        const string subset = "GNOUGO_CMD_COMMAND_SUBSET";
        var aliases = new[] { DelegatedTestReviewAuthority.CleanupCommand, UnitCheck };
        foreach (var restriction in environment.Where(p => p.Key.Equals(subset, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Value).Append(Environment.GetEnvironmentVariable(subset)))
            if (restriction is not null && aliases.Except(restriction.Split(',', StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase).Any())
                throw new PlanningConflictException("The existing command restriction excludes a required local review operation.");
        var prefix = $"Cmd__AllowedCommands__{UnitCheck}__";
        if (environment.Keys.Concat(Environment.GetEnvironmentVariables().Keys.Cast<string>())
            .Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new PlanningConflictException("An existing command configuration conflicts with the fixed review check.");
        environment[subset] = string.Join(',', aliases);
        environment[prefix + "Description"] = "Run the four pinned annotation cleanup unit tests against the authorized checkout. The path is the exact unit-test file.";
        environment[prefix + "Shell"] = OperatingSystem.IsWindows() ? "powershell" : "sh";
        environment[prefix + "Script"] = OperatingSystem.IsWindows()
            ? "python.exe -I -B -S {{path}}; exit $LASTEXITCODE" : "exec python3 -I -B -S {{path}}";
        environment[prefix + "Parameters__path__Required"] = "true";
        environment[prefix + "Parameters__path__MaxLength"] = MaxUnitPathLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment[prefix + "Parameters__path__Pattern"] = @"^workflows/(?:[A-Za-z0-9_-][A-Za-z0-9_.-]*/)+repository/\.bin/test-annotation-policy-cleanup\.py$";
        environment[prefix + "Parameters__path__IsWorkspacePath"] = "true";
        environment[prefix + "Parameters__path__MustExist"] = "true";
        environment[prefix + "Parameters__path__PathKind"] = "File";
    }

    internal static bool MatchesPinnedPython(string path, string expected)
    {
        // Git may materialize text as CRLF on Windows. Hash the reviewed source with LF.
        if (!File.Exists(path) || new FileInfo(path).Length > 32768) return false;
        var source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))) == expected;
    }

    internal static async Task<string> PromptAsync(string relative, PublishedReviewQualification target, JsonObject report, CancellationToken ct)
    {
        if ((relative + "/repository/" + UnitScript).Length > MaxUnitPathLength)
            throw new InvalidOperationException("The fixed upstream test path exceeds the configured Cmd alias limit.");
        await target.CheckCurrentAsync(ct);
        var files = await target.ChangedFilesAsync(ct);
        await target.CheckCurrentAsync(ct);
        var readSubject = files.OfType<JsonObject>().FirstOrDefault(file => file["status"]?.ToString() != "removed" &&
            file["patch"] is JsonValue && !string.IsNullOrWhiteSpace(file["filename"]?.ToString()))?["filename"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The pinned change has no readable changed file for the local review.");
        report["review_read_subject"] = readSubject;
        return Prompt(relative, target, files.Count, readSubject);
    }

    internal static string Prompt(string relative, PublishedReviewQualification target, int changedFiles, string readSubject) =>
        $"Declare two required public string inputs named pullRequestUrl and reviewText and one required public object output named report. Bind report to the verified structured report after its successful file write. " +
        $"The approved target is {LiveWorkflowEvaluation.ReviewUrl}, base {target.Base}, head {target.Head}. Verify the URL. " +
        $"Clone https://github.com/{target.Owner}/{target.Repository}.git at that head into {relative}/repository and compare the two revisions with Git. " +
        $"This pinned PR has {changedFiles} changed paths. " +
        (changedFiles <= 100
            ? $"Use one git_compare_refs with pageSize={changedFiles} and expectedCompleteFileCount={changedFiles}. On success bind its files output directly to inputs.review.files. A mismatch fails the comparison. "
            : "Use the complete ordered page directory from git_compare_refs and include each page exactly once. Bind its complete ordered files to inputs.review.files. ") +
        $"Run the known {UnitCheck} command directly via cmd_run with parameters.path={relative}/repository/{UnitScript}. Require success and pass its result to the review. " +
        $"Use one bounded read-only code agent in the literal workspace {relative}/repository. Bind reviewText and the Git comparison as data inputs. " +
        $"Keep the verification list static: file.read with subject {readSubject} requires observed tool and arguments, and review.diff with subject {target.Head} " +
        "verifies the reported coverage and changed-line anchors against actual SDK file reads and Git patches. Explore other relevant files as needed. " +
        "Empty findings are permitted. " +
        "The public report object has required string base and head, string arrays reviewed_files (observed repository-relative paths) and limitations, " +
        "and a findings array of objects. Each finding requires strings path, side, severity, category, explanation and evidence, integer startLine and endLine, and numeric confidence from 0 to 1. " +
        $"Write that JSON report to {relative}/review.json with an explicit tool. Clean the fixed disposable destination in the always block even when cloning leaves a partial directory. Bind cleanup to that destination independently of clone success. All feedback stays local.";

    internal static bool PassedCheck(JsonObject e, string relative) =>
        e["server"]?.ToString() == "GnOuGo.Cmd.Mcp" && e["tool"]?.ToString() == "cmd_run" &&
        e["arguments"]?["commandName"]?.ToString() == UnitCheck &&
        e["arguments"]?["parameters"]?["path"]?.ToString() == relative + "/repository/" + UnitScript &&
        e["result"]?["commandName"]?.ToString() == UnitCheck && e["result"]?["exitCode"]?.GetValue<int>() == 0 &&
        e["result"]?["success"]?.GetValue<bool>() == true && e["result"]?["timedOut"]?.GetValue<bool>() == false &&
        e["result"]?["stderr"]?.ToString() is string transcript && transcript.Contains("Ran 4 tests", StringComparison.Ordinal) &&
        transcript.Split('\n', StringSplitOptions.TrimEntries).Contains("OK", StringComparer.Ordinal);

    internal static async Task<JsonObject> VerifyAsync(string root, JsonObject run, PublishedReviewQualification target, CancellationToken ct)
    {
        var failures = new List<string>();
        var folder = Path.Combine(root, run["workspace_relative"]!.ToString());
        if (run["execution"]?["success"]?.GetValue<bool>() != true) failures.Add("workflow_execution_failed");
        if (run["checkout_head"]?.ToString() != target.Head || run["checkout_base"]?.ToString() != target.Base)
            failures.Add("pinned_checkout_unverified");
        await target.CheckCurrentAsync(ct);
        var files = (await target.ChangedFilesAsync(ct)).OfType<JsonObject>().Select(file =>
            new ReviewFilePatch(file["filename"]!.GetValue<string>(), file["status"]!.GetValue<string>(), file["patch"]?.GetValue<string>() ?? "",
                Truncated: file["patch"] is null)).ToDictionary(file => file.Path, StringComparer.Ordinal);
        await target.CheckCurrentAsync(ct);
        var events = run["events"]!.AsArray().OfType<JsonObject>().Where(e => e["error"]?.GetValue<bool>() == false).ToArray();
        if (!events.Any(e => e["tool"]?.ToString() == "git_clone") ||
            !events.Any(e => e["tool"]?.ToString() is "git_diff" or "git_compare_refs")) failures.Add("missing_git_operations");
        if (!events.Any(e => PassedCheck(e, run["workspace_relative"]!.ToString()))) failures.Add("missing_successful_cleanup_unit_check");
        var receipts = (run["code_agent_receipts"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (receipts.Length == 0 || receipts.Any(r => r["status"]?.ToString() != "completed")) failures.Add("incomplete_sdk_execution");
        var observed = receipts.SelectMany(r => r["evidence"]!.AsArray()).Where(e => e?["kind"]?.ToString() == "file.read")
            .Select(e => e!["subject"]!.ToString()).Where(files.ContainsKey).ToHashSet(StringComparer.Ordinal);
        if (observed.Count == 0) failures.Add("no_observed_changed_file_reads");
        if (run["review_read_subject"] is JsonValue required && !observed.Contains(required.GetValue<string>()))
            failures.Add("missing_required_changed_file_read");
        var reportPath = Path.Combine(folder, "review.json");
        if (!events.Any(e => e["tool"]?.ToString() == "document_write") || !File.Exists(reportPath))
            failures.Add("missing_workflow_report");
        else
        {
            try
            {
                var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, ct))!.AsObject();
                if (!JsonNode.DeepEquals(report, run["execution"]?["outputs"]?["report"])) failures.Add("returned_report_differs_from_file");
                if (report["base"]?.ToString() != target.Base || report["head"]?.ToString() != target.Head ||
                    report["reviewed_files"] is not JsonArray coverage || !observed.SetEquals(coverage.Select(n => n!.GetValue<string>())) ||
                    report["limitations"] is not JsonArray limits || observed.Count < files.Count && limits.Count == 0)
                    failures.Add("incorrect_revisions_or_coverage");
                var valid = new List<ReviewFinding>();
                foreach (var item in report["findings"]!.AsArray())
                {
                    var value = item!.Deserialize<ReviewFindingCandidate>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (value is null || !observed.Contains(value.Path) || !ReviewValidation.TryValidate(value, files, out var finding, out _))
                        failures.Add("unverified_finding_anchor");
                    else valid.Add(finding!);
                }
                if (ReviewValidation.Deduplicate(valid).Count != valid.Count) failures.Add("duplicate_findings");
                run["local_review_report"] = report;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException)
            { failures.Add("invalid_report_structure"); }
        }
        if (Directory.Exists(Path.Combine(folder, "repository"))) failures.Add("checkout_cleanup_missing");
        return new() { ["passed"] = failures.Count == 0, ["changed_files"] = files.Count,
            ["observed_files"] = JsonSerializer.SerializeToNode(observed.Order(StringComparer.Ordinal)),
            ["findings"] = JsonSerializer.SerializeToNode(failures) };
    }
}

internal sealed partial class ServerBusinessExecution
{
    private sealed record ReviewExecution(string Endpoint, string Workspace, string Relative, JsonObject Report);
    private ReviewExecution? _reviewExecution;

    private void ConfigureReviewExecution(string endpoint, string workspace, string relative, JsonObject report) =>
        _reviewExecution = new(endpoint, workspace, relative, report);

    internal IAgentTaskRunner ConfigureReviewRunner(IConfiguration configuration, string provider, string model) =>
        new ReviewedCodeRunner(this, configuration, provider, model);

    private sealed class ReviewedCodeRunner(ServerBusinessExecution owner, IConfiguration configuration, string provider, string model) : IAgentTaskRunner
    {
        public bool SupportsActionLimit => true;
        public string Description => "Review the pinned local checkout with the managed Copilot SDK.";
        private CopilotAgentTaskRunner Adapter(ReviewExecution? execution)
        {
            var policy = new Dictionary<string, string?> { ["Code:AllowWrites"] = "false", ["Code:Copilot:Provider"] = provider,
                ["Code:Copilot:Model"] = model, ["Code:Copilot:EnableApproveAll"] = "false" };
            if (execution is not null)
            {
                policy["Code:Copilot:InferenceProxyEndpoint"] = execution.Endpoint;
                if (execution.Report["sdk_wire_api"]?.GetValue<string>() is { } wire)
                    policy["Code:Copilot:Providers:" + provider + ":WireApi"] = wire;
            }
            return new(configuration, new ConfigurationBuilder().AddInMemoryCollection(policy).Build());
        }

        public Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct) => Adapter(null).DescribeAsync(ct);
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) =>
            Adapter(owner._reviewExecution).ValidateAsync(context, ct);
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) =>
            Adapter(owner._reviewExecution).ReconcileAsync(context, ct);

        public async Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct)
        {
            var execution = owner._reviewExecution ?? throw new PlanningConflictException("Missing campaign inference relay.");
            var instructions = execution.Report["inputs"]!["reviewText"]!.GetValue<string>();
            if (context.TenantId != "benchmark" || context.AgentId != owner._savedId || context.RunId != owner._runId ||
                owner.Adaptive?.Approves(context) != true || context.Task.Workspace.Replace('\\', '/') != execution.Relative + "/repository" ||
                !context.Task.Objective.Contains(instructions, StringComparison.Ordinal) && !ContainsText(context.Task.Inputs, instructions))
                throw new PlanningConflictException("The SDK request does not match the approved review and its exact caller instructions.");
            await LiveCodeEvidence.ObserveCheckoutAsync(execution.Workspace, execution.Report);
            if (execution.Report["checkout_base"]?.ToString() != owner.Target!.Base || execution.Report["checkout_head"]?.ToString() != owner.Target.Head)
                throw new PlanningConflictException("The SDK requires a verified base and head before exploration.");
            if (execution.Report["events"] is not JsonArray events || !events.OfType<JsonObject>().Any(e => LocalReviewQualification.PassedCheck(e, execution.Relative)))
                throw new PlanningConflictException("The SDK review requires the observed pinned unit check before exploration.");
            var result = await Adapter(execution).RunAsync(context, ct);
            var receipts = execution.Report["code_agent_receipts"] as JsonArray ?? new JsonArray();
            if (receipts.Parent is null) execution.Report["code_agent_receipts"] = receipts;
            receipts.Add(JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult));
            return result;
        }

        private static bool ContainsText(JsonNode? input, string required) => input switch
        {
            JsonValue value => value.TryGetValue<string>(out var text) && text.Contains(required, StringComparison.Ordinal),
            JsonObject obj => obj.Any(field => ContainsText(field.Value, required)),
            JsonArray list => list.Any(value => ContainsText(value, required)),
            _ => false
        };
    }
}
