using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

// Independent read-back uses only GET requests. All publication goes through the generated workflow's MCP.
internal sealed record PublishedReviewQualification(string Owner, string Repository, int Number, string Head, string Base)
{
    internal const string Scenario = "code-publish";
    internal const string Version = "github-published-review-v1";
    internal const string DeliveryVersion = "github-published-review-v2";
    internal const string LocalKindVersion = "github-published-kind-review-v1";
    internal const string HistoricalReviewUrl = "https://github.com/AxaFrance/SmartGuide/pull/718";
    internal static bool UsesPublicationChecks(JsonObject run) =>
        run["oracle_version"]?.ToString() is Version or DeliveryVersion or LocalKindVersion;

    internal static bool RequiresChatReport(JsonObject run) =>
        run["oracle_version"]?.ToString() is DeliveryVersion or LocalKindVersion;

    internal bool AllowsClone(JsonObject arguments) =>
        arguments["branch"]?.ToString() == Head && MatchesCloneRemote(arguments["remoteUrl"]?.ToString(), Owner, Repository);

    internal static bool MatchesCloneRemote(string? url, string owner, string repository) =>
        url == $"https://github.com/{owner}/{repository}" || url == $"https://github.com/{owner}/{repository}.git";

    internal static bool AuthorizesTarget(string url, string? version) => version switch
    {
        LocalKindVersion => url == LiveWorkflowEvaluation.ReviewUrl,
        Version or DeliveryVersion => url == HistoricalReviewUrl,
        _ => false
    };

