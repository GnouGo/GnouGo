using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DelegatedTestReviewTests
{
    [Theory]
    [InlineData("workflows/campaign/fresh-server-run", true)]
    [InlineData("workflows/other/fresh-server-run", false)]
    [InlineData("workflows/campaign/../escape", false)]
    [InlineData("/tmp/escape", false)]
    public async Task ExplicitServerWorkspaceMustMatchTheSignedConfinedAuthorization(string path, bool allowed)
    {
        var (state, _, original, evidence, receipt) = await Fixture();
        Assert.DoesNotContain("workspaceRelativePath", JsonSerializer.Serialize(original, DelegatedTestJsonContext.Default.DelegatedTestAuthorization));
        var authorization = original with { WorkspaceRelativePath = path };
        receipt = receipt with { AuthorizationHash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization)) };
        var authority = new DelegatedTestReviewAuthority(authorization, new(receipt, evidence), "campaign", "test-run", "synthetic",
            Path.GetTempPath(), "frozen-source", path);
        if (allowed) authority.Validate(state);
        else Assert.Throws<PlanningConflictException>(() => authority.Validate(state));
        Assert.Throws<PlanningConflictException>(() => Authority(authorization, evidence, receipt).Validate(state));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://www.amazon.fr/gp/aw/d/B0CFXD921L?pd_rd_plhdr=t&qid=1794055831", true)]
    [InlineData("https://amazon.fr/gp/aw/d/B0CFXD921L", true)]
    [InlineData("http://www.amazon.fr/gp/aw/d/B0CFXD921L", false)]
    [InlineData("https://user@www.amazon.fr/gp/aw/d/B0CFXD921L", false)]
    [InlineData("https://www.amazon.fr.example.com/gp/aw/d/B0CFXD921L", false)]
    [InlineData("https://www.amazon.fr/gp/aw/d/short", false)]
    [InlineData("https://www.amazon.fr/gp/aw/d/B0CFXD921L/orders", false)]
    [InlineData("https://www.amazon.fr/gp/aw/c/B0CFXD921L", false)]
    [InlineData("https://www.amazon.fr/gp/aw/d/%2Fcart%2F", false)]
    [InlineData("https://www.amazon.fr/ap/signin", false)]
    public async Task ObservedMobileProductRouteRetainsExactHostAndReadOnlyPathRestrictions(string url, bool allowed)
    {
        var (_, _, authorization, evidence, receipt) = await Fixture();
        var authority = Authority(authorization with { Scenario = "amazon" }, evidence, receipt, "amazon");
        void Read() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", new JsonObject { ["url"] = url });
        if (allowed) Read(); else Assert.Throws<PlanningConflictException>(Read);
    }

    [Fact]
    public async Task LocalPermissionDenialIsKnownBeforeTransportAndDurableCleanupIsNotBlocked()
    {
        var (_, _, authorization, evidence, receipt) = await Fixture();
        var authority = Authority(authorization with { Scenario = "amazon" }, evidence, receipt, "amazon");
        var transport = new GuardTransport(); var events = new List<JsonObject>();
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, e => { events.Add(e); return Task.CompletedTask; }, authority);
        var parsed = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: denied
                    type: mcp.call
                    input:
                      server: GnOuGo.Browser.Mcp
                      method: browser_get_content
                      request: { url: 'https://www.amazon.fr/ap/signin' }
                finally:
                  - id: cleanup
                    type: mcp.call
                    input:
                      server: GnOuGo.Browser.Mcp
                      method: browser_close
                      request: {}
            """);
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(parsed).Workflows["main"];
        var store = new InMemoryWorkflowRunStore();
        WorkflowEngine Engine() => new() { McpClientFactory = observed, RunStore = store, Limits = new() { TenantId = "benchmark", RunId = "guard-test" } };
        var result = await Engine().ExecuteAsync(compiled, null, Ct);
        Assert.False(result.Success); Assert.NotEqual("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
        Assert.Equal(["browser_close"], transport.Calls);
        var denial = events.Single(e => e["origin"]?.ToString() == "delegated_permission_guard");
        Assert.False(denial["dispatched"]!.GetValue<bool>()); Assert.True(denial["error"]!.GetValue<bool>());
        var run = (await store.ReadAsync("benchmark", "guard-test", Ct))!;
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.DoesNotContain(run.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
        var known = run.Invocations.Values.Single(i => i.Id.EndsWith("/denied", StringComparison.Ordinal));
        Assert.True(known.ExternalCompletionObserved); Assert.NotNull(known.CompletedAt);
        await Engine().ResumeAsync("benchmark", "guard-test", run.Revision, compiled, Ct);
        Assert.Equal(["browser_close"], transport.Calls); // Cold engine restores failure and cleanup without replay.
    }

    private sealed class GuardTransport : IMcpClientFactory, IMcpSession, IMcpExecutionHooks
    {
        internal List<string> Calls { get; } = [];
        public string ServerName => "GnOuGo.Browser.Mcp";
        public IReadOnlyList<McpServerMetadata> ServerMetadata { get; } = [new() { Name = "GnOuGo.Browser.Mcp" }];
        public Task<IMcpSession> GetClientAsync(string name, CancellationToken ct) => Task.FromResult<IMcpSession>(this);
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>(
            [new() { Name = "browser_get_content", InputSchema = new JsonObject { ["type"] = "object" } }, new() { Name = "browser_close", InputSchema = new JsonObject { ["type"] = "object" } }]);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? args, CancellationToken ct) => throw new NotSupportedException();
        public Task<McpCallResult> CallToolAsync(string name, JsonNode? args, CancellationToken ct)
        { Calls.Add(name); return Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = true } }); }
        public IDisposable BeginCall(McpCallExecutionContext context) => new Scope();
        public string FormatFailureDiagnostics(string name, Exception exception) => exception.GetType().Name;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"response\":true}")]
    [InlineData("{\"interaction_preflight\":\"stale\"}")]
    [InlineData("invalid-json")]
    public async Task InteractionPreflightRejectsUnavailableOrNonmatchingChannels(string response)
    {
        using var input = new StringReader(response); using var output = new StringWriter();
        if (response.Length == 0)
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowEvaluation.RequireInteractionAsync(input, output, "fresh-challenge", Ct));
        else await Assert.ThrowsAsync<PlanningConflictException>(() => LiveWorkflowEvaluation.RequireInteractionAsync(input, output, "fresh-challenge", Ct));
        Assert.Contains("grants no execution permission", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractionPreflightAcceptsOnlyItsFreshChallengeAndChecksTheBuild()
    {
        using var input = new StringReader("{\"interaction_preflight\":\"fresh-challenge\"}");
        using var output = new StringWriter();
        await LiveWorkflowEvaluation.RequireInteractionAsync(input, output, "fresh-challenge", Ct);
        var source = new string('a', 40);
        LiveWorkflowEvaluation.RequireBuildSource("1.0.0+" + source, source);
        Assert.Throws<PlanningConflictException>(() => LiveWorkflowEvaluation.RequireBuildSource("1.0.0+" + new string('b', 40), source));
        Assert.Throws<PlanningConflictException>(() => LiveWorkflowEvaluation.RequireBuildSource(null, source));
    }

    [Fact]
    public void McpBuildPreflightReadsMetadataWithoutStartingTheServer()
    {
        var assembly = typeof(WorkflowEngine).Assembly;
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)!.InformationalVersion;
        var source = version[(version.LastIndexOf('+') + 1)..];
        var servers = new Dictionary<string, GnOuGo.AI.Core.McpServerOptions>
            { ["test-server"] = new() { Type = "stdio", Command = "dotnet", Args = [assembly.Location] } };
        var builds = LiveWorkflowEvaluation.CaptureMcpBuilds(servers, source);
        Assert.Equal(source, builds["test-server"]!["source"]!.ToString());
        Assert.Equal(64, builds["test-server"]!["sha256"]!.ToString().Length);
        Assert.Throws<PlanningConflictException>(() => LiveWorkflowEvaluation.CaptureMcpBuilds(servers, new string('a', 40)));
        servers["test-server"].Args = ["run", "--project", "mutable.csproj"];
        Assert.Throws<PlanningConflictException>(() => LiveWorkflowEvaluation.CaptureMcpBuilds(servers, source));
    }

    [Fact]
    public async Task VisibleMethodsStillRequireConcreteAuthorityAndCanonicalAliasesRemainDenied()
    {
        var (_, _, authorization, evidence, receipt) = await Fixture();
        var authority = Authority(authorization, evidence, receipt, "code");
        Assert.True(DelegatedTestReviewAuthority.AllowsTool("code", "GnOuGo.Cmd.Mcp", "cmd_run"));
        authority.RequireTool("GnOuGo.Cmd.Mcp", "cmd_run", null, inspectOnly: true);
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Cmd.Mcp", "cmd_run",
            new JsonObject { ["commandName"] = "unrelated_command" }));
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.GithubCopilot.Mcp",
            "copilot_interactive_one_shot", null, inspectOnly: true));
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Git.Mcp", "git_push", null, inspectOnly: true));
        Assert.False(DelegatedTestReviewAuthority.AllowsTool("code", "another-provider", "code_agent_edit"));
        Assert.False(DelegatedTestReviewAuthority.AllowsTool("synthetic", "GnOuGo.Cmd.Mcp", "cmd_run"));
    }

    [Fact]
    public async Task DelegatedApprovalPreservesArtifactAndCannotAuthorizeAProductionHost()
    {
        var (state, runtime, authorization, evidence, receipt) = await Fixture();
        var artifact = state.ComputeArtifactHash();
        var authority = Authority(authorization, evidence, receipt);
        var approved = await new HybridWorkflowPlanner().AdvanceAsync(state, authority.Command(),
            new DelegatedTestReviewAuthority.Runtime(runtime, authority), Ct);
        Assert.Equal(PlanningStatus.Approved, approved.Status);
        Assert.Equal(artifact, approved.ComputeArtifactHash());
        Assert.Equal(artifact, approved.ApprovedHash);
        Assert.Single(approved.ValidationResults, r => r.Outcome == "delegated_test_reviewed");
        Assert.DoesNotContain(approved.ValidationResults, r => r.Outcome == "human_reviewed");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(approved, PlanningJsonContext.Default.PlanningSession),
            PlanningJsonContext.Default.PlanningSession)!;
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(restored));
        PlanningArtifactApproval.Verify(restored, authority.Receipt);
        Assert.Null(state.ApprovalReview);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
    }

    [Theory]
    [InlineData("unfinished")]
    [InlineData("candidate")]
    [InlineData("scope")]
    [InlineData("requirement")]
    [InlineData("requirement_checkpoint")]
    [InlineData("local_review")]
    [InlineData("global_review")]
    [InlineData("stale_review")]
    public async Task CompilableArtifactCannotBypassIncompleteConstruction(string defect)
    {
        var (state, _, _, _, _) = await Fixture();
        var checkpoint = state.Construction!.Checkpoints.Last();
        PlanningArtifactApproval.Verify(state);
        switch (defect)
        {
            case "unfinished": state.Construction.Complete = false; break;
            case "candidate": state.Construction.Candidate = new(); break;
            case "scope": state.Construction.OpenScopes.Add("/root"); break;
            case "requirement": state.Construction.Requirements[0].Status = "remaining"; break;
            case "requirement_checkpoint": state.Construction.Requirements[0].Checkpoints = [999]; break;
            case "local_review": checkpoint.Reviews.Clear(); break;
            case "global_review": checkpoint.Reviews[0]["purpose"] = "construction_local_judge"; break;
            case "stale_review": checkpoint.Reviews[0]["candidateHash"] = new string('c', 64); break;
        }
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Theory]
    [InlineData("authority_missing")]
    [InlineData("wrong_authorization")]
    [InlineData("wrong_reviewer")]
    [InlineData("wrong_owner")]
    [InlineData("wrong_revision")]
    [InlineData("wrong_artifact")]
    [InlineData("wrong_source")]
    [InlineData("wrong_requirement")]
    [InlineData("missing_obligation")]
    [InlineData("changed_task")]
    [InlineData("wrong_evidence_hash")]
    [InlineData("duplicate_acknowledgment")]
    public async Task DelegationRequiresIndependentAuthorityAndExactReviewEvidence(string defect)
    {
        var (state, runtime, authorization, evidence, receipt) = await Fixture();
        switch (defect)
        {
            case "wrong_authorization": receipt = receipt with { AuthorizationHash = new('0', 64) }; break;
            case "wrong_reviewer": receipt = receipt with { Reviewer = "model-judge" }; break;
            case "wrong_owner": receipt = receipt with { TenantId = "other" }; break;
            case "wrong_revision": receipt = receipt with { ReviewedRevision = receipt.ReviewedRevision - 1 }; break;
            case "wrong_artifact": receipt = receipt with { ArtifactHash = new('0', 64) }; break;
            case "wrong_source": authorization = authorization with { SourceRevision = "changed" }; break;
            case "wrong_requirement": evidence.Requirements[0] = evidence.Requirements[0] with { RequirementHash = new('0', 64) }; break;
            case "missing_obligation": evidence.Requirements[0].Obligations.RemoveAt(0); break;
            case "changed_task": state.Plan!.Root.Tasks[0].Objective = "Different work"; break;
            case "wrong_evidence_hash": receipt = receipt with { EvidenceHash = new('0', 64) }; break;
            case "duplicate_acknowledgment": receipt.ReviewedRequirementIds.Add(receipt.ReviewedRequirementIds[0]); break;
        }
        if (defect is "wrong_requirement" or "missing_obligation") receipt = receipt with { EvidenceHash = EvidenceHash(evidence) };
        var authority = Authority(authorization, evidence, receipt);
        state.Request.Options["delegated_test"] = true; // Serialized options never supply host authority.
        // Keep the command tied to the actual candidate when testing evidence rather than the artifact guard.
        if (defect is not ("wrong_artifact" or "changed_task")) receipt = receipt with { ArtifactHash = state.ComputeArtifactHash()! };
        authority = Authority(authorization, evidence, receipt);
        var snapshot = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, authority.Command(),
            defect == "authority_missing" ? runtime : new DelegatedTestReviewAuthority.Runtime(runtime, authority), Ct));
        Assert.Equal(snapshot, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("browser_click", "https://www.amazon.fr/s?k=test")]
    [InlineData("browser_get_content", "https://www.amazon.fr/gp/buy")]
    [InlineData("browser_get_content", "https://example.com/dp/product")]
    [InlineData("browser_get_content", "http://www.amazon.fr/s?k=test")]
    public async Task AmazonDelegationRejectsExtraEffectsBeforeTransport(string tool, string url)
    {
        var (_, _, authorization, evidence, receipt) = await Fixture();
        var authority = Authority(authorization with { Scenario = "amazon" }, evidence, receipt, "amazon");
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", tool, new JsonObject { ["url"] = url }));
        authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", new JsonObject { ["url"] = "https://www.amazon.fr/dp/opaque-product" });
        authority.RequireTool("GnOuGo.Browser.Mcp", "browser_close", new JsonObject());
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Document.Mcp", "document_write",
            new JsonObject { ["filePath"] = "workflows/other/products.xlsx" }));
    }

    [Theory]
    [InlineData("chaussure geox homme 45")]
    [InlineData("café & lampe + 45% ?")]
    public async Task DerivedSearchAddressesRemainSubjectToExactExecutionAuthority(string query)
    {
        var (_, _, authorization, evidence, receipt) = await Fixture();
        var authority = Authority(authorization with { Scenario = "amazon" }, evidence, receipt, "amazon");
        var address = "https://www.amazon.fr/s?k=" + Uri.EscapeDataString(query);
        authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", new JsonObject { ["url"] = address });
        Assert.Equal(query, Uri.UnescapeDataString(new Uri(address).Query[3..]));
        foreach (var denied in new[] { address.Replace("www.amazon.fr", "www.amazon.fr.example.com", StringComparison.Ordinal),
            address.Replace("/s?", "/ap/signin?", StringComparison.Ordinal), "http://www.amazon.fr/s?k=" + Uri.EscapeDataString(query) })
            Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", new JsonObject { ["url"] = denied }));
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_click", new JsonObject { ["url"] = address }));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("error")]
    [InlineData("new_navigation")]
    [InlineData("wrong_generation")]
    [InlineData("unapproved_redirect")]
    public async Task CursorReadsRequireExactProducerGenerationAndExpireWithBrowserContext(string invalidation)
    {
        var (_, _, authorization, evidence, receipt) = await Fixture();
        var authority = Authority(authorization with { Scenario = "amazon" }, evidence, receipt, "amazon");
        var args = new JsonObject { ["url"] = "https://www.amazon.fr/s?k=lamp", ["format"] = "observation_pages" };
        var page = new JsonObject { ["cursor"] = "snapshot:page:0", ["format"] = "observation" };
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", new JsonObject()));
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", page));
        authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", args);
        authority.ObserveTool("GnOuGo.Browser.Mcp", "browser_get_content", args, new() { Content = new JsonObject {
            ["success"] = true, ["url"] = args["url"]!.DeepClone(), ["observationManifest"] = new JsonObject {
                ["id"] = "snapshot", ["pages"] = new JsonArray(new JsonObject { ["cursor"] = "snapshot:page:0" }) } } });
        authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", page);
        var forged = page.DeepClone().AsObject(); forged["cursor"] = "snapshot:page:unissued";
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", forged));
        switch (invalidation)
        {
            case "close": authority.RequireTool("GnOuGo.Browser.Mcp", "browser_close", new JsonObject()); break;
            case "error": authority.ObserveTool("GnOuGo.Browser.Mcp", "browser_get_content", page, new() { IsError = true }); break;
            case "new_navigation": authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", args); break;
            default:
                Assert.Throws<PlanningConflictException>(() => authority.ObserveTool("GnOuGo.Browser.Mcp", "browser_get_content", page,
                    new() { Content = new JsonObject { ["success"] = true,
                        ["url"] = invalidation == "unapproved_redirect" ? "https://www.amazon.fr/ap/signin" : args["url"]!.ToString(),
                        ["observation"] = new JsonObject { ["id"] = "other", ["nextCursor"] = "other:page:1" } } }));
                break;
        }
        Assert.Throws<PlanningConflictException>(() => authority.RequireTool("GnOuGo.Browser.Mcp", "browser_get_content", page));
    }

    [Theory]
    [InlineData("wrong_run")]
    [InlineData("missing_owner")]
    [InlineData("unreviewed_owner")]
    [InlineData("missing_details")]
    [InlineData("sandbox_bypass")]
    [InlineData("broad_only")]
    public async Task RuntimeDelegationRequiresCorrelatedConcreteSingleOperation(string defect)
    {
        var (authority, _) = await RuntimeFixture();
        var request = new HumanInputRequest { RunId = "test-run", ParentInvocationId = "/workflow/main/step/n_" + PlanningGraphCompiler.Fingerprint("review")[..16],
            Context = new JsonObject { ["mcp_server"] = "GnOuGo.GithubCopilot.Mcp", ["mcp_method"] = "code_agent_edit" },
            Fields = [new() { Name = "answer", Description = "Shell command: pnpm test\nSandbox bypass requested: no", Options = ["Allow once", "Refuse"] }] };
        request.StepId = request.ParentInvocationId + "/human/concrete-dialog";
        using var active = authority.BeginCall(new(new() { TenantId = "benchmark", RunId = "test-run", StepId = request.ParentInvocationId,
            StepType = "mcp.call", ServerName = "GnOuGo.GithubCopilot.Mcp", MethodName = "code_agent_edit" }, _ => { }, _ => { }));
        authority.RequireRuntimeRequest(request);
        switch (defect)
        {
            case "wrong_run": request.RunId = "another"; break;
            case "missing_owner": request.ParentInvocationId = null; break;
            case "unreviewed_owner": request.ParentInvocationId = "/workflow/main/step/unreviewed"; break;
            case "missing_details": request.Fields[0].Description = ""; break;
            case "sandbox_bypass": request.Fields[0].Description = "Sandbox bypass requested: yes"; break;
            case "broad_only": request.Fields[0].Options = ["Allow all", "Refuse"]; break;
        }
        Assert.Throws<PlanningConflictException>(() => authority.RequireRuntimeRequest(request));
    }

    [Fact]
    public async Task CopiedArtifactSuffixCannotAuthorizeInactiveOrAmbiguousInvocations()
    {
        var (authority, _) = await RuntimeFixture();
        var step = "/workflow/main/step/n_" + PlanningGraphCompiler.Fingerprint("review")[..16];
        McpCallExecutionContext Context(string path) => new(new() { TenantId = "benchmark", RunId = "test-run", StepId = path,
            StepType = "mcp.call", ServerName = "GnOuGo.GithubCopilot.Mcp", MethodName = "code_agent_edit" }, _ => { }, _ => { });
        var request = new HumanInputRequest { RunId = "test-run", ParentInvocationId = step, StepId = step + "/human/request",
            Context = new JsonObject { ["mcp_server"] = "GnOuGo.GithubCopilot.Mcp", ["mcp_method"] = "code_agent_edit" },
            Fields = [new() { Name = "answer", Description = "Shell command: pnpm test\nSandbox bypass requested: no", Options = ["Allow once", "Refuse"] }] };
        Assert.Throws<PlanningConflictException>(() => authority.RequireRuntimeRequest(request));
        using (authority.BeginCall(Context(step)))
        {
            Assert.False(authority.RequireRuntimeRequest(request));
            Assert.Throws<PlanningConflictException>(() => authority.BeginCall(Context(step)));
            using (authority.BeginCall(Context("/different" + step)))
                Assert.Throws<PlanningConflictException>(() => authority.RequireRuntimeRequest(request));
            request.StepId = "/forged/human/request";
            Assert.Throws<PlanningConflictException>(() => authority.RequireRuntimeRequest(request));
        }
        request.StepId = step + "/human/request";
        Assert.Throws<PlanningConflictException>(() => authority.RequireRuntimeRequest(request));
    }

    [Theory]
    [InlineData("step")]
    [InlineData("prompt")]
    [InlineData("mode")]
    [InlineData("run")]
    [InlineData("parent")]
    public async Task ArtifactConfirmationMustBeTheExactReviewedHostGate(string defect)
    {
        var (authority, state) = await RuntimeFixture();
        var request = new HumanInputRequest { RunId = "test-run", Mode = HumanInputContract.ModeConfirm,
            StepId = "/workflow/main/step/n_" + PlanningGraphCompiler.Fingerprint(PlanningConfirmationGuards.Confirm)[..16],
            Prompt = state.Graph!.Summary, AllowAbandon = true, Choices = ["approve", "reject"] };
        Assert.True(authority.RequireRuntimeRequest(request));
        switch (defect)
        {
            case "step": request.StepId += "other"; break;
            case "prompt": request.Prompt += " changed"; break;
            case "mode": request.Mode = HumanInputContract.ModeChoice; break;
            case "run": request.RunId = "other"; break;
            case "parent": request.ParentInvocationId = "/unrelated"; break;
        }
        Assert.Throws<PlanningConflictException>(() => authority.RequireRuntimeRequest(request));
    }

    private static async Task<(DelegatedTestReviewAuthority, PlanningSession)> RuntimeFixture()
    {
        var (state, _, authorization, evidence, receipt) = await Fixture();
        state.Catalog!.Capabilities.Add(new() { Id = "review-capability", Server = "GnOuGo.GithubCopilot.Mcp", Method = "code_agent_edit" });
        var main = state.Graph!.Workflows.Single(w => w.Key == state.Graph.Entrypoint);
        main.Steps.Add(new() { Key = "review", Type = "mcp.call", CapabilityId = "review-capability" });
        main.Steps.Add(new() { Key = PlanningConfirmationGuards.Confirm, Type = "human.input", InternalRole = "confirmation" });
        authorization = authorization with { Scenario = "code" };
        evidence.Requirements[0] = evidence.Requirements[0] with { Obligations = [.. DelegatedTestReviewAuthority.Obligations("code")] };
        receipt = receipt with { ArtifactHash = state.ComputeArtifactHash()!, EvidenceHash = EvidenceHash(evidence),
            AuthorizationHash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization)) };
        var authority = Authority(authorization, evidence, receipt, "code"); authority.Validate(state);
        return (authority, state);
    }

    private static DelegatedTestReviewAuthority Authority(DelegatedTestAuthorization authorization, DelegatedReviewEvidence evidence,
        PlanningApprovalReview receipt, string scenario = "synthetic") => new(authorization, new(receipt, evidence),
        "campaign", "test-run", scenario, Path.GetTempPath(), "frozen-source");

    private static string EvidenceHash(DelegatedReviewEvidence evidence) => PlanningGraphCompiler.Fingerprint(
        JsonSerializer.Serialize(evidence, DelegatedTestJsonContext.Default.DelegatedReviewEvidence));

    private static async Task<(PlanningSession, IPlanningRuntime, DelegatedTestAuthorization, DelegatedReviewEvidence, PlanningApprovalReview)> Fixture()
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "test-run", null, Ct);
        var requirement = Assert.Single(state.Requirements!.Outcomes);
        var task = Assert.Single(state.Plan!.Root.Tasks);
        var authorization = new DelegatedTestAuthorization("explicit-delegation", "User delegates this isolated test review", "codex",
            "campaign", "test-run", "synthetic", Path.GetTempPath(), "frozen-source");
        var evidence = new DelegatedReviewEvidence([new(requirement.Id,
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(requirement, PlanningJsonContext.Default.PlanningRequirement)),
            ["exact_outputs", "declared_effects", "cleanup"], "accepted_with_recorded_uncertainty")],
            [new(task.Id, PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask)),
                "Independent arithmetic fixture checks exact numeric result; no external operations", "reviewed")]);
        var receipt = new PlanningApprovalReview("concrete-review", "delegated_test", "codex", PlanningGraphCompiler.Fingerprint(
            JsonSerializer.Serialize(authorization, DelegatedTestJsonContext.Default.DelegatedTestAuthorization)), EvidenceHash(evidence),
            "benchmark", "test-run", state.Revision, state.ComputeArtifactHash()!, [requirement.Id], DateTimeOffset.UtcNow);
        return (state, runtime, authorization, evidence, receipt);
    }
}
