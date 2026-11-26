using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Configuration;

internal static class LocalReviewQualification
{
    internal const string Scenario = "code-local", Version = "local-sdk-review-v1";

    internal static string Prompt(string relative, PublishedReviewQualification target) =>
        $"Create a reusable local review workflow with two required string inputs, pullRequestUrl and reviewText. " +
        $"The approved target is {LiveWorkflowEvaluation.ReviewUrl}, base {target.Base}, head {target.Head}. Verify the URL. " +
        $"Clone https://github.com/{target.Owner}/{target.Repository}.git at that head into {relative}/repository and compare the two revisions with Git. " +
        "Keep the Git comparison available to its consumer. Bind its ordered changed paths, patches and the original reviewText to the review task. " +
        "If a nested scope collects comparison pages, declare those pages as a scope output before it closes. " +
        "Use one bounded, read-only coding agent to explore changed source files, using the exact reviewText and verified diff. " +
        "Require file.read evidence from real project reads. Verify any findings on changed lines. Empty findings are permitted. " +
        "Return a structured local report with base, head, reviewed_files (observed repository-relative paths), limitations (array), " +
        "and findings (array of path, startLine, endLine, side, severity, category, confidence, explanation and evidence). " +
        $"Write that JSON report to {relative}/review.json with an explicit tool. Remove the disposable clone in cleanup. All feedback stays local.";

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
        var receipts = (run["code_agent_receipts"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (receipts.Length == 0 || receipts.Any(r => r["status"]?.ToString() != "completed")) failures.Add("incomplete_sdk_execution");
        var observed = receipts.SelectMany(r => r["evidence"]!.AsArray()).Where(e => e?["kind"]?.ToString() == "file.read")
            .Select(e => e!["subject"]!.ToString()).Where(files.ContainsKey).ToHashSet(StringComparer.Ordinal);
        if (observed.Count == 0) failures.Add("no_observed_changed_file_reads");
        var reportPath = Path.Combine(folder, "review.json");
        if (!events.Any(e => e["tool"]?.ToString() == "document_write") || !File.Exists(reportPath))
            failures.Add("missing_workflow_report");
        else
        {
            try
            {
                var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, ct))!.AsObject();
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
