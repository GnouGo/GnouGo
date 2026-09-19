using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Evaluates declared checks against original observations. Publication is a separate host operation.</summary>
public static class ReviewEvaluation
{
    public static ReviewEvaluationResult Evaluate(ReviewEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Review is null || request.RequiredChecks is null || request.Checks is null ||
            string.IsNullOrWhiteSpace(request.WorkingDirectory) || request.Review.BlockingFindingCount < 0 ||
            request.RequiredChecks.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name)) ||
            request.Checks.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name) || !Enum.IsDefined(c.Status)) ||
            request.RequiredChecks.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.RequiredChecks.Count ||
            request.Checks.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Checks.Count ||
            request.Checks.Any(c => !request.RequiredChecks.Any(r => string.Equals(r.Name.Trim(), c.Name.Trim(), StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Declare distinct required checks and at most one result per declared check.");

        var limitations = new List<string>();
        if (!request.Review.Complete || request.Review.Coverage.SkippedFiles > 0 || request.Review.Coverage.TruncatedFiles > 0)
            limitations.Add("Review coverage is incomplete, skipped, truncated, or contains invalid findings.");
        if (request.RequiredChecks.Count == 0) limitations.Add("No requested verification checks were declared.");
        var reused = request.Checks.Where(c => c.Execution is not null).GroupBy(c => c.Execution!.ToolCallId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var checks = new List<ReviewCheckResult>();
        foreach (var required in request.RequiredChecks)
        {
            var supplied = request.Checks.SingleOrDefault(c => string.Equals(c.Name.Trim(), required.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            var result = supplied is null ? Blocked(required, "No result was supplied for this requested check.") : EvaluateCheck(required, supplied);
            checks.Add(result);
            if (result.Status == ReviewCheckStatus.Blocked) limitations.Add(required.Name + ": " + result.Evidence);
        }
        var blockers = Math.Max(request.Review.BlockingFindingCount, request.Review.Findings.Count(f => f.Severity >= ReviewSeverity.High));
        var verdict = blockers > 0 || checks.Any(c => c.Status == ReviewCheckStatus.Failed) ? ReviewSubmitEvent.RequestChanges
            : limitations.Count > 0 ? ReviewSubmitEvent.Comment : ReviewSubmitEvent.Approve;
        var body = new StringBuilder().AppendLine("## Automated review").AppendLine()
            .AppendLine("Reviewed commit: `" + request.Review.HeadSha + "`")
            .AppendLine("Outcome: **" + verdict + "**").AppendLine().AppendLine("### Requested checks").AppendLine();
        foreach (var check in checks)
        {
            body.AppendLine("- **" + check.Name + ": " + check.Status + "** — " + check.Evidence);
            if (check.Execution is { } execution)
            {
                body.AppendLine("  Invocation: `" + execution.ArgumentsJson?.Replace("`", "'", StringComparison.Ordinal) + "`");
                foreach (var terminal in execution.Terminals)
                    body.AppendLine("  Directory: `" + terminal.WorkingDirectory + "`; exit code: " + (terminal.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved") + ".");
            }
        }
        body.AppendLine().AppendLine("### Findings").AppendLine();
        if (request.Review.Findings.Count == 0) body.AppendLine("No new validated findings.");
        foreach (var finding in request.Review.Findings)
            body.AppendLine("- **" + finding.Severity + "** `" + finding.Path + ":" + finding.StartLine + "` — " + finding.Explanation + " Evidence: " + finding.Evidence);
        if (blockers > request.Review.Findings.Count(f => f.Severity >= ReviewSeverity.High))
            body.AppendLine("Previously reported blocking findings remain relevant; duplicate comments were suppressed.");
        if (limitations.Count > 0)
        {
            body.AppendLine().AppendLine("### Verification limitations").AppendLine();
            foreach (var limitation in limitations) body.AppendLine("- " + limitation);
        }
        return new(verdict, checks, limitations, body.ToString());

        ReviewCheckResult EvaluateCheck(ReviewCheckRequirement required, ReviewCheckResult supplied)
        {
            if (string.IsNullOrWhiteSpace(supplied.Evidence)) return Blocked(required, "No review or execution evidence was supplied.");
            if (supplied.Status == ReviewCheckStatus.NotApplicable)
                return required.AllowNotApplicable && !required.RequiresExecution ? supplied with { RequiresExecution = false }
                    : Blocked(required, "This required check cannot be marked not applicable.");
            if (!required.RequiresExecution) return supplied with { RequiresExecution = false };
            var execution = supplied.Execution;
            if (execution is null || string.IsNullOrWhiteSpace(execution.ToolCallId) || reused.Contains(execution.ToolCallId))
                return Blocked(required, "Each command check needs its own original execution observation; grouped or reused evidence is insufficient.");
            if (!SameArguments(required.ExpectedArgumentsJson, execution.ArgumentsJson))
                return Blocked(required, "The observed invocation does not match this check's declared command arguments.");
            if (!execution.CompletionObserved || execution.ConflictingCompletion || execution.Terminals is not { Count: > 0 } ||
                execution.Terminals.Any(t => t.ExitCode is null || !SameDirectory(request.WorkingDirectory, t.WorkingDirectory)))
                return Blocked(required, "Completion, exit codes, or the single working directory could not be established.");
            var status = execution.Terminals.Any(t => t.ExitCode != 0) ? ReviewCheckStatus.Failed
                : execution.ToolSucceeded == true ? ReviewCheckStatus.Passed : ReviewCheckStatus.Blocked;
            return supplied with { Name = required.Name, RequiresExecution = true, Status = status };
        }
    }

    private static ReviewCheckResult Blocked(ReviewCheckRequirement required, string reason) => new(required.Name, ReviewCheckStatus.Blocked, reason, required.RequiresExecution);
    private static bool SameArguments(string? expected, string? actual)
    {
        try { return expected is not null && actual is not null && JsonNode.Parse(expected) is JsonObject obj && JsonNode.DeepEquals(obj, JsonNode.Parse(actual)); }
        catch (System.Text.Json.JsonException) { return false; }
    }
    private static bool SameDirectory(string expected, string? actual)
    {
        try { return actual is not null && Path.IsPathFullyQualified(expected) && Path.IsPathFullyQualified(actual) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)); }
        catch (ArgumentException) { return false; }
    }
}