    internal static string? ReturnedReportFinding(JsonObject run)
    {
        if (run["caller_response"] is not JsonObject answer ||
            run["execution_run_id"]?.ToString() is not { Length: > 0 } id || answer["run"]?.ToString() != id ||
            run["run"]?.ToString() is not { Length: > 0 } agent || answer["agent"]?.ToString() != agent ||
            answer["text"]?.GetValue<string>() is not { } text || string.IsNullOrWhiteSpace(text))
            return "caller_review_report_missing";
        // A public JSON result and a Markdown answer both reach the caller as rendered text.
        // Decode JSON escaping before looking for the observed commands in their original spelling.
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject outputs) text = ReportText(outputs);
            }
            catch (System.Text.Json.JsonException) { /* A free-form report can start with a brace. */ }
        }
        if (run["observed_commands"] is not JsonArray { Count: > 0 } commands) return "caller_review_report_command_missing";
        var pending = commands.Select(c => (Command: c?["command"]?.ToString(), Code: c?["exit_code"]?.ToString())).ToHashSet();
        if (pending.Any(p => string.IsNullOrWhiteSpace(p.Command) || p.Code is null)) return "caller_review_report_command_missing";
        var names = pending.Select(p => p.Command!).Distinct(StringComparer.Ordinal).OrderByDescending(name => name.Length);
        var mentions = Regex.Matches(text, string.Join('|', names.Select(Regex.Escape)), RegexOptions.CultureInvariant);
        for (var i = 0; i < mentions.Count; i++)
        {
            var mention = mentions[i];
            var end = i + 1 < mentions.Count ? mentions[i + 1].Index : text.Length;
            var details = text[(mention.Index + mention.Length)..end];
            var code = Regex.Match(details,
                @"(?:\A\s*[|,:=–—()]*\s*|\b(?:exit(?:ed|[_ -]?code|[_ -]?status)?|code(?: de (?:sortie|retour))?)\s*[:=]?\s*)(-?[0-9]+)(?!\w|\.[0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (code.Success) pending.Remove((mention.Value, code.Groups[1].Value));
        }
        return pending.Count == 0 ? null : "caller_review_report_command_or_exit_missing";
    }

    private static string ReportText(JsonNode? node) => node switch
    {
        JsonObject record when record["command"] is JsonValue command && (record["exitCode"] ?? record["exit_code"]) is JsonValue code =>
            command.ToString() + " " + code,
        JsonObject obj => string.Join('\n', obj.Select(p => ReportText(p.Value))),
        JsonArray array => string.Join('\n', array.Select(ReportText)),
        _ => node?.ToString() ?? ""
    };
    private string Api => $"repos/{Owner}/{Repository}/pulls/{Number}";

    // These grants belong to the qualification host. The provider's metadata also serves other hosts.
    internal static McpToolInfo DescribeGuardedTool(string server, McpToolInfo tool)
    {
        var guard = (server, tool.Name) switch
        {
            ("GnOuGo.Git.Mcp", "git_clone") => "The host checks remoteUrl, branch and targetDirectory before dispatch. " +
                "They must identify the authorized repository, the exact captured pull request head and the confined checkout directory.",
            ("Github", "pull_request_review_write") => "Permitted write methods are create and submit_pending. The host checks for an existing pending review before create " +
                "and refuses creation if it finds one. It verifies the new review's authenticated owner and head, and permits comments and submission " +
                "only to the review created by this execution. GitHub acknowledges a completed submit_pending mutation with the message " +
                "'pending pull request review successfully submitted'. This acknowledgement is available to the workflow with its submitted arguments. " +
                "The host records the new review ID privately and, after workflow execution, reads back the submitted review and inline comments by that ID. " +
                "If submission stops, leave the pending review intact for reconciliation. Retention is the intended failure outcome. " +
                "delete_pending is outside this grant. The host checks recorded mandatory checks, asks for confirmation of the concrete verdict " +
                "and inline comments, then rechecks the pull request head and base before transmission. " +
                "Refusal or a changed target fails the operation before transmission with DELEGATED_PERMISSION_DENIED. " +
                "This failure has no normal result or submission acknowledgement. The workflow propagates the error and runs its cleanup. " +
                "Calling submit_pending invokes this guard directly.",
            _ => null
        };
        return guard is null ? tool : new()
        {
            Name = tool.Name, EffectKind = tool.EffectKind, InputSchema = tool.InputSchema, OutputSchema = tool.OutputSchema,
            ArtifactContract = tool.ArtifactContract, CompositionContract = tool.CompositionContract, OutputContract = tool.OutputContract,
            Meta = tool.Meta, ExampleResponse = tool.ExampleResponse, Description = guard + "\n" + tool.Description
        };
    }

    internal static async Task<PublishedReviewQualification> CaptureAsync(string url, string? version, CancellationToken ct)
    {
        if (!AuthorizesTarget(url, version))
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
            args["pullNumber"]?.ToString() != Number.ToString(CultureInfo.InvariantCulture) || args["threadId"] is not null) return false;
        return method switch
        {
            "pull_request_read" => args["method"]?.ToString() is "get" or "get_diff" or "get_files" or "get_reviews" or "get_review_comments",
            "pull_request_review_write" when args["method"]?.ToString() == "create" =>
                args["commitID"]?.ToString() == Head && args["event"] is null,
            "pull_request_review_write" when args["method"]?.ToString() == "submit_pending" =>
                (args["commitID"] is null || args["commitID"]?.ToString() == Head) &&
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

    internal async Task CheckWriteAsync(string tool, JsonObject args, JsonArray events, CancellationToken ct)
    {
        if (!Allows(tool, args)) throw new PlanningConflictException("The GitHub operation is outside the reviewed target.");
        if (tool == "pull_request_read") return;
        await CheckCurrentAsync(ct);
        var pending = await PendingAsync(ct);
        var creates = Writes(events, "pull_request_review_write", "create");
        if (tool == "pull_request_review_write" && args["method"]?.ToString() == "create")
        {
            if (pending.Length != 0 || Attempts(events, "pull_request_review_write", "create").Any())
                throw new PlanningConflictException("A pending or previously created review already exists.");
        }
        else if (pending.Length != 1 || creates.Length != 1 || CreatedId(creates[0]) is not { } id ||
            pending[0]["id"]?.ToString() != id || pending[0]["commit_id"]?.ToString() != Head ||
            args["method"]?.ToString() == "submit_pending" && Writes(events, "add_comment_to_pending_review").Length == 0 ||
            Attempts(events, "pull_request_review_write", "submit_pending").Any())
            throw new PlanningConflictException("A write requires the pending review created by this execution.");
    }

    private async Task<JsonObject[]> PendingAsync(CancellationToken ct) =>
        (await ReadAsync(Api + "/reviews?per_page=100", ct, paginate: true)).AsArray()
            .SelectMany(page => page!.AsArray()).OfType<JsonObject>().Where(r => r["state"]?.ToString() == "PENDING").ToArray();

    internal async Task ObserveCreatedReviewAsync(JsonObject evt, CancellationToken ct)
    {
        if (evt["server"]?.ToString() != "Github" || evt["tool"]?.ToString() != "pull_request_review_write" ||
            evt["arguments"]?["method"]?.ToString() != "create" || evt["error"]?.GetValue<bool>() != false) return;
        var actor = await ReadAsync("user", ct);
        var pending = await PendingAsync(ct);
        if (pending.Length != 1 || actor["id"] is null || pending[0]["user"]?["id"]?.ToString() != actor["id"]!.ToString() ||
            pending[0]["commit_id"]?.ToString() != Head || NumericId(pending[0]["id"]) is not { } id ||
            ReceiptId(evt) is { } reported && reported != id)
            throw new PlanningConflictException("The newly created pending review could not be attributed to this execution.");
        evt["observed_pending_review_id"] = id;
    }

    private static string? CreatedId(JsonObject evt)
    {
        var observed = NumericId(evt["observed_pending_review_id"]);
        var reported = ReceiptId(evt);
        return observed is not null && reported is not null && observed != reported ? null : observed ?? reported;
    }
    private static bool MatchesSubmission(JsonObject evt, string id) =>
        ReceiptId(evt) is { } reported ? reported == id :
            evt["result"] is JsonObject result && result["message"] is JsonValue message && message.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);

    internal async Task<JsonObject> ReadBackAsync(JsonArray events, CancellationToken ct)
    {
        var submissions = Writes(events, "pull_request_review_write", "submit_pending");
        var creates = Writes(events, "pull_request_review_write", "create");
        if (creates.Length != 1 || CreatedId(creates[0]) is not { } id || submissions.Length != 1 || !MatchesSubmission(submissions[0], id))
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
        if (id is null || creates.Length != 1 || submissions.Length != 1 || CreatedId(creates[0]) != id || !MatchesSubmission(submissions[0], id) ||
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
        return NumericId(data["id"] ?? (data["review"] as JsonObject)?["id"]);
    }

    private static string? NumericId(JsonNode? value)
    {
        var id = value?.ToString();
        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0 ? id : null;
    }

    private static JsonObject[] Writes(JsonArray events, string tool, string? method = null) =>
        Attempts(events, tool, method).Where(e => e["error"]?.GetValue<bool>() == false).ToArray();

    private static IEnumerable<JsonObject> Attempts(JsonArray events, string tool, string? method) =>
        events.OfType<JsonObject>().Where(e => e["server"]?.ToString() == "Github" && e["tool"]?.ToString() == tool &&
            e["dispatched"]?.GetValue<bool>() != false && (method is null || e["arguments"]?["method"]?.ToString() == method));

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
