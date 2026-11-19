using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

// Independent read-back uses only GET requests. All publication goes through the generated workflow's MCP.
internal sealed record PublishedReviewQualification(string Owner, string Repository, int Number, string Head, string Base)
{
    internal const string Scenario = "code-publish";
    internal const string Version = "github-published-review-v1";
    private string Api => $"repos/{Owner}/{Repository}/pulls/{Number}";

    internal static async Task<PublishedReviewQualification> CaptureAsync(string url, CancellationToken ct)
    {
        if (url != LiveWorkflowEvaluation.ReviewUrl)
            throw new PlanningConflictException("The live publication grant covers only the requested pull request.");
        var parts = new Uri(url).AbsolutePath.Trim('/').Split('/');
        var number = int.Parse(parts[3], CultureInfo.InvariantCulture);
        var pr = await ReadAsync($"repos/{parts[0]}/{parts[1]}/pulls/{number}", ct);
        if (pr["state"]?.ToString() != "open" || pr["head"]?["sha"]?.ToString() is not { Length: 40 } head ||
            pr["base"]?["sha"]?.ToString() is not { Length: 40 } basis || !head.All(char.IsAsciiHexDigit) || !basis.All(char.IsAsciiHexDigit))
            throw new PlanningConflictException("An open pull request and its exact commit identities are required.");
        return new(parts[0], parts[1], number, head, basis);
    }

    internal bool Allows(string method, JsonObject args)
    {
        if (!string.Equals(args["owner"]?.ToString(), Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args["repo"]?.ToString(), Repository, StringComparison.OrdinalIgnoreCase) ||
            args["pullNumber"]?.ToString() != Number.ToString(CultureInfo.InvariantCulture)) return false;
        return method switch
        {
            "pull_request_read" => args["method"]?.ToString() is "get" or "get_diff" or "get_files" or "get_reviews" or "get_review_comments",
            "pull_request_review_write" when args["method"]?.ToString() == "create" =>
                args["commitID"]?.ToString() == Head && args["event"] is null,
            "pull_request_review_write" when args["method"]?.ToString() == "submit_pending" =>
                args["event"]?.ToString() is "APPROVE" or "REQUEST_CHANGES" && !string.IsNullOrWhiteSpace(args["body"]?.ToString()),
            "add_comment_to_pending_review" => args["subjectType"]?.ToString() == "LINE" &&
                args["side"]?.ToString() is "LEFT" or "RIGHT" && args["line"]?.GetValue<int>() > 0 &&
                !string.IsNullOrWhiteSpace(args["path"]?.ToString()) && !string.IsNullOrWhiteSpace(args["body"]?.ToString()),
            _ => false
        };
    }

    internal async Task CheckCurrentAsync(CancellationToken ct)
    {
        var pr = await ReadAsync(Api, ct);
        if (pr["state"]?.ToString() != "open" || pr["head"]?["sha"]?.ToString() != Head || pr["base"]?["sha"]?.ToString() != Base)
            throw new PlanningConflictException("The reviewed head or base changed before publication.");
    }

    internal async Task<JsonObject> ReadBackAsync(JsonArray events, CancellationToken ct)
    {
        var submissions = Writes(events, "pull_request_review_write", "submit_pending");
        if (submissions.Length != 1 || ReceiptId(submissions[0]) is not { } id)
            return Verdict(["unique_submitted_review_receipt_missing"]);
        var review = await ReadAsync($"{Api}/reviews/{id}", ct);
        var pages = await ReadAsync($"{Api}/reviews/{id}/comments?per_page=100", ct, paginate: true);
        var comments = new JsonArray(pages.AsArray().SelectMany(p => p!.AsArray()).Select(c => c!.DeepClone()).ToArray());
        var result = Verify(events, review, comments);
        result["review_url"] = review["html_url"]?.DeepClone();
        result["review_id"] = id;
        result["comment_urls"] = new JsonArray(comments.Select(c => c?["html_url"]?.DeepClone()).ToArray());
        return result;
    }

    internal JsonObject Verify(JsonArray events, JsonNode review, JsonArray comments)
    {
        var findings = new List<string>();
        var creates = Writes(events, "pull_request_review_write", "create");
        var submissions = Writes(events, "pull_request_review_write", "submit_pending");
        var inline = Writes(events, "add_comment_to_pending_review");
        var id = review["id"]?.ToString();
        if (id is null || creates.Length != 1 || submissions.Length != 1 || ReceiptId(creates[0]) != id || ReceiptId(submissions[0]) != id ||
            !Allows("pull_request_review_write", creates[0]["arguments"]!.AsObject()) ||
            !Allows("pull_request_review_write", submissions[0]["arguments"]!.AsObject()) ||
            review["commit_id"]?.ToString() != Head || review["submitted_at"] is null ||
            review["state"]?.ToString() != (submissions[0]["arguments"]!["event"]?.ToString() == "APPROVE" ? "APPROVED" : "CHANGES_REQUESTED") ||
            review["body"]?.ToString() != submissions[0]["arguments"]!["body"]?.ToString())
            findings.Add("github_verdict_or_commit_mismatch");
        if (inline.Length == 0 || comments.Count != inline.Length) findings.Add("published_inline_comments_missing");
        var remaining = comments.OfType<JsonObject>().ToList();
        foreach (var write in inline)
        {
            var args = write["arguments"]!.AsObject();
            var match = remaining.FindIndex(c => Allows("add_comment_to_pending_review", args) &&
                c["pull_request_review_id"]?.ToString() == id && c["commit_id"]?.ToString() == Head &&
                c["path"]?.ToString() == args["path"]?.ToString() && c["line"]?.ToString() == args["line"]?.ToString() &&
                c["side"]?.ToString() == args["side"]?.ToString() && c["body"]?.ToString() == args["body"]?.ToString() &&
                !string.IsNullOrWhiteSpace(c["diff_hunk"]?.ToString()));
            if (match < 0) findings.Add("inline_diff_location_or_body_mismatch");
            else remaining.RemoveAt(match);
        }
        return Verdict(findings);
    }

    internal static string? ReceiptId(JsonObject evt)
    {
        if (evt["result"] is not JsonObject data) return null;
        var id = (data["id"] ?? (data["review"] as JsonObject)?["id"])?.ToString();
        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0 ? id : null;
    }

    private static JsonObject[] Writes(JsonArray events, string tool, string? method = null) =>
        events.OfType<JsonObject>().Where(e => e["server"]?.ToString() == "Github" && e["tool"]?.ToString() == tool &&
            e["error"]?.GetValue<bool>() == false && (method is null || e["arguments"]?["method"]?.ToString() == method)).ToArray();

    private static JsonObject Verdict(IEnumerable<string> findings)
    {
        var errors = new JsonArray(findings.Distinct().Select(f => (JsonNode)JsonValue.Create(f)).ToArray());
        return new() { ["passed"] = errors.Count == 0, ["findings"] = errors };
    }

    private static async Task<JsonNode> ReadAsync(string path, CancellationToken ct, bool paginate = false)
    {
        var start = new ProcessStartInfo("gh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("api");
        if (paginate) { start.ArgumentList.Add("--paginate"); start.ArgumentList.Add("--slurp"); }
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("GitHub read-back could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var body = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(body, error, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new InvalidOperationException($"GitHub GET failed for {path} (exit {process.ExitCode}).");
            return JsonNode.Parse(await body) ?? throw new InvalidOperationException("GitHub GET returned an empty document.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
