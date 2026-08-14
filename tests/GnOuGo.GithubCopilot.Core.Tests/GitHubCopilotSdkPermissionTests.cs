using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Text.Json;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class GitHubCopilotSdkPermissionTests
{
    [Theory]
    [InlineData(CopilotPermissionGrantScope.CurrentTask, "\"current_task\"")]
    [InlineData(CopilotPermissionGrantScope.WorkflowRun, "\"workflow_run\"")]
    [InlineData(CopilotPermissionGrantScope.FutureAgentRuns, "\"future_agent_runs\"")]
    public void GrantScope_UsesStableSnakeCaseWireValues(CopilotPermissionGrantScope scope, string expected)
        => Assert.Equal(expected, JsonSerializer.Serialize(scope));

    [Fact]
    public void InteractiveResponses_MapAllowOnceRefuseAndMatchingSessionDecision()
    {
        var sessionDecision = new PermissionDecisionApproveForSession
        {
            Approval = new PermissionDecisionApproveForSessionApprovalRead()
        };

        var allowOnce = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionAllowOnceChoice),
            sessionDecision);
        var refuse = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionRefuseChoice),
            sessionDecision);
        var allowForSession = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionAllowForSessionChoice),
            sessionDecision);
        var unsupportedSessionChoice = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionAllowForSessionChoice),
            null);

        Assert.IsType<PermissionDecisionApproveOnce>(allowOnce);
        Assert.IsType<PermissionDecisionReject>(refuse);
        Assert.Same(sessionDecision, allowForSession);
        Assert.IsType<PermissionDecisionReject>(unsupportedSessionChoice);
    }

    [Fact]
    public void ShellApproval_IsScopedToDetectedCommandIdentifiers()
    {
        var request = new PermissionRequestShell
        {
            CanOfferSessionApproval = true,
            FullCommandText = "dotnet test && npm test",
            HasWriteFileRedirection = false,
            Intention = "Run tests",
            PossiblePaths = [],
            PossibleUrls = [],
            Commands =
            [
                new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false },
                new PermissionRequestShellCommand { Identifier = "npm", ReadOnly = false },
                new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false }
            ]
        };

        var supported = GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope);

        Assert.True(supported);
        var forSession = Assert.IsType<PermissionDecisionApproveForSession>(decision);
        var commands = Assert.IsType<PermissionDecisionApproveForSessionApprovalCommands>(forSession.Approval);
        Assert.Equal(["dotnet", "npm"], commands.CommandIdentifiers);
        Assert.Contains("dotnet, npm", scope);
    }

    [Fact]
    public void ShellApproval_IsHiddenWhenSdkDisallowsIt()
    {
        var request = new PermissionRequestShell
        {
            CanOfferSessionApproval = false,
            FullCommandText = "dotnet test",
            HasWriteFileRedirection = false,
            Intention = "Run tests",
            PossiblePaths = [],
            PossibleUrls = [],
            Commands = [new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false }]
        };

        Assert.False(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope));
        Assert.Null(decision);
        Assert.Null(scope);
    }

    [Fact]
    public void McpApproval_IsScopedToExactServerAndTool()
    {
        var request = new PermissionRequestMcp
        {
            ServerName = "github",
            ToolName = "pull_request_read",
            ToolTitle = "Read pull request",
            ReadOnly = true
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out _));
        var forSession = Assert.IsType<PermissionDecisionApproveForSession>(decision);
        var approval = Assert.IsType<PermissionDecisionApproveForSessionApprovalMcp>(forSession.Approval);
        Assert.Equal("github", approval.ServerName);
        Assert.Equal("pull_request_read", approval.ToolName);
    }

    [Fact]
    public void FilesystemApprovals_UseOnlySupportedReadOrWriteCategories()
    {
        var read = new PermissionRequestRead
        {
            Path = "src/App.cs",
            Intention = "Inspect source"
        };
        var write = new PermissionRequestWrite
        {
            CanOfferSessionApproval = true,
            Diff = "@@ -1 +1 @@",
            FileName = "src/App.cs",
            Intention = "Apply a fix"
        };
        var unsafeWrite = new PermissionRequestWrite
        {
            CanOfferSessionApproval = false,
            Diff = "@@ -1 +1 @@",
            FileName = "src/App.cs",
            Intention = "Apply a fix"
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(read, out var readDecision, out _));
        Assert.IsType<PermissionDecisionApproveForSessionApprovalRead>(
            Assert.IsType<PermissionDecisionApproveForSession>(readDecision).Approval);
        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(write, out var writeDecision, out _));
        Assert.IsType<PermissionDecisionApproveForSessionApprovalWrite>(
            Assert.IsType<PermissionDecisionApproveForSession>(writeDecision).Approval);
        Assert.False(GitHubCopilotSdkClient.TryBuildSessionApproval(unsafeWrite, out _, out _));
    }

    [Fact]
    public void CustomToolApproval_IsScopedToExactToolName()
    {
        var request = new PermissionRequestCustomTool
        {
            ToolName = "run_repository_check",
            ToolDescription = "Runs a repository check"
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out _));
        var approval = Assert.IsType<PermissionDecisionApproveForSessionApprovalCustomTool>(
            Assert.IsType<PermissionDecisionApproveForSession>(decision).Approval);
        Assert.Equal("run_repository_check", approval.ToolName);
    }

    [Fact]
    public void UrlApproval_IsScopedToDomain()
    {
        var request = new PermissionRequestUrl
        {
            Url = "https://packages.example.test/v1/index.json",
            Intention = "Restore packages"
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope));
        var forSession = Assert.IsType<PermissionDecisionApproveForSession>(decision);
        Assert.Null(forSession.Approval);
        Assert.Equal("packages.example.test", forSession.Domain);
        Assert.Contains("packages.example.test", scope);
    }

    [Fact]
    public void UrlApproval_IsHiddenForInvalidOrNonHttpUrl()
    {
        var request = new PermissionRequestUrl
        {
            Url = "file:///tmp/secret",
            Intention = "Read a local URL"
        };

        Assert.False(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope));
        Assert.Null(decision);
        Assert.Null(scope);
    }

    [Fact]
    public void Description_IncludesCommandWarningAndSandboxBypassReason()
    {
        var request = new PermissionRequestShell
        {
            FullCommandText = "dotnet test",
            Intention = "Run the test suite",
            CanOfferSessionApproval = true,
            Commands = [new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false }],
            HasWriteFileRedirection = false,
            PossiblePaths = [],
            PossibleUrls = [],
            Warning = "This command may restore packages.",
            RequestSandboxBypass = true,
            RequestSandboxBypassReason = "Needs package cache access"
        };

        var details = GitHubCopilotSdkClient.DescribePermission(request);

        Assert.Contains("dotnet test", details);
        Assert.Contains("This command may restore packages.", details);
        Assert.Contains("Needs package cache access", details);
        Assert.Contains("Sandbox bypass requested: yes", details);
    }

    [Fact]
    public void Description_ExplicitlyReportsWhenSandboxBypassIsNotRequested()
    {
        var request = new PermissionRequestRead
        {
            Path = "README.md",
            Intention = "Inspect documentation",
            RequestSandboxBypass = false
        };

        var details = GitHubCopilotSdkClient.DescribePermission(request);

        Assert.Contains("Read path: README.md", details);
        Assert.Contains("Sandbox bypass requested: no", details);
    }

    [Fact]
    public async Task CurrentTaskGrant_AutoApprovesRemainingOperationsAndEmitsActivity()
    {
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowAllTaskChoice);
        var events = new RecordingPermissionEventSink();
        var source = CreateSource(human, new MemoryPermissionGrantStore(), events);
        var state = new GitHubCopilotSdkClient.InteractivePermissionTaskState();

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(Shell("dotnet restore"), source, state));
        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(Shell("dotnet test"), source, state));

        Assert.Single(human.Requests);
        Assert.Contains(GitHubCopilotSdkClient.PermissionAllowAllTaskChoice, human.Requests[0].Choices);
        Assert.Contains(events.Events, item => item.Kind == "permission.auto_approved" && item.Scope == CopilotPermissionGrantScope.CurrentTask);
    }

    [Fact]
    public async Task WorkflowGrant_AutoApprovesAnotherInteractiveTaskInTheSameExecution()
    {
        var store = new MemoryPermissionGrantStore();
        var firstHuman = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowAllWorkflowChoice);
        var first = CreateSource(firstHuman, store, new RecordingPermissionEventSink());

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet restore"), first, new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        var secondHuman = new QueueHumanInputProvider();
        var secondEvents = new RecordingPermissionEventSink();
        var second = CreateSource(secondHuman, store, secondEvents);
        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet test"), second, new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Empty(secondHuman.Requests);
        Assert.Contains(secondEvents.Events, item => item.Kind == "permission.auto_approved" && item.Scope == CopilotPermissionGrantScope.WorkflowRun);
    }

    [Fact]
    public async Task SandboxBypassGate_OffersThreeExplicitScopesWhenStableIdentitiesAreAvailable()
    {
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionRefuseChoice);
        var source = CreateSource(
            human,
            new MemoryPermissionGrantStore(),
            new RecordingPermissionEventSink(),
            enableSandboxBypassGrants: true);
        var request = Shell("dotnet test");
        request.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionReject>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            request,
            source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Equal(
            [
                GitHubCopilotSdkClient.PermissionAllowOnceChoice,
                GitHubCopilotSdkClient.PermissionAllowAllTaskSandboxBypassChoice,
                GitHubCopilotSdkClient.PermissionAllowAllWorkflowSandboxBypassChoice,
                GitHubCopilotSdkClient.PermissionAllowAllFutureAgentSandboxBypassChoice,
                GitHubCopilotSdkClient.PermissionRefuseChoice
            ],
            human.Requests[0].Choices);
    }

    [Fact]
    public async Task CurrentTaskSandboxBypassGrant_AutoApprovesNormalAndBypassOperations()
    {
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowAllTaskSandboxBypassChoice);
        var events = new RecordingPermissionEventSink();
        var source = CreateSource(
            human,
            new MemoryPermissionGrantStore(),
            events,
            enableSandboxBypassGrants: true);
        var state = new GitHubCopilotSdkClient.InteractivePermissionTaskState();
        var first = Shell("dotnet restore");
        first.RequestSandboxBypass = true;
        var second = Shell("dotnet test");
        second.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(first, source, state));
        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(second, source, state));
        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(Shell("dotnet build"), source, state));

        Assert.Single(human.Requests);
        Assert.Contains(events.Events, item => item.Kind == "permission.auto_approved"
                                               && item.Scope == CopilotPermissionGrantScope.CurrentTask
                                               && item.SandboxBypass);
    }

    [Fact]
    public async Task WorkflowSandboxBypassGrant_AutoApprovesAnotherInteractiveTask()
    {
        var store = new MemoryPermissionGrantStore();
        var firstHuman = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowAllWorkflowSandboxBypassChoice);
        var first = CreateSource(
            firstHuman,
            store,
            new RecordingPermissionEventSink(),
            enableSandboxBypassGrants: true);
        var firstRequest = Shell("dotnet restore");
        firstRequest.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            firstRequest,
            first,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        var secondHuman = new QueueHumanInputProvider();
        var secondEvents = new RecordingPermissionEventSink();
        var second = CreateSource(
            secondHuman,
            store,
            secondEvents,
            enableSandboxBypassGrants: true);
        var secondRequest = Shell("dotnet test");
        secondRequest.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            secondRequest,
            second,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));
        Assert.Empty(secondHuman.Requests);
        Assert.True(store.Reusable?.AllowSandboxBypass);
        Assert.Contains(secondEvents.Events, item => item.Kind == "permission.auto_approved"
                                                    && item.Scope == CopilotPermissionGrantScope.WorkflowRun
                                                    && item.SandboxBypass);
    }

    [Fact]
    public async Task FutureAgentGrant_RequiresSecondConfirmation()
    {
        var store = new MemoryPermissionGrantStore();
        var human = new QueueHumanInputProvider(
            GitHubCopilotSdkClient.PermissionAllowAllFutureAgentChoice,
            GitHubCopilotSdkClient.PermissionConfirmFutureAgentChoice);
        var source = CreateSource(human, store, new RecordingPermissionEventSink());

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet test"), source, new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Equal(2, human.Requests.Count);
        Assert.Equal("permission_persistence_confirmation", human.Requests[1].Kind);
        Assert.NotNull(store.Reusable);
        Assert.Equal(CopilotPermissionGrantScope.FutureAgentRuns, store.Reusable!.Scope);
    }

    [Fact]
    public async Task FutureAgentSandboxBypassGrant_RequiresExplicitSecondConfirmation()
    {
        var store = new MemoryPermissionGrantStore();
        var human = new QueueHumanInputProvider(
            GitHubCopilotSdkClient.PermissionAllowAllFutureAgentSandboxBypassChoice,
            GitHubCopilotSdkClient.PermissionConfirmFutureAgentSandboxBypassChoice);
        var source = CreateSource(
            human,
            store,
            new RecordingPermissionEventSink(),
            enableSandboxBypassGrants: true);
        var request = Shell("dotnet test");
        request.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            request,
            source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Equal(2, human.Requests.Count);
        Assert.Equal("permission_persistence_confirmation", human.Requests[1].Kind);
        Assert.Contains("survives restarts", human.Requests[1].Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tenant: tenant-a", human.Requests[1].Details, StringComparison.Ordinal);
        Assert.Contains("Agent: Reviewer", human.Requests[1].Details, StringComparison.Ordinal);
        Assert.Contains("high risk", human.Requests[1].Details, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.Reusable?.AllowSandboxBypass);
        Assert.Equal(CopilotPermissionGrantScope.FutureAgentRuns, store.Reusable?.Scope);
    }

    [Fact]
    public async Task FutureAgentSandboxBypassGrant_CancelledConfirmationRejectsWithoutPersisting()
    {
        var store = new MemoryPermissionGrantStore();
        var human = new QueueHumanInputProvider(
            GitHubCopilotSdkClient.PermissionAllowAllFutureAgentSandboxBypassChoice,
            GitHubCopilotSdkClient.PermissionCancelFutureAgentChoice);
        var source = CreateSource(
            human,
            store,
            new RecordingPermissionEventSink(),
            enableSandboxBypassGrants: true);
        var request = Shell("dotnet test");
        request.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionReject>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            request,
            source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Null(store.Reusable);
    }

    [Fact]
    public async Task FutureAgentGrant_SecondConfirmationRefusalRejectsOperationWithoutPersisting()
    {
        var store = new MemoryPermissionGrantStore();
        var human = new QueueHumanInputProvider(
            GitHubCopilotSdkClient.PermissionAllowAllFutureAgentChoice,
            GitHubCopilotSdkClient.PermissionCancelFutureAgentChoice);
        var events = new RecordingPermissionEventSink();
        var source = CreateSource(human, store, events);

        Assert.IsType<PermissionDecisionReject>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet test"), source, new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Null(store.Reusable);
        Assert.Contains(events.Events, item => item.Kind == "permission.refused" && item.Scope == CopilotPermissionGrantScope.FutureAgentRuns);
    }

    [Fact]
    public async Task BroadChoices_RequireTheirStableRunAndAgentIdentities()
    {
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionRefuseChoice);
        var source = CreateSource(
            human,
            new MemoryPermissionGrantStore(),
            new RecordingPermissionEventSink(),
            executionId: null,
            agentId: null);

        await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet test"), source, new GitHubCopilotSdkClient.InteractivePermissionTaskState());

        Assert.Contains(GitHubCopilotSdkClient.PermissionAllowAllTaskChoice, human.Requests[0].Choices);
        Assert.DoesNotContain(GitHubCopilotSdkClient.PermissionAllowAllWorkflowChoice, human.Requests[0].Choices);
        Assert.DoesNotContain(GitHubCopilotSdkClient.PermissionAllowAllFutureAgentChoice, human.Requests[0].Choices);
    }

    [Fact]
    public async Task SandboxBypass_OrdinaryGrantsDoNotAutoApproveWhenBypassGateIsDisabled()
    {
        var store = new MemoryPermissionGrantStore
        {
            Reusable = Grant(CopilotPermissionGrantScope.FutureAgentRuns)
        };
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowOnceChoice);
        var source = CreateSource(human, store, new RecordingPermissionEventSink());
        var state = new GitHubCopilotSdkClient.InteractivePermissionTaskState { AllowAll = true };
        var request = Shell("dotnet test");
        request.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(request, source, state));

        Assert.Equal(
            [GitHubCopilotSdkClient.PermissionAllowOnceChoice, GitHubCopilotSdkClient.PermissionRefuseChoice],
            human.Requests[0].Choices);
        Assert.Equal(0, store.FindCount);
    }

    [Fact]
    public async Task BroadChoices_RemainHiddenWhenHostGateIsDisabled()
    {
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionRefuseChoice);
        var source = CreateSource(human, new MemoryPermissionGrantStore(), new RecordingPermissionEventSink(), enableApproveAll: false);

        Assert.IsType<PermissionDecisionReject>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet test"), source, new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.DoesNotContain(human.Requests[0].Choices, choice => choice.StartsWith("Allow all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReusableGrant_IsIgnoredWhenBroadApprovalGateIsDisabled()
    {
        var store = new MemoryPermissionGrantStore
        {
            Reusable = Grant(CopilotPermissionGrantScope.FutureAgentRuns)
        };
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowOnceChoice);
        var source = CreateSource(
            human,
            store,
            new RecordingPermissionEventSink(),
            enableApproveAll: false);

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("dotnet test"),
            source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Single(human.Requests);
        Assert.Equal(0, store.FindCount);
    }

    [Fact]
    public async Task SandboxBypassGrant_IsIgnoredWhenBypassGrantGateIsDisabled()
    {
        var store = new MemoryPermissionGrantStore
        {
            Reusable = Grant(CopilotPermissionGrantScope.FutureAgentRuns, allowSandboxBypass: true)
        };
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowOnceChoice);
        var source = CreateSource(human, store, new RecordingPermissionEventSink());
        var request = Shell("dotnet test");
        request.RequestSandboxBypass = true;

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            request,
            source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState()));

        Assert.Single(human.Requests);
        Assert.Equal(0, store.SandboxBypassFindCount);
    }

    [Fact]
    public async Task PermissionActivity_RedactsLikelyCredentialsFromCommandSummary()
    {
        var human = new QueueHumanInputProvider(GitHubCopilotSdkClient.PermissionAllowOnceChoice);
        var events = new RecordingPermissionEventSink();
        var source = CreateSource(human, new MemoryPermissionGrantStore(), events);

        await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("tool --token=super-secret-value"),
            source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState());

        Assert.DoesNotContain(events.Events, item => item.Message.Contains("super-secret-value", StringComparison.Ordinal));
        Assert.Contains(events.Events, item => item.Message.Contains("token=<redacted>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AutomaticPermissionActivity_RedactsLikelyCredentialsWithoutRequestingHumanInput()
    {
        var human = new QueueHumanInputProvider();
        var events = new RecordingPermissionEventSink();
        var source = CreateSource(human, new MemoryPermissionGrantStore(), events);
        var state = new GitHubCopilotSdkClient.InteractivePermissionTaskState { AllowAll = true };

        Assert.IsType<PermissionDecisionApproveOnce>(await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(
            Shell("tool --token=super-secret-value"),
            source,
            state));

        Assert.Empty(human.Requests);
        var automatic = Assert.Single(events.Events, item => item.Kind == "permission.auto_approved");
        Assert.DoesNotContain("super-secret-value", automatic.Message, StringComparison.Ordinal);
        Assert.Contains("token=<redacted>", automatic.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CopilotSdkSessionConfiguration CreateSource(
        ICopilotHumanInputProvider human,
        ICopilotPermissionGrantStore store,
        ICopilotPermissionEventSink sink,
        bool enableApproveAll = true,
        string? executionId = "execution-a",
        string? agentId = "agent-a",
        bool enableSandboxBypassGrants = false)
    {
        var context = new CopilotRequestContext(
            "tenant-a",
            RunId: "run-child",
            StepId: "copilot",
            ExecutionId: executionId,
            AgentId: agentId,
            AgentName: "Reviewer");
        return new CopilotSdkSessionConfiguration(
            new CopilotSessionCreateRequest(
                context,
                new CopilotRuntimeConfiguration(Path.GetTempPath(), "model", EnableApproveAll: enableApproveAll)
                {
                    EnableSandboxBypassGrants = enableSandboxBypassGrants
                },
                CopilotSessionKind.Managed,
                CopilotPermissionMode.Interactive),
            null,
            human,
            store,
            sink);
    }

    private static PermissionRequestShell Shell(string command)
        => new()
        {
            FullCommandText = command,
            Intention = "Run repository checks",
            CanOfferSessionApproval = false,
            Commands = [new PermissionRequestShellCommand { Identifier = command.Split(' ')[0], ReadOnly = false }],
            HasWriteFileRedirection = false,
            PossiblePaths = [],
            PossibleUrls = [],
            RequestSandboxBypass = false
        };

    private static CopilotPermissionGrant Grant(CopilotPermissionGrantScope scope, bool allowSandboxBypass = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new CopilotPermissionGrant("grant", "tenant-a", scope, "execution-a", "agent-a", "Reviewer", now, now, AllowSandboxBypass: allowSandboxBypass);
    }

    private sealed class QueueHumanInputProvider(params string[] answers) : ICopilotHumanInputProvider
    {
        private readonly Queue<string> _answers = new(answers);
        public List<CopilotHumanInputRequest> Requests { get; } = [];

        public Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var answer = _answers.Count > 0 ? _answers.Dequeue() : GitHubCopilotSdkClient.PermissionRefuseChoice;
            return Task.FromResult(new CopilotHumanInputResponse(true, answer));
        }
    }

    private sealed class RecordingPermissionEventSink : ICopilotPermissionEventSink
    {
        public List<CopilotPermissionEvent> Events { get; } = [];
        public void Report(CopilotPermissionEvent permissionEvent) => Events.Add(permissionEvent);
    }

    private sealed class MemoryPermissionGrantStore :
        ICopilotPermissionGrantStore,
        ICopilotSandboxBypassPermissionGrantStore
    {
        public CopilotPermissionGrant? Reusable { get; set; }
        public int FindCount { get; private set; }
        public int SandboxBypassFindCount { get; private set; }

        public Task<CopilotPermissionGrant?> FindReusableGrantAsync(CopilotRequestContext context, CancellationToken cancellationToken)
        {
            FindCount++;
            return Task.FromResult(Reusable);
        }

        public Task<CopilotPermissionGrant> GrantWorkflowRunAsync(CopilotRequestContext context, CancellationToken cancellationToken)
        {
            Reusable = Grant(CopilotPermissionGrantScope.WorkflowRun);
            return Task.FromResult(Reusable);
        }

        public Task<CopilotPermissionGrant> GrantFutureAgentRunsAsync(CopilotRequestContext context, CancellationToken cancellationToken)
        {
            Reusable = Grant(CopilotPermissionGrantScope.FutureAgentRuns);
            return Task.FromResult(Reusable);
        }

        public Task<CopilotPermissionGrant?> FindReusableSandboxBypassGrantAsync(CopilotRequestContext context, CancellationToken cancellationToken)
        {
            SandboxBypassFindCount++;
            return Task.FromResult(Reusable?.AllowSandboxBypass == true ? Reusable : null);
        }

        public Task<CopilotPermissionGrant> GrantWorkflowRunWithSandboxBypassAsync(CopilotRequestContext context, CancellationToken cancellationToken)
        {
            Reusable = Grant(CopilotPermissionGrantScope.WorkflowRun, allowSandboxBypass: true);
            return Task.FromResult(Reusable);
        }

        public Task<CopilotPermissionGrant> GrantFutureAgentRunsWithSandboxBypassAsync(CopilotRequestContext context, CancellationToken cancellationToken)
        {
            Reusable = Grant(CopilotPermissionGrantScope.FutureAgentRuns, allowSandboxBypass: true);
            return Task.FromResult(Reusable);
        }

        public Task<IReadOnlyList<CopilotPermissionGrant>> ListFutureAgentGrantsAsync(string tenantId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CopilotPermissionGrant>>(Reusable is null ? [] : [Reusable]);

        public Task<bool> RevokeAsync(string tenantId, string grantId, CancellationToken cancellationToken)
        {
            var removed = Reusable?.Id == grantId;
            if (removed) Reusable = null;
            return Task.FromResult(removed);
        }

        public Task<int> RevokeAgentAsync(string tenantId, string agentId, CancellationToken cancellationToken)
        {
            var removed = Reusable?.AgentId == agentId ? 1 : 0;
            if (removed == 1) Reusable = null;
            return Task.FromResult(removed);
        }
    }
}
