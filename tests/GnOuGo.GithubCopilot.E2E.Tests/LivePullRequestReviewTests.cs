using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Git.Mcp;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using LibGit2Sharp;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.E2E.Tests;

public sealed class LivePullRequestReviewTests
{
    private const string EnableVariable = "GNOU_GO_LIVE_PR_REVIEW_E2E";
    private const string ProviderSecretKey = "LLM--Models--OpenAi";
    private const string GithubMcpSecretKey = "LLM--McpServers--Github";
    private const string GitTokenSecretKey = "LLM--McpServerOverrides--GnOuGo.Git.Mcp--Git--Token";
    private const string ReviewMarker = "[GnOuGo E2E]";

    [Fact]
    public async Task ConfiguredOpenAiProvider_CanPublishValidatedCommentReview()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal),
            $"Set {EnableVariable}=1 to create the disposable live GitHub fixture.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = timeout.Token;
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();
        var keyVaultPath = KeyVaultDatabasePathResolver.Resolve(null, repositoryRoot);
        Assert.True(File.Exists(keyVaultPath), "The shared GnOuGo KeyVault database was not found.");

        var secretReader = new KeyVaultSecretReader(keyVaultPath);
        var providerConfigRaw = await RequireSecretAsync(secretReader, ProviderSecretKey, ct);
        var githubConfigRaw = await RequireSecretAsync(secretReader, GithubMcpSecretKey, ct);
        var githubConfig = ParseObject(githubConfigRaw, GithubMcpSecretKey);
        var githubOptions = BuildGithubOptions(githubConfig);
        var gitToken = await secretReader.GetDefaultTenantSecretValueAsync(GitTokenSecretKey, nameof(LivePullRequestReviewTests), ct)
            ?? githubOptions.ApiKey;
        Assert.False(string.IsNullOrWhiteSpace(gitToken), "No in-memory Git credential is configured for the fixture lifecycle.");

        var sensitiveValues = CollectSensitiveValues(ParseObject(providerConfigRaw, ProviderSecretKey), githubConfig, gitToken!);
        var (owner, repository, remoteUrl) = ReadOrigin(repositoryRoot);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var branchName = $"gnougo/e2e-pr-review-{runId}";
        var fixtureRelativePath = $"e2e-fixtures/GnOuGoPrReviewFixture-{runId}.cs";
        var fixtureCloneRelative = $"workflows/e2e/pr-review-fixture-{runId}";
        var reviewCloneRelative = $"workflows/e2e/pr-review-readonly-{runId}";
        var tenantId = "e2e-pr-review";

        var fixtureGit = CreateFixtureGitService(workspaceRoot, gitToken!);
        ConfiguredMcpClientFactory? mcpFactory = null;
        IMcpSession? github = null;
        IMcpSession? copilot = null;
        string? fixtureProjectRoot = null;
        int? pullNumber = null;
        bool branchPushed = false;
        bool reviewSubmitted = false;

        try
        {
            mcpFactory = CreateMcpFactory(repositoryRoot, workspaceRoot, keyVaultPath, githubOptions, gitToken!);
            using (ConfiguredMcpClientFactory.PushCorrelationContext(new McpCorrelationContext
            {
                TenantId = tenantId,
                CorrelationId = runId,
                RunId = runId,
                StepId = "provider-preflight",
                StepType = "e2e",
                Context = new JsonObject
                {
                    ["repository"] = $"{owner}/{repository}"
                }
            }))
            {
                copilot = await mcpFactory.GetClientAsync("copilot", ct);
                await RequireToolsAsync(copilot, ["copilot_one_shot", "copilot_review", "copilot_review_publication_gate"], ct);
                var providerPreflight = await CallAsync(copilot, "copilot_one_shot", new JsonObject
                {
                    ["projectRoot"] = ".",
                    ["prompt"] = "Reply with READY and no other text.",
                    ["permissionMode"] = "deny",
                    ["provider"] = "OpenAi",
                    ["tenantId"] = tenantId
                }, ct);
                Assert.Contains("READY", RequireString(providerPreflight, "content"), StringComparison.OrdinalIgnoreCase);
                AssertSafe(providerPreflight, sensitiveValues);
            }

            var fixtureClone = fixtureGit.Clone(remoteUrl, fixtureCloneRelative, historyDepth: 0, fetchAllBranches: false);
            fixtureProjectRoot = fixtureClone.ProjectRootRelative;
            var baseBranch = Assert.IsType<string>(fixtureClone.ResolvedBranch);
            Assert.False(string.IsNullOrWhiteSpace(baseBranch));
            fixtureGit.CreateBranch(fixtureProjectRoot, branchName, checkout: true);
            await WriteFixtureAsync(workspaceRoot, fixtureProjectRoot, fixtureRelativePath, ct);
            fixtureGit.Stage(fixtureProjectRoot, [fixtureRelativePath]);
            var fixtureCommit = fixtureGit.Commit(
                fixtureProjectRoot,
                "test: add automated PR review E2E fixture",
                "GnOuGo E2E",
                "gnougo-e2e@localhost");
            fixtureGit.Push(fixtureProjectRoot, branchName: branchName);
            branchPushed = true;

            github = await mcpFactory.GetClientAsync("github", ct);
            await RequireToolsAsync(
                github,
                ["create_pull_request", "pull_request_read", "pull_request_review_write", "add_comment_to_pending_review", "update_pull_request"],
                ct);

            var created = await CallAsync(github, "create_pull_request", new JsonObject
            {
                ["owner"] = owner,
                ["repo"] = repository,
                ["base"] = baseBranch,
                ["head"] = branchName,
                ["draft"] = true,
                ["title"] = "[E2E] GnOuGo automated PR review fixture",
                ["body"] = "Disposable non-production fixture for validating the GnOuGo automated pull-request review pipeline. It will be closed and its branch deleted; it must never be merged."
            }, ct);
            pullNumber = RequirePullNumber(created);

            var details = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get", ct);
            var pullObject = FindObjectContaining(details, "base", "head");
            var baseSha = RequireNestedString(pullObject, "base", "sha");
            var headSha = RequireNestedString(pullObject, "head", "sha");
            var resolvedHeadBranch = RequireNestedString(pullObject, "head", "ref");
            Assert.Equal(fixtureCommit.Sha, headSha, ignoreCase: true);
            Assert.Equal(branchName, resolvedHeadBranch);

            using var correlation = ConfiguredMcpClientFactory.PushCorrelationContext(new McpCorrelationContext
            {
                TenantId = tenantId,
                CorrelationId = runId,
                RunId = runId,
                StepId = "live-pr-review",
                StepType = "e2e",
                Context = new JsonObject
                {
                    ["repository"] = $"{owner}/{repository}",
                    ["pullRequestNumber"] = pullNumber,
                    ["headSha"] = headSha
                }
            });

            // Read every PR input through the official GitHub MCP before analysis.
            _ = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_files", ct);
            _ = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_status", ct);
            _ = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_check_runs", ct);
            var reviewsBefore = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_reviews", ct);
            _ = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_review_comments", ct);

            var git = await mcpFactory.GetClientAsync("git-review", ct);
            await RequireToolsAsync(git, ["git_get_policy", "git_clone", "git_fetch", "git_compare_refs"], ct);
            var gitPolicy = await CallAsync(git, "git_get_policy", null, ct);
            Assert.True(RequireBool(gitPolicy, "reviewReadOnly"));
            Assert.False(RequireBool(gitPolicy, "allowMutations"));

            var reviewClone = await CallAsync(git, "git_clone", new JsonObject
            {
                ["remoteUrl"] = remoteUrl,
                ["targetDirectory"] = reviewCloneRelative,
                ["branch"] = baseBranch,
                ["historyDepth"] = 0,
                ["fetchAllBranches"] = false,
                ["tagFetchMode"] = "none"
            }, ct);
            var reviewProjectRoot = RequireString(reviewClone, "projectRootRelative");
            _ = await CallAsync(git, "git_fetch", new JsonObject
            {
                ["projectRoot"] = reviewProjectRoot,
                ["remoteName"] = "origin",
                ["refSpec"] = $"refs/heads/{branchName}:refs/remotes/origin/{branchName}"
            }, ct);

            var comparedFiles = await CompareAllPagesAsync(git, reviewProjectRoot, baseSha, headSha, ct);
            Assert.Contains(comparedFiles, file => string.Equals(RequireString(file, "path"), fixtureRelativePath, StringComparison.Ordinal));
            Assert.DoesNotContain(comparedFiles, file => RequireBool(file, "truncated"));

            var reviewResult = await CallAsync(copilot, "copilot_review", new JsonObject
            {
                ["projectRoot"] = reviewProjectRoot,
                ["baseSha"] = baseSha,
                ["headSha"] = headSha,
                ["filesJson"] = new JsonArray(comparedFiles.Select(static file => file.DeepClone()).ToArray()).ToJsonString(),
                ["reviewInstructions"] = "Report only demonstrable correctness defects introduced by the supplied fixture diff. Prefer a small number of high-confidence findings.",
                ["provider"] = "OpenAi",
                ["maxBatchCharacters"] = 60_000,
                ["tenantId"] = tenantId
            }, ct);
            AssertSafe(reviewResult, sensitiveValues);

            var findings = RequireArray(reviewResult, "findings")
                .OfType<JsonObject>()
                .Where(finding => string.Equals(RequireString(finding, "path"), fixtureRelativePath, StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(findings);

            // Prove the interactive gate fails closed and performs no GitHub write.
            var rejectedGate = await CallAsync(copilot, "copilot_review_publication_gate", new JsonObject
            {
                ["expectedHeadSha"] = headSha,
                ["currentHeadSha"] = headSha,
                ["publicationPolicy"] = "interactive",
                ["validatedFindingCount"] = findings.Length,
                ["humanApproved"] = false,
                ["proposedEvent"] = "comment"
            }, ct);
            Assert.False(RequireBool(rejectedGate, "mayWrite"));
            var reviewsAfterRejectedGate = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_reviews", ct);
            Assert.Equal(CountResultItems(reviewsBefore), CountResultItems(reviewsAfterRejectedGate));

            // The live fixture explicitly opts into the only headless write policy: COMMENT.
            var freshDetails = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get", ct);
            var freshHeadSha = RequireNestedString(FindObjectContaining(freshDetails, "base", "head"), "head", "sha");
            var publicationGate = await CallAsync(copilot, "copilot_review_publication_gate", new JsonObject
            {
                ["expectedHeadSha"] = headSha,
                ["currentHeadSha"] = freshHeadSha,
                ["publicationPolicy"] = "auto_comment",
                ["validatedFindingCount"] = findings.Length,
                ["humanApproved"] = false,
                ["proposedEvent"] = "comment"
            }, ct);
            Assert.True(RequireBool(publicationGate, "mayWrite"));
            Assert.Equal("comment", RequireString(publicationGate, "submitEvent"), ignoreCase: true);

            _ = await CallAsync(github, "pull_request_review_write", new JsonObject
            {
                ["method"] = "create",
                ["owner"] = owner,
                ["repo"] = repository,
                ["pullNumber"] = pullNumber.Value,
                ["commitID"] = freshHeadSha
            }, ct);

            foreach (var finding in findings)
            {
                var startLine = RequireInt(finding, "startLine");
                var endLine = RequireInt(finding, "endLine");
                var side = RequireString(finding, "side").ToUpperInvariant();
                var comment = new JsonObject
                {
                    ["owner"] = owner,
                    ["repo"] = repository,
                    ["pullNumber"] = pullNumber.Value,
                    ["path"] = fixtureRelativePath,
                    ["subjectType"] = "LINE",
                    ["side"] = side,
                    ["line"] = endLine,
                    ["body"] = $"{ReviewMarker} {RequireString(finding, "explanation")}\n\nEvidence: {RequireString(finding, "evidence")}\n\nFingerprint: `{RequireString(finding, "fingerprint")}`"
                };
                if (startLine != endLine)
                {
                    comment["startLine"] = startLine;
                    comment["startSide"] = side;
                }
                _ = await CallAsync(github, "add_comment_to_pending_review", comment, ct);
            }

            var submitted = await CallAsync(github, "pull_request_review_write", new JsonObject
            {
                ["method"] = "submit_pending",
                ["owner"] = owner,
                ["repo"] = repository,
                ["pullNumber"] = pullNumber.Value,
                ["event"] = "COMMENT",
                ["body"] = $"{ReviewMarker} Automated review completed with {findings.Length} validated inline finding(s)."
            }, ct);
            reviewSubmitted = true;
            AssertSafe(submitted, sensitiveValues);

            var publishedReviews = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_reviews", ct);
            var publishedComments = await ReadPullRequestAsync(github, owner, repository, pullNumber.Value, "get_review_comments", ct);
            Assert.Contains(ReviewMarker, publishedReviews.ToJsonString(), StringComparison.Ordinal);
            Assert.Contains(ReviewMarker, publishedComments.ToJsonString(), StringComparison.Ordinal);
            AssertSafe(publishedReviews, sensitiveValues);
            AssertSafe(publishedComments, sensitiveValues);
        }
        finally
        {
            if (pullNumber is not null && github is not null)
            {
                if (!reviewSubmitted)
                    await TryDeletePendingReviewAsync(github, owner, repository, pullNumber.Value);
                await TryClosePullRequestAsync(github, owner, repository, pullNumber.Value);
            }

            if (branchPushed && fixtureProjectRoot is not null)
                TryDeleteRemoteBranch(fixtureGit, fixtureProjectRoot, branchName);

            if (mcpFactory is not null)
                await mcpFactory.DisposeAsync();

            DeleteIsolatedDirectory(workspaceRoot, fixtureCloneRelative, "workflows/e2e");
            DeleteIsolatedDirectory(workspaceRoot, reviewCloneRelative, "workflows/e2e");
        }
    }

    private static ConfiguredMcpClientFactory CreateMcpFactory(
        string repositoryRoot,
        string workspaceRoot,
        string keyVaultPath,
        McpServerOptions github,
        string gitToken)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = github,
            ["git-review"] = new()
            {
                Type = "stdio",
                Command = dotnet,
                Args = [FindMcpAssembly(repositoryRoot, "GnOuGo.Git.Mcp")],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["Git__DefaultWorkingDirectory"] = workspaceRoot,
                    ["Git__AllowMutations"] = "false",
                    ["Git__AllowNetworkOperations"] = "true",
                    ["Git__ReviewReadOnly"] = "true",
                    ["Git__Token"] = gitToken,
                    ["OpenTelemetry__Enabled"] = "false"
                }
            },
            ["copilot"] = new()
            {
                Type = "stdio",
                Command = dotnet,
                Args = [FindMcpAssembly(repositoryRoot, "GnOuGo.GithubCopilot.Mcp")],
                CallTimeoutSeconds = 1_800,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["KeyVault__DatabasePath"] = keyVaultPath,
                    ["Code__DefaultWorkingDirectory"] = workspaceRoot,
                    ["Code__AllowWrites"] = "false",
                    ["Code__Copilot__UseLoggedInUser"] = "false",
                    ["OpenTelemetry__Enabled"] = "false"
                }
            }
        });
    }

    private static GitRepositoryService CreateFixtureGitService(string workspaceRoot, string token)
    {
        var settings = new GitServerSettings
        {
            DefaultWorkingDirectory = workspaceRoot,
            AllowedWorkingRoots = [workspaceRoot],
            AllowMutations = true,
            AllowNetworkOperations = true,
            ReviewReadOnly = false,
            Token = token
        };
        return new GitRepositoryService(new GitPolicy(settings, AppContext.BaseDirectory), Options.Create(settings));
    }

    private static async Task<JsonObject[]> CompareAllPagesAsync(
        IMcpSession git,
        string projectRoot,
        string baseSha,
        string headSha,
        CancellationToken ct)
    {
        var files = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var arguments = new JsonObject
            {
                ["projectRoot"] = projectRoot,
                ["baseRef"] = baseSha,
                ["headRef"] = headSha,
                ["compareFromMergeBase"] = true,
                ["pageSize"] = 20
            };
            if (cursor is not null)
                arguments["cursor"] = cursor;

            var page = await CallAsync(git, "git_compare_refs", arguments, ct);
            Assert.Equal(baseSha, RequireString(page, "baseSha"), ignoreCase: true);
            Assert.Equal(headSha, RequireString(page, "headSha"), ignoreCase: true);
            files.AddRange(RequireArray(page, "files").OfType<JsonObject>().Select(static file => (JsonObject)file.DeepClone()));
            cursor = RequireBool(page, "hasMore") ? RequireString(page, "nextCursor") : null;
        }
        while (cursor is not null);

        return files.ToArray();
    }

    private static Task<JsonNode> ReadPullRequestAsync(
        IMcpSession github,
        string owner,
        string repository,
        int pullNumber,
        string method,
        CancellationToken ct)
        => CallAsync(github, "pull_request_read", new JsonObject
        {
            ["method"] = method,
            ["owner"] = owner,
            ["repo"] = repository,
            ["pullNumber"] = pullNumber,
            ["perPage"] = 100
        }, ct);

    private static async Task<JsonNode> CallAsync(IMcpSession session, string toolName, JsonNode? arguments, CancellationToken ct)
    {
        var result = await session.CallToolAsync(toolName, arguments, ct);
        var errorMessage = ExtractErrorMessage(result.Content);
        Assert.False(result.IsError, $"MCP tool '{toolName}' returned an error: {RedactError(errorMessage)}");
        return result.Content ?? new JsonObject();
    }

    private static string RedactError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "no structured error message";
        var redacted = Regex.Replace(message, @"(?i)(api[_-]?key|token|authorization|secret)\s*[:=]\s*\S+", "$1=<redacted>");
        return redacted.Length <= 1_000 ? redacted : redacted[..1_000] + "…";
    }

    private static string? ExtractErrorMessage(JsonNode? content)
    {
        if (content is null)
            return null;
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return OptionalString(content, "error_message")
               ?? OptionalString(content, "message")
               ?? OptionalString(content, "text");
    }

    private static async Task RequireToolsAsync(IMcpSession session, IReadOnlyList<string> required, CancellationToken ct)
    {
        var names = (await session.ListToolsAsync(ct)).Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in required)
            Assert.Contains(name, names);
    }

    private static McpServerOptions BuildGithubOptions(JsonObject config)
    {
        var configuredUrl = RequireString(config, "url");
        var endpoint = new Uri(configuredUrl, UriKind.Absolute);
        Assert.Equal("api.githubcopilot.com", endpoint.Host, ignoreCase: true);
        Assert.DoesNotContain("insiders", endpoint.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview", endpoint.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        var pullRequestsEndpoint = new UriBuilder(endpoint)
        {
            Path = "/mcp/x/pull_requests",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;

        return new McpServerOptions
        {
            Type = "http",
            Description = "Official GitHub MCP GA pull_requests toolset",
            Url = pullRequestsEndpoint,
            ApiKey = OptionalString(config, "apiKey") ?? OptionalString(config, "api_key"),
            DiscoveryTimeoutSeconds = 120,
            CallTimeoutSeconds = 1_200
        };
    }

    private static async Task WriteFixtureAsync(
        string workspaceRoot,
        string projectRoot,
        string fixtureRelativePath,
        CancellationToken ct)
    {
        var projectPath = Path.GetFullPath(Path.Combine(workspaceRoot, projectRoot));
        var fixturePath = Path.GetFullPath(Path.Combine(projectPath, fixtureRelativePath));
        Assert.True(GnOuGoWorkspace.IsPathWithinRoot(fixturePath, projectPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        await File.WriteAllTextAsync(fixturePath, """
            namespace GnOuGo.E2E;

            // Disposable correctness fixture used only by the live PR-review test.
            public static class ArithmeticFixture
            {
                public static int Divide(int numerator, int denominator)
                {
                    if (denominator == 0)
                        throw new DivideByZeroException();

                    return numerator / (denominator - denominator);
                }
            }
            """, ct);
    }

    private static (string Owner, string Repository, string RemoteUrl) ReadOrigin(string repositoryRoot)
    {
        using var repository = new Repository(repositoryRoot);
        var remoteUrl = repository.Network.Remotes["origin"]?.Url
            ?? throw new InvalidOperationException("The current repository has no origin remote.");
        var normalized = remoteUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? remoteUrl[..^4] : remoteUrl;
        string path;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath.Trim('/');
        else
            path = normalized[(normalized.LastIndexOf(':') + 1)..].Trim('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 2, "Could not parse owner/repository from the origin URL.");
        return (parts[^2], parts[^1], $"https://github.com/{parts[^2]}/{parts[^1]}.git");
    }

    private static string FindRepositoryRoot()
        => GnOuGoWorkspace.DiscoverWorkspaceRoot(AppContext.BaseDirectory)
           ?? throw new InvalidOperationException("Could not locate the GnOuGo repository root.");

    private static string FindMcpAssembly(string repositoryRoot, string projectName)
    {
#if DEBUG
        string[] configurations = ["Debug", "Release"];
#else
        string[] configurations = ["Release", "Debug"];
#endif
        var candidates = new[]
        {
            Path.Combine(repositoryRoot, "src", projectName, "bin", configurations[0], "net10.0", $"{projectName}.dll"),
            Path.Combine(repositoryRoot, "src", projectName, "bin", configurations[1], "net10.0", $"{projectName}.dll")
        };
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException($"Build {projectName} before running the live E2E test.");
    }

    private static async Task<string> RequireSecretAsync(KeyVaultSecretReader reader, string key, CancellationToken ct)
    {
        var value = await reader.GetDefaultTenantSecretValueAsync(key, nameof(LivePullRequestReviewTests), ct);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Required KeyVault secret '{key}' is not configured.");
        return value!;
    }

    private static JsonObject ParseObject(string value, string source)
        => JsonNode.Parse(value) as JsonObject
           ?? throw new InvalidOperationException($"KeyVault secret '{source}' must contain a JSON object.");

    private static string RequireString(JsonNode node, string property)
        => FindProperty(node, property)?.GetValue<string>()
           ?? throw new InvalidOperationException($"Expected string property '{property}'.");

    private static string? OptionalString(JsonNode node, string property)
    {
        var value = FindProperty(node, property);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var result) ? result : null;
    }

    private static int RequireInt(JsonNode node, string property)
    {
        var value = FindProperty(node, property) ?? throw new InvalidOperationException($"Expected integer property '{property}'.");
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var result))
            return result;
        if (value is JsonValue longValue && longValue.TryGetValue<long>(out var longResult))
            return checked((int)longResult);
        throw new InvalidOperationException($"Property '{property}' was not an integer.");
    }

    private static int RequirePullNumber(JsonNode node)
    {
        foreach (var property in new[] { "number", "pullNumber", "pull_number" })
        {
            var value = FindProperty(node, property);
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number))
                return number;
            if (value is JsonValue longValue && longValue.TryGetValue<long>(out var longNumber))
                return checked((int)longNumber);
        }

        foreach (var text in EnumerateStringValues(node))
        {
            var match = Regex.Match(text, @"/pull/(?<number>\d+)(?:\D|$)", RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups["number"].Value, out var number))
                return number;
        }

        throw new InvalidOperationException("The GitHub MCP create_pull_request result did not contain a PR number or PR URL.");
    }

    private static bool RequireBool(JsonNode node, string property)
        => FindProperty(node, property)?.GetValue<bool>()
           ?? throw new InvalidOperationException($"Expected Boolean property '{property}'.");

    private static JsonArray RequireArray(JsonNode node, string property)
        => FindProperty(node, property) as JsonArray
           ?? throw new InvalidOperationException($"Expected array property '{property}'.");

    private static JsonObject FindObjectContaining(JsonNode node, params string[] properties)
    {
        if (node is JsonObject obj && properties.All(property => GetDirectProperty(obj, property) is not null))
            return obj;
        foreach (var child in EnumerateChildren(node))
        {
            try { return FindObjectContaining(child, properties); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Expected an object containing: {string.Join(", ", properties)}.");
    }

    private static string RequireNestedString(JsonObject obj, string parent, string property)
    {
        var parentNode = GetDirectProperty(obj, parent) as JsonObject
            ?? throw new InvalidOperationException($"Expected object property '{parent}'.");
        return OptionalString(parentNode, property)
            ?? throw new InvalidOperationException($"Expected string property '{parent}.{property}'.");
    }

    private static JsonNode? FindProperty(JsonNode node, string property)
    {
        if (node is JsonObject obj)
        {
            var direct = GetDirectProperty(obj, property);
            if (direct is not null)
                return direct;
        }
        foreach (var child in EnumerateChildren(node))
        {
            var result = FindProperty(child, property);
            if (result is not null)
                return result;
        }
        return null;
    }

    private static JsonNode? GetDirectProperty(JsonObject obj, string property)
        => obj.FirstOrDefault(item => string.Equals(item.Key, property, StringComparison.OrdinalIgnoreCase)).Value;

    private static IEnumerable<JsonNode> EnumerateChildren(JsonNode node)
        => node switch
        {
            JsonObject obj => obj.Select(static item => item.Value).OfType<JsonNode>(),
            JsonArray array => array.OfType<JsonNode>(),
            _ => []
        };

    private static IEnumerable<string> EnumerateStringValues(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            yield return text;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var childText in EnumerateStringValues(child))
                yield return childText;
        }
    }

    private static int CountResultItems(JsonNode node)
    {
        if (node is JsonArray array)
            return array.Count;
        foreach (var property in new[] { "reviews", "items", "nodes" })
        {
            if (FindProperty(node, property) is JsonArray result)
                return result.Count;
        }
        return 0;
    }

    private static IReadOnlyList<string> CollectSensitiveValues(JsonObject provider, JsonObject github, string gitToken)
    {
        var values = new HashSet<string>(StringComparer.Ordinal) { gitToken };
        foreach (var config in new[] { provider, github })
        {
            foreach (var key in new[] { "apiKey", "api_key", "bearerToken", "bearer_token", "clientSecret", "client_secret", "privateKeyPem", "private_key_pem" })
            {
                var value = OptionalString(config, key);
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }
        return values.Where(static value => value.Length >= 8).ToArray();
    }

    private static void AssertSafe(JsonNode value, IReadOnlyList<string> sensitiveValues)
    {
        var serialized = value.ToJsonString();
        foreach (var sensitive in sensitiveValues)
            Assert.DoesNotContain(sensitive, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("<think>", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chain-of-thought", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task TryDeletePendingReviewAsync(IMcpSession github, string owner, string repository, int pullNumber)
    {
        try
        {
            _ = await github.CallToolAsync("pull_request_review_write", new JsonObject
            {
                ["method"] = "delete_pending",
                ["owner"] = owner,
                ["repo"] = repository,
                ["pullNumber"] = pullNumber
            }, CancellationToken.None);
        }
        catch { }
    }

    private static async Task TryClosePullRequestAsync(IMcpSession github, string owner, string repository, int pullNumber)
    {
        try
        {
            _ = await github.CallToolAsync("update_pull_request", new JsonObject
            {
                ["owner"] = owner,
                ["repo"] = repository,
                ["pullNumber"] = pullNumber,
                ["state"] = "closed"
            }, CancellationToken.None);
        }
        catch { }
    }

    private static void TryDeleteRemoteBranch(GitRepositoryService git, string projectRoot, string branchName)
    {
        try { git.DeleteRemoteBranch(projectRoot, "origin", branchName); }
        catch { }
    }

    private static void DeleteIsolatedDirectory(string workspaceRoot, string relativePath, string requiredParent)
    {
        var path = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        var parent = Path.GetFullPath(Path.Combine(workspaceRoot, requiredParent));
        if (!GnOuGoWorkspace.IsPathWithinRoot(path, parent))
            throw new InvalidOperationException($"Refusing to delete E2E path outside '{requiredParent}'.");
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
