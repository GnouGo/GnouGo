using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Planning.Benchmark;

// Synthetic, immutable transport observations for the two authorized input URLs.
// These are not claims about the current contents or check results of those PRs.
// Names and business assertions belong exclusively to this benchmark boundary.
internal sealed class CodeReviewExecutionFixture(int pullNumber, string variant) : IHumanInputProvider
{
    internal static string Fingerprint { get; } = SourceFingerprint();
    private static string SourceFingerprint()
    {
        using var stream = typeof(CodeReviewExecutionFixture).Assembly.GetManifestResourceStream("GnOuGo.Agent.Planning.Benchmark.CodeReviewExecutionFixture.cs")
            ?? throw new InvalidOperationException("Frozen fixture source is missing.");
        using var reader = new StreamReader(stream);
        return GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(reader.ReadToEnd());
    }
    internal const string Instructions = "Verify the boundary value is rejected; verify cleanup preserves the original failure.";
    private const string Base = "1111111111111111111111111111111111111111";
    private const string Head = "2222222222222222222222222222222222222222";
    private const string Manifest = """{"name":"frozen-review-fixture","packageManager":"pnpm@10.0.0","scripts":{"lint":"eslint .","test":"vitest run","test:integration":"vitest run --config integration.config.ts"}}""";
    private readonly object sync = new();
    private readonly HashSet<string> checks = new(StringComparer.Ordinal);
    private string? ownedPath;
    private string? filesJson;
    private string? checkedOut;
    private readonly Dictionary<string, string> remoteRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> localRefs = new(StringComparer.Ordinal);
    private readonly HashSet<string> commits = new(StringComparer.Ordinal);
    private readonly List<string> defaultFetchSources = [];
    private int clones, cleanups, publications, confirmations, inspections, reviews;
    private bool permission;
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidOperationException("FROZEN_FIXTURE: " + message); }
    private static string Text(JsonNode? input, string field) => input?[field]?.ToString() ?? "";
    private bool PullMatches(JsonNode? input) => double.TryParse(Text(input, "pullNumber"), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == pullNumber;
    internal string Url => $"https://github.com/AxaFrance/SmartGuide/pull/{pullNumber}";

    internal static async Task VerifyAsync(JsonArray discovery, CancellationToken ct)
    {
        foreach (var number in new[] { 449, 510 })
        foreach (var scenario in new[] { "passing", "mixed", "empty", "boundary", "rejected", "omitted_defaults", "invalid_input", "setup_failure", "execution_failure" })
        {
            var fixture = new CodeReviewExecutionFixture(number, scenario);
            if (scenario == "invalid_input") { fixture.AssertComplete(false, GnOuGo.Flow.Core.Models.ErrorCodes.InputValidation, null); continue; }
            JsonNode Call(string method, JsonObject input)
            {
                var tools = discovery.OfType<JsonObject>().SelectMany(s => s["tools"]!.AsArray().OfType<JsonObject>()).Where(t => t["name"]?.ToString() == method).ToArray();
                Require(tools.Length == 1, "Self-test needs one frozen tool contract.");
                var tool = tools[0];
                Require(GnOuGo.Flow.Core.Planning.PlanningContractValidation.ValidateInstance(input, tool["inputSchema"]!).Count == 0, "Self-test invocation must satisfy the frozen input contract.");
                var response = fixture.Invoke("fixture", method, input).Content!;
                if (tool["outputSchema"] is { } schema)
                    Require(GnOuGo.Flow.Core.Planning.PlanningContractValidation.ValidateInstance(response, schema).Count == 0, "Fixture response must satisfy its producer contract.");
                return response;
            }
            JsonObject Root() => new() { ["projectRoot"] = "workflows/frozen-case" };
            void Cleanup() => Call("cmd_run", new() { ["commandName"] = "delete_directory", ["parametersJson"] = "{\"path\":\"workflows/frozen-case\"}" });
            Call("pull_request_read", new() { ["method"] = "get", ["owner"] = "AxaFrance", ["repo"] = "SmartGuide", ["pullNumber"] = number });
            try { Call("git_clone", new() { ["remoteUrl"] = number == 449 ? "https://github.com/fixture-fork/SmartGuide.git" : "https://github.com/AxaFrance/SmartGuide.git",
                ["branch"] = number == 449 ? Head : "main", ["historyDepth"] = 0, ["targetDirectory"] = "workflows/frozen-case" }); }
            catch (InvalidOperationException error) when (scenario == "setup_failure" && error.Message == "FROZEN_SETUP_FAILURE")
            { fixture.AssertComplete(false, error.Message, null); continue; }
            var fetch = Root();
            if (number == 510)
            {
                try { Call("code_project_summary", Root()); throw new InvalidOperationException("An unreviewed checkout was accepted."); }
                catch (InvalidOperationException error) when (error.Message.StartsWith("FROZEN_FIXTURE:", StringComparison.Ordinal)) { }
                fetch["refSpec"] = "refs/pull/510/head:refs/remotes/origin/frozen-pr";
            }
            Call("git_fetch", fetch);
            var checkout = Root(); checkout["branchOrCommit"] = Head; Call("git_checkout", checkout);
            var comparison = Root(); comparison["baseRef"] = Base; comparison["headRef"] = Head;
            var observed = Call("git_compare_refs", comparison);
            Call("code_project_summary", Root());
            var file = Root(); file["relativePath"] = "package.json"; Call("code_read_file", file);
            var search = Root(); search["query"] = "scripts"; search["glob"] = "*.json";
            Require(Call("code_search_text", search)["results"]!.AsArray().Count == 1, "Frozen search must return the actual manifest line.");
            search["query"] = "not-in-this-manifest";
            Require(Call("code_search_text", search)["results"]!.AsArray().Count == 0, "Search must not fabricate manifest matches.");
            try
            {
                foreach (var command in new[] { "pnpm install", "pnpm run lint", "pnpm test", "pnpm run test:integration" })
                { var input = Root(); input["prompt"] = "Execute " + command; Call("copilot_interactive_one_shot", input); }
            }
            catch (InvalidOperationException error) when (scenario == "execution_failure" && error.Message == "FROZEN_EXECUTION_FAILURE")
            { Cleanup(); fixture.AssertComplete(false, error.Message, null); continue; }
            var review = Root(); review["baseSha"] = Base; review["headSha"] = Head; review["filesJson"] = observed["filesJson"]!.DeepClone();
            review["reviewInstructions"] = Instructions; review["runtimeContextJson"] = "{\"checks\":\"observed\"}"; Call("copilot_review", review);
            await fixture.RequestInputAsync(new(), ct);
            var decision = scenario is "mixed" or "boundary" ? "REQUEST_CHANGES" : "APPROVE";
            if (scenario != "rejected") Call("pull_request_review_write", new() { ["method"] = "create", ["owner"] = "AxaFrance", ["repo"] = "SmartGuide", ["pullNumber"] = number, ["event"] = decision, ["commitID"] = Head, ["body"] = Instructions });
            Cleanup(); fixture.AssertComplete(true, null, new JsonObject { ["decision"] = decision });
        }
        var rejected = new CodeReviewExecutionFixture(449, "passing");
        try { rejected.Invoke("fixture", "pull_request_review_write", new JsonObject()); throw new InvalidOperationException("Unconfirmed publication was accepted."); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("FROZEN_FIXTURE:", StringComparison.Ordinal)) { }
        try { rejected.Invoke("fixture", "unconfigured_tool", new JsonObject()); throw new InvalidOperationException("Unconfigured transport was accepted."); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("FROZEN_FIXTURE:", StringComparison.Ordinal)) { }
        Console.WriteLine("Verified 18 frozen fixture cases and denied unconfirmed/unconfigured invocations; no model or business transport calls.");
    }

    internal McpCallResult Invoke(string server, string tool, JsonNode? input)
    {
        lock (sync)
        {
            JsonNode response;
            switch (tool)
            {
                case "pull_request_read":
                    Require(Text(input, "method") == "get" && Text(input, "owner") == "AxaFrance" && Text(input, "repo") == "SmartGuide" && PullMatches(input), "PR lookup must use this execution's parsed inputs.");
                    response = new JsonObject
                    {
                        ["number"] = pullNumber, ["html_url"] = Url, ["state"] = "open", ["title"] = "Frozen boundary regression",
                        ["head"] = new JsonObject { ["sha"] = Head, ["ref"] = "fixture-head", ["repo"] = new JsonObject { ["clone_url"] = "https://github.com/fixture-fork/SmartGuide.git", ["full_name"] = "fixture-fork/SmartGuide", ["fork"] = true } },
                        ["base"] = new JsonObject { ["sha"] = Base, ["ref"] = "main", ["repo"] = new JsonObject { ["clone_url"] = "https://github.com/AxaFrance/SmartGuide.git", ["full_name"] = "AxaFrance/SmartGuide" } }
                    };
                    break;
                case "git_clone":
                    Require(++clones == 1, "Exactly one clone is permitted per execution.");
                    var remoteUrl = Text(input, "remoteUrl");
                    Require(remoteUrl is "https://github.com/fixture-fork/SmartGuide.git" or "https://github.com/AxaFrance/SmartGuide.git", "Clone must use the declared head or base repository.");
                    remoteRefs["refs/heads/main"] = Base;
                    remoteRefs[remoteUrl.Contains("fixture-fork", StringComparison.Ordinal) ? "refs/heads/fixture-head" : $"refs/pull/{pullNumber}/head"] = Head;
                    var branch = Text(input, "branch");
                    if (branch.Length == 0) branch = "main";
                    var source = branch.StartsWith("refs/", StringComparison.Ordinal) || branch is Base or Head ? branch : "refs/heads/" + branch;
                    Require(source is Base or Head || remoteRefs.ContainsKey(source), "The requested clone reference must exist in the frozen remote.");
                    var depth = input?["historyDepth"]?.GetValue<int>() ?? 1;
                    // The frozen history is Base -> Head. Full history or depth two
                    // proves both revisions; a shallow head alone cannot compare them.
                    Require(depth >= 0, "History depth cannot be negative.");
                    checkedOut = source is Base or Head ? source : remoteRefs[source];
                    commits.Add(checkedOut);
                    if (checkedOut == Head && (depth == 0 || depth >= 2)) commits.Add(Base);
                    localRefs[branch] = checkedOut;
                    var allBranches = input?["fetchAllBranches"]?.GetValue<bool>() == true;
                    defaultFetchSources.AddRange(allBranches || source is Base or Head || source.StartsWith("refs/", StringComparison.Ordinal) && !source.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? remoteRefs.Keys.Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal)) : [source]);
                    if (allBranches) foreach (var reference in defaultFetchSources) { commits.Add(remoteRefs[reference]); localRefs["refs/remotes/origin/" + reference[11..]] = remoteRefs[reference]; }
                    var path = Text(input, "targetDirectory");
                    Require(path.StartsWith("workflows/", StringComparison.Ordinal) && !path.Split('/').Contains(".."), "Clone must own an isolated workspace child.");
                    if (variant == "setup_failure") throw new InvalidOperationException("FROZEN_SETUP_FAILURE");
                    ownedPath = path;
                    response = new JsonObject { ["repositoryRoot"] = "/fixture/" + path, ["remoteUrl"] = Text(input, "remoteUrl"), ["branch"] = Text(input, "branch"), ["projectRootRelative"] = path, ["success"] = true };
                    break;
                case "git_fetch":
                    Owned(input); Require(Text(input, "remoteName") is "" or "origin", "Fetch must use the existing owned remote.");
                    var refSpec = Text(input, "refSpec");
                    if (refSpec.Length == 0)
                        foreach (var reference in defaultFetchSources) { commits.Add(remoteRefs[reference]); localRefs["refs/remotes/origin/" + reference[11..]] = remoteRefs[reference]; }
                    else
                    {
                        var parts = refSpec.TrimStart('+').Split(':');
                        Require(parts.Length == 2 && remoteRefs.ContainsKey(parts[0]) && (parts[1].StartsWith("refs/remotes/", StringComparison.Ordinal) || parts[1].StartsWith("refs/heads/", StringComparison.Ordinal) || parts[1].StartsWith("refs/tags/", StringComparison.Ordinal)), "Fetch requires a declared source and controlled destination ref.");
                        commits.Add(remoteRefs[parts[0]]); localRefs[parts[1]] = remoteRefs[parts[0]];
                    }
                    response = GitResult("fetch");
                    break;
                case "git_checkout":
                    Owned(input);
                    var revision = Text(input, "branchOrCommit");
                    var resolved = commits.Contains(revision) ? revision : localRefs.GetValueOrDefault(revision)
                        ?? localRefs.GetValueOrDefault("refs/heads/" + revision) ?? localRefs.GetValueOrDefault("refs/remotes/" + revision);
                    Require(resolved is not null && commits.Contains(resolved), "Checkout requires a materialized revision.");
                    checkedOut = resolved;
                    if (input?["createBranch"]?.GetValue<bool>() == true)
                    { var name = Text(input, "newBranchName"); localRefs[name.Length == 0 ? revision : name] = resolved; }
                    response = GitResult("checkout");
                    break;
                case "git_compare_refs":
                    Owned(input); Require(Text(input, "baseRef") == Base && Text(input, "headRef") == Head, "Comparison must use the exact PR revisions.");
                    Require(commits.Contains(Base) && commits.Contains(Head), "Both compared revisions must have been materialized in the one clone.");
                    var files = new JsonArray();
                    if (variant != "empty") files.Add(new JsonObject { ["path"] = "src/boundary.ts", ["previousPath"] = null, ["status"] = "modified", ["patch"] = "@@ -1 +1 @@\n-export const accepts = n => n >= 0;\n+export const accepts = n => n > 0;", ["isBinary"] = false, ["isSubmodule"] = false, ["truncated"] = false, ["linesAdded"] = 1, ["linesDeleted"] = 1, ["oldObjectId"] = Base, ["newObjectId"] = Head });
                    filesJson = files.ToJsonString();
                    response = new JsonObject { ["repositoryRoot"] = "/fixture/" + ownedPath, ["baseRef"] = Base, ["headRef"] = Head, ["baseSha"] = Base, ["headSha"] = Head, ["mergeBaseSha"] = Base, ["comparedFromSha"] = Base, ["files"] = files, ["filesJson"] = filesJson, ["totalFiles"] = files.Count, ["offset"] = 0, ["pageSize"] = 50, ["hasMore"] = false, ["nextCursor"] = null, ["totalPatchCharacters"] = filesJson.Length, ["truncatedFileCount"] = 0, ["success"] = true };
                    break;
                case "code_project_summary":
                    Owned(input); Require(checkedOut == Head, "Inspect and execute against the reviewed head."); inspections++;
                    response = new JsonObject { ["rootPath"] = "/fixture/" + ownedPath, ["projectRootRelative"] = ownedPath, ["solutionFiles"] = new JsonArray(), ["projectFiles"] = new JsonArray("package.json"), ["topLevelDirectories"] = new JsonArray("src", "tests"), ["codeFileCount"] = 2, ["approximateBytes"] = 400, ["success"] = true };
                    break;
                case "code_read_file":
                    Owned(input); Require(checkedOut == Head && inspections > 0 && Text(input, "relativePath") == "package.json", "Read the discovered manifest before selecting commands."); inspections++;
                    response = new JsonObject { ["path"] = "package.json", ["fullPath"] = "/fixture/" + ownedPath + "/package.json", ["relativePath"] = "package.json", ["content"] = Manifest, ["lengthBytes"] = Manifest.Length, ["success"] = true };
                    break;
                case "code_search_text":
                    Owned(input); Require(checkedOut == Head && inspections > 0, "Search the inspected review checkout.");
                    var query = Text(input, "query"); Require(query.Length > 0, "Search requires literal text.");
                    var comparison = input?["caseSensitive"]?.GetValue<bool>() == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    var glob = Text(input, "glob");
                    var matches = Manifest.Contains(query, comparison) && (glob.Length == 0 || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, "package.json"));
                    response = new JsonObject { ["results"] = matches ? new JsonArray(new JsonObject { ["path"] = "package.json", ["line"] = 1, ["text"] = Manifest }) : new JsonArray(), ["truncated"] = false, ["success"] = true, ["ok"] = true };
                    break;
                case "copilot_interactive_one_shot":
                    Owned(input); Require(checkedOut == Head && inspections >= 2, "Execution requires previously observed manifests at the reviewed head.");
                    if (variant == "execution_failure") throw new InvalidOperationException("FROZEN_EXECUTION_FAILURE");
                    var prompt = Text(input, "prompt");
                    var commands = new[] { "pnpm install", "pnpm run lint", "pnpm test", "pnpm run test:integration" }.Where(c => prompt.Contains(c, StringComparison.Ordinal)).ToArray();
                    Require(commands.Length == 1, "An execution request must identify exactly one declared command; ambiguous prompts cannot certify work.");
                    var command = commands[0];
                    if (command != "pnpm install") Require(checks.Contains("pnpm install"), "Dependencies must be installed before checks.");
                    Require(checks.Add(command), "A command must not be rerun to fabricate additional evidence.");
                    int? exit = variant == "boundary" && command == "pnpm test" ? null : variant == "mixed" && command == "pnpm test" ? 1 : 0;
                    response = new JsonObject
                    {
                        ["handle"] = "fixture", ["copilotSessionId"] = "fixture", ["content"] = "Use execution observations; this text does not establish success.", ["model"] = null, ["completed"] = true, ["progressEvents"] = new JsonArray(),
                        ["toolExecutions"] = new JsonArray(new JsonObject { ["toolCallId"] = command, ["parentToolCallId"] = null, ["toolName"] = "terminal", ["argumentsJson"] = new JsonObject { ["command"] = command, ["workingDirectory"] = ownedPath }.ToJsonString(), ["completionObserved"] = true, ["toolSucceeded"] = true, ["conflictingCompletion"] = false, ["errorCode"] = null, ["terminals"] = new JsonArray(new JsonObject { ["workingDirectory"] = ownedPath, ["exitCode"] = exit, ["text"] = "Boundary and cleanup regression evidence." }) })
                    };
                    break;
                case "copilot_review":
                    Owned(input); Require(filesJson is not null && Text(input, "filesJson") == filesJson, "Review must consume the original comparison payload.");
                    Require(Text(input, "baseSha") == Base && Text(input, "headSha") == Head, "Review revisions must match comparison.");
                    Require(Text(input, "reviewInstructions") == Instructions && !string.IsNullOrWhiteSpace(Text(input, "runtimeContextJson")), "Review must receive the exact instructions and check evidence.");
                    Require(checks.Count == 4, "All available check categories must be observed before review."); reviews++;
                    response = new JsonObject { ["baseSha"] = Base, ["headSha"] = Head, ["findings"] = new JsonArray(), ["coverage"] = new JsonObject { ["totalFiles"] = variant == "empty" ? 0 : 1, ["reviewedFiles"] = variant == "empty" ? 0 : 1, ["skippedFiles"] = 0, ["truncatedFiles"] = 0, ["skippedPaths"] = new JsonArray(), ["truncatedPaths"] = new JsonArray() }, ["rejectedFindings"] = new JsonArray(), ["summary"] = Instructions + " Observations recorded for both requirements." };
                    break;
                case "pull_request_review_write":
                    Require(permission && confirmations == 1 && reviews == 1, "Publication requires confirmed review evidence.");
                    Require(++publications == 1 && Text(input, "method") == "create" && Text(input, "commitID") == Head, "Publish once against the reviewed commit.");
                    Require(Text(input, "owner") == "AxaFrance" && Text(input, "repo") == "SmartGuide" && PullMatches(input), "Publish only to this input PR.");
                    Require(Text(input, "event") == (variant is "mixed" or "boundary" ? "REQUEST_CHANGES" : "APPROVE"), "Decision must follow observed required checks.");
                    Require(Text(input, "body").Contains("boundary", StringComparison.OrdinalIgnoreCase) && Text(input, "body").Contains("cleanup", StringComparison.OrdinalIgnoreCase), "Published explanation must cover both instructions.");
                    response = new JsonObject { ["id"] = 1, ["state"] = Text(input, "event"), ["commit_id"] = Head, ["body"] = Text(input, "body") };
                    break;
                case "cmd_run":
                    Require(Text(input, "commandName") == "delete_directory", "Only the owned cleanup command is permitted.");
                    Require(ownedPath is not null && JsonNode.Parse(Text(input, "parametersJson"))?["path"]?.ToString() == ownedPath, "Cleanup must target the exact materialized path.");
                    Require(++cleanups == 1, "Cleanup must execute once.");
                    response = new JsonObject { ["commandName"] = "delete_directory", ["shell"] = null, ["workingDirectory"] = null, ["exitCode"] = 0, ["success"] = true, ["timedOut"] = false, ["stdout"] = "deleted", ["stderr"] = null, ["outputTruncated"] = false, ["startedAtUtc"] = null, ["finishedAtUtc"] = null, ["durationMs"] = 1 };
                    break;
                default: throw new InvalidOperationException("FROZEN_FIXTURE: Unconfigured business invocation; no external transport is available.");
            }
            return new McpCallResult { Content = response };
        }
    }

    private void Owned(JsonNode? input) => Require(ownedPath is not null && Text(input, "projectRoot") == ownedPath, "Use the original owned clone artifact.");
    private JsonObject GitResult(string operation) => new() { ["repositoryRoot"] = "/fixture/" + ownedPath, ["projectRootRelative"] = ownedPath,
        ["repositoryRootRelative"] = ownedPath, ["operation"] = operation, ["message"] = "Frozen Git operation completed.", ["success"] = true, ["ok"] = true };
    public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
    {
        lock (sync) { Require(reviews == 1 && ++confirmations == 1, "Confirmation must follow the complete review."); permission = variant != "rejected"; return Task.FromResult<JsonNode?>(JsonValue.Create(permission)); }
    }
    internal void AssertComplete(bool success, string? error, JsonNode? outputs)
    {
        if (variant == "invalid_input")
        { Require(!success && clones == 0 && publications == 0 && error == GnOuGo.Flow.Core.Models.ErrorCodes.InputValidation, "Invalid public input must stop before business invocation."); return; }
        Require(clones == 1 && cleanups == (variant == "setup_failure" ? 0 : 1), "One clone and cleanup of every materialized clone are mandatory.");
        if (variant is "setup_failure" or "execution_failure")
        {
            Require(!success && publications == 0 && (error?.Contains(variant == "setup_failure" ? "FROZEN_SETUP_FAILURE" : "FROZEN_EXECUTION_FAILURE", StringComparison.Ordinal) ?? false), "Original execution failure must be preserved.");
            return;
        }
        Require(success && outputs is not null && checks.Count == 4 && reviews == 1 && confirmations == 1, "Execution must return complete validated review evidence.");
        Require(publications == (variant == "rejected" ? 0 : 1), "Publication must follow permission.");
        Require(outputs.ToJsonString().Contains(variant is "mixed" or "boundary" ? "REQUEST_CHANGES" : "APPROVE", StringComparison.Ordinal), "Public outputs must expose the correct business decision.");
    }
}
