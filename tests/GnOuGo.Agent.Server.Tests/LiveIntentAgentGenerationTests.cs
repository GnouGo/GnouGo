using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Mcp.Core;
using GnOuGo.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveIntentAgentGenerationTests
{
    private const string EnableVariable = "GNOU_GO_LIVE_INTENT_AGENT_E2E";
    private const string ProgressPathVariable = "GNOU_GO_LIVE_INTENT_AGENT_PROGRESS_PATH";
    private static readonly object ProgressFileLock = new();
    private const string AcceptancePrompt = """
        Create a reusable agent that accepts a GitHub pull-request URL and review instructions.

        For the supplied pull request:
        1. Clone the project once.
        2. Ask Copilot to install or restore dependencies for every modified project.
        3. Ask Copilot to run all relevant unit tests and linters.
        4. Ask Copilot to review all changed code and publish only high-confidence findings as inline GitHub pull-request review comments.
        5. APPROVE only when dependency restoration, tests, lint, and complete changed-code coverage succeed without findings; otherwise submit REQUEST_CHANGES. Include a functional-then-technical summary with dependency, test, lint, coverage, and findings status.

        Whatever happens, delete every directory created by the workflow at the end.
        """;

    [Fact]
    [Trait("Category", "Live")]
    public async Task SimpleIntent_GeneratesThreeValidatedAgentsUsingLiveConfiguration()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            return;

        WriteLiveProgress("test_started");
        var sourceRoot = FindSourceRoot();
        var previousDirectory = Directory.GetCurrentDirectory();
        var workspaceRoot = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();
        var workflowWorkspacesRoot = GnOuGoWorkspace.ResolveWorkflowWorkspacesDirectory(workspaceRoot);
        var existingWorkflowWorkspaces = SnapshotWorkflowWorkspaces(workflowWorkspacesRoot);
        var generatedAgents = new List<(string Id, string Name)>();
        AgentUserConfigSnapshot? previousConfig = null;
        WebApplication? app = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        try
        {
            Directory.SetCurrentDirectory(sourceRoot);
            app = GnOuGoAgentWebHost.Build(
                ["--OtlpCollector:Enabled=false", "--OpenTelemetry:Enabled=false"],
                urls: "http://127.0.0.1:0",
                contentRoot: Path.Combine(sourceRoot, "src", "GnOuGo.Agent.Server"),
                enableHttpsRedirection: false);
            await app.StartAsync(timeout.Token);
            WriteLiveProgress("host_started");

            var services = app.Services;
            var configureAgents = services.GetRequiredService<ConfigureAgentsService>();
            var humanInput = services.GetRequiredService<AgentHumanInputProvider>();
            var userConfig = services.GetRequiredService<AgentUserConfigMcpClient>();
            var mcpFactory = services.GetRequiredService<IMcpClientFactory>();
            previousConfig = await userConfig.GetAsync(timeout.Token);
            await AssertLiveReviewCompositionContractAsync(services, timeout.Token);
            WriteLiveProgress("composition_contract_validated");

            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            (string Name, GeneratedAgentContract Contract)? publicationAgent = null;
            var generationCount = int.TryParse(
                Environment.GetEnvironmentVariable("GNOU_GO_LIVE_INTENT_AGENT_GENERATIONS"),
                out var configuredCount)
                ? Math.Clamp(configuredCount, 1, 3)
                : 3;
            for (var attempt = 1; attempt <= generationCount; attempt++)
            {
                var name = $"e2e-intent-pr-review-{runId}-{attempt}";
                WriteLiveProgress("generation_started", generation: attempt);
                List<SmartFlowEvent>? events = null;
                for (var providerAttempt = 1; providerAttempt <= 2; providerAttempt++)
                {
                    WriteLiveProgress("provider_attempt_started", generation: attempt, providerAttempt: providerAttempt);
                    using var responderCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var responder = RespondToAgentCreationAsync(humanInput, name, responderCancellation.Token);
                    events = await CollectAsync(
                        configureAgents.ExecuteAsync("/gnougo add", timeout.Token),
                        timeout.Token,
                        item => WriteLiveProgress(
                            "generation_event",
                            generation: attempt,
                            providerAttempt: providerAttempt,
                            flowEvent: item));
                    WriteLiveProgress("provider_attempt_completed", generation: attempt, providerAttempt: providerAttempt);
                    responderCancellation.Cancel();
                    try
                    {
                        await responder;
                    }
                    catch (OperationCanceledException) when (responderCancellation.IsCancellationRequested)
                    {
                        // The command can fail before requesting or completing all interactive forms.
                    }
                    var providerFailure = events.FirstOrDefault(static item => item.Type == "error");
                    if (providerAttempt < 2 && providerFailure?.Retryable == true)
                        continue;
                    break;
                }

                Assert.NotNull(events);
                var failure = events!.FirstOrDefault(static item => item.Type == "error");
                var answerFailure = events.FirstOrDefault(static item => item.Type == "answer"
                    && item.Text?.StartsWith("❌", StringComparison.Ordinal) == true);
                Assert.True(failure is null && answerFailure is null, failure?.Text ?? answerFailure?.Text);
                var agent = await GetAgentAsync(mcpFactory, name, timeout.Token);
                generatedAgents.Add((RequireString(agent, "id"), name));
                var workflow = RequireString(agent, "workflow");
                Assert.Equal(
                    AcceptancePrompt.Trim().ReplaceLineEndings("\n"),
                    RequireString(agent, "original_prompt").Trim().ReplaceLineEndings("\n"));
                var contract = await ValidateGeneratedAgentAsync(workflow, mcpFactory, timeout.Token);
                WriteLiveProgress("generation_validated", generation: attempt);
                if (attempt == 1)
                {
                    await ExecuteReadOnlyAcceptanceAsync(
                        services,
                        humanInput,
                        name,
                        contract,
                        timeout.Token);
                    publicationAgent = (name, contract);
                }
            }

            Assert.NotNull(publicationAgent);
            await ExecutePublicationAcceptanceAsync(
                services,
                humanInput,
                publicationAgent.Value.Name,
                publicationAgent.Value.Contract,
                sourceRoot,
                timeout.Token);
            WriteLiveProgress("publication_acceptance_completed");
        }
        finally
        {
            WriteLiveProgress("cleanup_started");
            if (app is not null)
            {
                var services = app.Services;
                var mcpFactory = services.GetService<IMcpClientFactory>();
                if (mcpFactory is not null)
                {
                    foreach (var agent in generatedAgents)
                    {
                        try
                        {
                            await CallAgentToolAsync(mcpFactory, "agent_delete", new JsonObject { ["id"] = agent.Id }, CancellationToken.None);
                        }
                        catch
                        {
                            // Cleanup is best effort; the assertion failure remains primary.
                        }
                    }
                }

                var userConfig = services.GetService<AgentUserConfigMcpClient>();
                if (userConfig is not null && previousConfig is not null)
                {
                    if (string.IsNullOrWhiteSpace(previousConfig.DefaultAgent))
                        await userConfig.SetAsync(clearDefaultAgent: true, ct: CancellationToken.None);
                    else
                        await userConfig.SetAsync(defaultAgent: previousConfig.DefaultAgent, ct: CancellationToken.None);
                }
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
            Directory.SetCurrentDirectory(previousDirectory);
            DeleteNewWorkflowWorkspaces(workflowWorkspacesRoot, existingWorkflowWorkspaces);
            WriteLiveProgress("cleanup_completed");
        }
    }

    private static async Task AssertLiveReviewCompositionContractAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        var runtimeFactory = services.GetRequiredService<SecureWorkflowRuntimeFactory>();
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var server = Assert.Single(runtime.McpClientFactory.ServerMetadata!, static metadata =>
            metadata.Name.Contains("GithubCopilot", StringComparison.OrdinalIgnoreCase));
        await using var client = await runtime.McpClientFactory.GetClientAsync(server.Name, ct);
        var review = Assert.Single(await client.ListToolsAsync(ct), static tool =>
            string.Equals(tool.Name, "copilot_review", StringComparison.Ordinal));
        var transportContract = McpCapabilityCompositionParser.ParseAndValidate(review.Meta);
        Assert.True(transportContract.IsDeclared, $"copilot_review metadata did not declare composition: {review.Meta?.ToJsonString() ?? "null"}");
        Assert.True(transportContract.IsValid, string.Join("; ", transportContract.Errors));
        Assert.True(review.CompositionContract is not null,
            $"Configured adapter dropped valid composition metadata: {review.Meta?.ToJsonString() ?? "null"}");
        Assert.Empty(review.CompositionContract.Errors);
        Assert.Equal(
            McpCapabilityCompositionConventions.CompleteOperationKind,
            review.CompositionContract.Contract?.Kind);
    }

    private static async Task RespondToAgentCreationAsync(
        AgentHumanInputProvider humanInput,
        string agentName,
        CancellationToken ct)
    {
        await foreach (var request in humanInput.PendingRequests.ReadAllAsync(ct))
        {
            JsonNode response = request.StepId.EndsWith("input_name", StringComparison.Ordinal)
                ? new JsonObject
                {
                    ["agent_name"] = agentName,
                    [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
                }
                : request.StepId.EndsWith("input_prompt", StringComparison.Ordinal)
                    ? new JsonObject
                    {
                        ["description"] = AcceptancePrompt,
                        [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
                    }
                    : request.StepId.Contains(":intent_clarification:", StringComparison.Ordinal)
                        ? BuildIntentClarificationResponse(request)
                        : request.StepId.Contains(":capability_clarification:", StringComparison.Ordinal)
                        ? BuildCapabilityClarificationResponse(request)
                    : request.StepId.EndsWith("review_workflow", StringComparison.Ordinal)
                        ? new JsonObject
                        {
                            ["response"] = "approve",
                            [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
                        }
                        : throw new InvalidOperationException($"Unexpected agent-generation human input step '{request.StepId}'.");
            Assert.True(humanInput.TrySubmitResponse(request.RunId, request.StepId, response));
            if (request.StepId.EndsWith("review_workflow", StringComparison.Ordinal))
                return;
        }
    }

    private static JsonObject BuildIntentClarificationResponse(HumanInputRequest request)
    {
        const string completeIntent = "Treat environment preparation and check execution as an observable effect distinct from changed-code review. Perform the changed-code review as one complete action, without exposing its internal start, batch, or finish phases. Inputs are one pull-request URL and review instructions. Use one disposable checkout and never push changes. Return typed preparation, test, lint, coverage, findings, runtime APPROVE or REQUEST_CHANGES, and justification results. Publish only high-confidence findings with valid anchors after one human confirmation, submit one matching runtime decision, fail closed on unresolved evidence, and always clean workflow-created directories.";
        Assert.Equal(HumanInputContract.ModeForm, request.Mode);
        Assert.True(request.AllowAbandon);
        Assert.InRange(request.Fields?.Count ?? 0, 1, 5);
        var response = new JsonObject
        {
            [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
        };
        var usedCustomAnswer = false;
        var usedRecommendedAnswer = false;
        var customFieldIndex = request.Fields!.Count > 1
            ? Enumerable.Range(0, request.Fields.Count)
            .Select(index => new
            {
                Index = index,
                Text = $"{request.Fields[index].Description} {request.Fields[index].Default}".ToLowerInvariant()
            })
            .OrderByDescending(static candidate =>
                (candidate.Text.Contains("review", StringComparison.Ordinal) ? 8 : 0)
                + (candidate.Text.Contains("analysis", StringComparison.Ordinal) ? 7 : 0)
                + (candidate.Text.Contains("scope", StringComparison.Ordinal) ? 5 : 0)
                + (candidate.Text.Contains("operation", StringComparison.Ordinal) ? 4 : 0)
                + (candidate.Text.Contains("policy", StringComparison.Ordinal) ? 3 : 0))
            .ThenBy(static candidate => candidate.Index)
            .First().Index
            : -1;
        for (var fieldIndex = 0; fieldIndex < request.Fields!.Count; fieldIndex++)
        {
            var field = request.Fields[fieldIndex];
            Assert.True(field.Required);
            Assert.Equal("radio", field.Type);
            Assert.True(field.AllowCustomAnswer);
            Assert.InRange(field.Options?.Count ?? 0, 2, 3);
            Assert.Equal(field.Options![0], field.Default);
            Assert.Equal(field.Options.Count, field.OptionDefinitions?.Count);
            Assert.True(field.OptionDefinitions![0].Recommended);
            Assert.False(string.IsNullOrWhiteSpace(field.OptionDefinitions[0].Description));
            Assert.All(field.OptionDefinitions.Skip(1), static option => Assert.False(option.Recommended));

            if (fieldIndex == customFieldIndex)
            {
                response[field.Name] = completeIntent;
                usedCustomAnswer = true;
            }
            else
            {
                response[field.Name] = field.Default;
                usedRecommendedAnswer = true;
            }
        }
        if (request.Fields.Count > 1)
            Assert.True(usedCustomAnswer);
        Assert.True(usedRecommendedAnswer);
        return response;
    }

    private static JsonObject BuildCapabilityClarificationResponse(HumanInputRequest request)
    {
        Assert.Equal(HumanInputContract.ModeForm, request.Mode);
        Assert.NotEmpty(request.Fields!);
        var fields = request.Fields!;
        Assert.All(fields, static field => Assert.True(field.Required));
        Assert.Equal(fields.Count, fields.Select(static field => field.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(request.Context);
        var response = new JsonObject
        {
            [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
        };
        foreach (var field in fields)
        {
            response[field.Name] = field.Name switch
            {
                var name when name.StartsWith("unresolved_intent_", StringComparison.Ordinal)
                    => "The intended operation is the complete one-shot review; start/analyse/finish primitives are implementation phases, not separate requested effects.",
                var name when name.StartsWith("unresolved_choice_", StringComparison.Ordinal)
                    => "Use the complete one-shot review capability. Publication is runtime-dependent and must use the exact selector branch corresponding to the computed review result.",
                "intended_outcome_and_scope"
                    => "Review the disposable pull request completely, publish one explained review decision, and clean only resources created by this test.",
                "runtime_decision_rules"
                    => "Compute the decision from runtime dependency restoration, tests, lint, changed-code coverage, and findings; execute exactly one matching publication branch and never ask the human to predict that result.",
                "external_effect_boundaries"
                    => "Allow reads for the disposable pull request and confirmed writes only to that pull request. Reject every other target or write.",
                "success_criteria"
                    => "All changed code is covered, required checks are represented, one decision branch executes, and its body matches the generated explanation.",
                "failure_policy"
                    => "Fail closed and abandon generation when intent, capability support, decision provenance, or safe cleanup remains unresolved after clarification.",
                _ => throw new InvalidOperationException($"Unexpected capability clarification field '{field.Name}'.")
            };
        }

        return response;
    }

    private static async Task<JsonObject> GetAgentAsync(IMcpClientFactory factory, string name, CancellationToken ct)
    {
        var payload = await CallAgentToolAsync(factory, "agent_get_by_name", new JsonObject { ["name"] = name }, ct);
        Assert.True(payload["success"]?.GetValue<bool>(), payload.ToJsonString());
        return Assert.IsType<JsonObject>(payload["agent"]);
    }

    private static async Task<JsonObject> CallAgentToolAsync(
        IMcpClientFactory factory,
        string method,
        JsonNode? arguments,
        CancellationToken ct)
    {
        await using var session = await factory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
        var result = await session.CallToolAsync(method, arguments, ct);
        Assert.False(result.IsError);
        return Assert.IsType<JsonObject>(result.Content);
    }

    private static async Task<GeneratedAgentContract> ValidateGeneratedAgentAsync(
        string yaml,
        IMcpClientFactory factory,
        CancellationToken ct)
    {
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        Assert.NotNull(compiled.Entrypoint);
        var workflow = document.Workflows[compiled.Entrypoint!];
        var inputs = workflow.Inputs ?? document.Skill?.Inputs;
        Assert.NotNull(inputs);
        var pullRequestUrlInput = Assert.Single(
            inputs!.Keys,
            static name => name is "pr_url" or "pull_request_url" or "github_pull_request_url");
        Assert.Contains("review_instructions", inputs.Keys, StringComparer.Ordinal);

        var steps = EnumerateReachableSteps(document, compiled.Entrypoint!).ToArray();
        Assert.Single(steps, static step => step.Type == "human.input");
        var calls = steps.Where(static step => step.Type == "mcp.call").ToArray();
        Assert.NotEmpty(calls);
        var discovered = new Dictionary<string, (Dictionary<string, McpToolInfo> Tools, HashSet<string> Prompts)>(StringComparer.Ordinal);
        var resolvedToolCalls = new List<(StepDef Step, McpToolInfo Tool)>();
        foreach (var call in calls)
        {
            var server = call.Input?["server"]?.GetValue<string>();
            var kind = call.Input?["kind"]?.GetValue<string>() ?? "tool";
            var method = call.Input?["method"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(server));
            Assert.False(string.IsNullOrWhiteSpace(method));
            AssertStableCapabilityIdentifier(server!);
            AssertStableCapabilityIdentifier(method!);
            if (!discovered.TryGetValue(server!, out var catalog))
            {
                await using var session = await factory.GetClientAsync(server!, ct);
                catalog = (
                    (await session.ListToolsAsync(ct)).ToDictionary(static tool => tool.Name, StringComparer.Ordinal),
                    (await session.ListPromptsAsync(ct)).Select(static prompt => prompt.Name).ToHashSet(StringComparer.Ordinal));
                discovered[server!] = catalog;
            }
            Assert.True(kind == "prompt" ? catalog.Prompts.Contains(method!) : catalog.Tools.ContainsKey(method!),
                $"Generated call '{server}/{method}' was not present in the live discovered catalog.");
            if (kind != "prompt" && catalog.Tools.TryGetValue(method!, out var tool))
            {
                ValidateLiteralSelectors(tool.InputSchema, call.Input?["request"]);
                resolvedToolCalls.Add((call, tool));
            }
        }

        var workspaceMaterializers = resolvedToolCalls.Where(static call =>
        {
            var parsed = McpArtifactContractParser.ParseAndValidate(
                call.Tool.Meta,
                call.Tool.InputSchema,
                call.Tool.OutputSchema);
            return parsed.IsValid && parsed.Contract!.Produces.Any(static artifact =>
                artifact.Kind == McpArtifactContractMetadata.WorkspaceDirectoryKind
                && artifact.Mode == McpArtifactContractMetadata.MaterializeMode);
        }).ToArray();
        Assert.Single(workspaceMaterializers);

        var workspaceConsumers = resolvedToolCalls.Where(static call =>
        {
            var parsed = McpArtifactContractParser.ParseAndValidate(
                call.Tool.Meta,
                call.Tool.InputSchema,
                call.Tool.OutputSchema);
            return parsed.IsValid && parsed.Contract!.Consumes.Any(static artifact =>
                artifact.Kind == McpArtifactContractMetadata.WorkspaceDirectoryKind && artifact.Required);
        }).ToArray();
        Assert.NotEmpty(workspaceConsumers);
        Assert.All(workspaceConsumers, static call =>
        {
            var projectRoot = call.Step.Input?["request"]?["projectRoot"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(projectRoot));
            Assert.Contains("${", projectRoot, StringComparison.Ordinal);
        });

        var interactiveCopilotCalls = calls.Where(static call => string.Equals(
            call.Input?["method"]?.GetValue<string>(),
            "copilot_interactive_one_shot",
            StringComparison.Ordinal)).ToArray();
        Assert.True(interactiveCopilotCalls.Length >= 2);
        Assert.Contains(interactiveCopilotCalls, static call =>
            call.Input?["request"]?["prompt"]?.GetValue<string>()?.Contains("depend", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(interactiveCopilotCalls, static call =>
        {
            var prompt = call.Input?["request"]?["prompt"]?.GetValue<string>();
            return prompt?.Contains("test", StringComparison.OrdinalIgnoreCase) == true;
        });
        Assert.Contains(interactiveCopilotCalls, static call =>
        {
            var prompt = call.Input?["request"]?["prompt"]?.GetValue<string>();
            return prompt?.Contains("lint", StringComparison.OrdinalIgnoreCase) == true
                   || prompt?.Contains("format", StringComparison.OrdinalIgnoreCase) == true;
        });

        var reviewEvents = calls
            .Where(static call => string.Equals(call.Input?["method"]?.GetValue<string>(), "pull_request_review_write", StringComparison.Ordinal))
            .Select(static call => call.Input?["request"]?["event"]?.GetValue<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("APPROVE", reviewEvents);
        Assert.Contains("REQUEST_CHANGES", reviewEvents);

        var finallyCalls = document.Workflows.Values
            .SelectMany(static workflow => EnumerateSteps(workflow.Finally))
            .Where(static step => step.Type == "mcp.call")
            .ToArray();
        Assert.Contains(finallyCalls, static call =>
            string.Equals(call.Input?["method"]?.GetValue<string>(), "cmd_run", StringComparison.Ordinal)
            && string.Equals(call.Input?["request"]?["commandName"]?.GetValue<string>(), "delete_directory", StringComparison.Ordinal));

        var githubServers = calls
            .Where(static call => string.Equals(
                call.Input?["method"]?.GetValue<string>(),
                "pull_request_read",
                StringComparison.Ordinal))
            .Select(static call => call.Input!["server"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(githubServers);

        var gitServers = calls
            .Where(static call => string.Equals(
                call.Input?["method"]?.GetValue<string>(),
                "git_compare_refs",
                StringComparison.Ordinal))
            .Select(static call => call.Input!["server"]!.GetValue<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var gitServer = gitServers.Length switch
        {
            0 => null,
            1 => gitServers[0],
            _ => throw new InvalidOperationException("Generated workflow used more than one Git comparison server.")
        };

        return new GeneratedAgentContract(
            pullRequestUrlInput,
            steps.Any(static step => step.Type == "llm.call"),
            githubServers,
            gitServer);
    }

    private static void ValidateLiteralSelectors(JsonNode? schema, JsonNode? request, int depth = 0)
    {
        if (depth > 4 || schema is not JsonObject schemaObject || request is not JsonObject requestObject)
            return;

        if (schemaObject["properties"] is JsonObject properties)
        {
            var discriminatorProperty = (schemaObject["discriminator"] as JsonObject)?["propertyName"]?.GetValue<string>();
            foreach (var property in properties)
            {
                if (property.Value is not JsonObject propertySchema
                    || !requestObject.TryGetPropertyValue(property.Key, out var requestValue)
                    || requestValue is null)
                {
                    continue;
                }

                var isSelector = propertySchema.ContainsKey("const")
                                 || string.Equals(discriminatorProperty, property.Key, StringComparison.Ordinal)
                                 || IsActionSelectorProperty(property.Key);
                var allowed = ReadDocumentedScalarValues(propertySchema);
                if (allowed.Count > 0)
                {
                    Assert.IsAssignableFrom<JsonValue>(requestValue);
                    if (requestValue is JsonValue textValue
                        && textValue.TryGetValue<string>(out var textValueString))
                    {
                        if (isSelector)
                        {
                            Assert.DoesNotContain("${", textValueString, StringComparison.Ordinal);
                            AssertStableCapabilityIdentifier(textValueString);
                        }
                    }
                    Assert.Contains(allowed, candidate => JsonNode.DeepEquals(candidate, requestValue));
                }

                ValidateLiteralSelectors(propertySchema, requestValue, depth + 1);
            }
        }

        foreach (var branchName in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (schemaObject[branchName] is not JsonArray branches)
                continue;
            foreach (var branch in branches)
                ValidateLiteralSelectors(branch, request, depth + 1);
        }
    }

    private static bool IsActionSelectorProperty(string propertyName)
        => propertyName is "method" or "action" or "operation" or "command" or "mode" or "event" or "kind";

    private static void AssertStableCapabilityIdentifier(string value)
    {
        Assert.DoesNotContain("preview", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("experimental", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insiders", value, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<JsonNode> ReadDocumentedScalarValues(JsonObject schema)
    {
        if (schema["const"] is JsonValue constant)
            return [constant];
        if (schema["enum"] is not JsonArray values)
            return Array.Empty<JsonNode>();
        return values.Where(static value => value is JsonValue).Select(static value => value!).ToArray();
    }

    private static async Task ExecuteReadOnlyAcceptanceAsync(
        IServiceProvider services,
        AgentHumanInputProvider humanInput,
        string agentName,
        GeneratedAgentContract contract,
        CancellationToken ct)
    {
        var recordingFactory = new WriteDenyingMcpClientFactory(
            services.GetRequiredService<IMcpClientFactory>(),
            contract.GitHubServers);
        var runtimeFactory = new SecureWorkflowRuntimeFactory(
            services.GetRequiredService<LLMRuntimeOptionsStore>(),
            services.GetRequiredService<IKeyVaultRuntimeConfigStore>(),
            services.GetRequiredService<ILoggerFactory>(),
            mcpClientFactoryOverride: recordingFactory,
            backgroundModeCache: services.GetRequiredService<IMemoryCache>(),
            llmCapabilityResolver: services.GetService<ILLMCapabilityResolver>(),
            humanInputProvider: humanInput);
        var smartFlow = ActivatorUtilities.CreateInstance<SmartFlowService>(services, runtimeFactory);
        var inputs = new JsonObject
        {
            [contract.PullRequestUrlInput] = "https://github.com/AxaFrance/SmartGuide/pull/535",
            ["review_instructions"] = "Report only demonstrable correctness, security, error-handling, concurrency, null-handling, regression, and missing-test findings from changed code."
        };

        List<SmartFlowEvent>? events = null;
        for (var executionAttempt = 1; executionAttempt <= 2; executionAttempt++)
        {
            using var responderCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var responder = RejectPublicationAsync(humanInput, responderCancellation.Token);
            events = await CollectAsync(
                smartFlow.ExecuteAsync(
                    "Review the supplied pull request using the supplied review instructions.",
                    correlationId: $"live-intent-axa-{Guid.NewGuid():N}",
                    agentName,
                    filesIds: null,
                    workflowInputs: inputs,
                    ct),
                ct);
            responderCancellation.Cancel();
            try
            {
                await responder;
            }
            catch (OperationCanceledException) when (responderCancellation.IsCancellationRequested)
            {
                // The publication gate was not reached only when execution failed earlier.
            }

            var attemptFailure = events.FirstOrDefault(static item => item.Type == "error");
            if (executionAttempt < 2 && attemptFailure?.Retryable == true)
                continue;
            break;
        }

        Assert.NotNull(events);
        var failure = events!.FirstOrDefault(static item => item.Type == "error");
        Assert.Null(failure);
        Assert.Contains(events, static item => item.Type == "answer" && !string.IsNullOrWhiteSpace(item.Text));
        Assert.Equal(0, recordingFactory.DeniedWriteAttempts);
        Assert.DoesNotContain(recordingFactory.Calls, static call => call.IsGitHub && call.IsPotentialWrite);
        Assert.Contains(recordingFactory.Calls, static call => call.IsGitHub && !call.IsPotentialWrite);
        Assert.True(
            recordingFactory.Calls.Any(static call => call.Method.Contains("compare", StringComparison.OrdinalIgnoreCase))
            || recordingFactory.Calls.Any(static call => call.IsGitHub
                && (string.Equals((call.Arguments as JsonObject)?["method"]?.GetValue<string>(), "get_diff", StringComparison.Ordinal)
                    || string.Equals((call.Arguments as JsonObject)?["method"]?.GetValue<string>(), "get_files", StringComparison.Ordinal))),
            "The read-only execution did not obtain changed-code data from a discovered capability.");
        Assert.True(contract.HasNativeLlmCall
                    || recordingFactory.Calls.Any(static call => call.Server.Contains("copilot", StringComparison.OrdinalIgnoreCase)),
            "The read-only execution did not exercise a discovered AI-analysis capability.");
    }

    private static async Task ExecutePublicationAcceptanceAsync(
        IServiceProvider services,
        AgentHumanInputProvider humanInput,
        string agentName,
        GeneratedAgentContract contract,
        string sourceRoot,
        CancellationToken ct)
    {
        var factory = services.GetRequiredService<IMcpClientFactory>();
        var (owner, repository, remoteUrl) = await ReadOriginAsync(sourceRoot, ct);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var branchName = $"gnougo/e2e-intent-pr-review-{runId}";
        var fixtureRelativePath = $"e2e-fixtures/IntentPrReviewFixture-{runId}.cs";
        var fixtureCloneRelative = $"workflows/e2e/intent-pr-review-{runId}";
        var workspaceRoot = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();
        var workflowWorkspacesRoot = GnOuGoWorkspace.ResolveWorkflowWorkspacesDirectory(workspaceRoot);
        var existingWorkflowWorkspaces = SnapshotWorkflowWorkspaces(workflowWorkspacesRoot);
        string? fixtureProjectRoot = null;
        int? pullNumber = null;
        var branchPushed = false;

        var fixtureGitServer = await ResolveFixtureGitServerAsync(factory, contract.GitServer, ct);
        await using var git = await factory.GetClientAsync(fixtureGitServer, ct);
        var githubServer = Assert.Single(contract.GitHubServers);
        await using var github = await factory.GetClientAsync(githubServer, ct);
        try
        {
            var clone = await CallSessionToolAsync(git, "git_clone", new JsonObject
            {
                ["remoteUrl"] = remoteUrl,
                ["targetDirectory"] = fixtureCloneRelative,
                ["historyDepth"] = 0,
                ["fetchAllBranches"] = false,
                ["tagFetchMode"] = "none"
            }, ct);
            fixtureProjectRoot = RequireNestedString(clone, "projectRootRelative");
            var baseBranch = RequireNestedString(clone, "resolvedBranch");
            await CallSessionToolAsync(git, "git_create_branch", new JsonObject
            {
                ["projectRoot"] = fixtureProjectRoot,
                ["branchName"] = branchName,
                ["checkout"] = true
            }, ct);
            await WriteFixtureAsync(workspaceRoot, fixtureProjectRoot, fixtureRelativePath, ct);
            await CallSessionToolAsync(git, "git_stage", new JsonObject
            {
                ["projectRoot"] = fixtureProjectRoot,
                ["pathsJson"] = new JsonArray(fixtureRelativePath).ToJsonString()
            }, ct);
            await CallSessionToolAsync(git, "git_commit", new JsonObject
            {
                ["projectRoot"] = fixtureProjectRoot,
                ["message"] = "test: add intention-first PR review fixture",
                ["authorName"] = "GnOuGo E2E",
                ["authorEmail"] = "gnougo-e2e@localhost"
            }, ct);
            await CallSessionToolAsync(git, "git_push", new JsonObject
            {
                ["projectRoot"] = fixtureProjectRoot,
                ["branchName"] = branchName,
                ["setUpstream"] = true
            }, ct);
            branchPushed = true;

            var created = await CallSessionToolAsync(github, "create_pull_request", new JsonObject
            {
                ["owner"] = owner,
                ["repo"] = repository,
                ["base"] = baseBranch,
                ["head"] = branchName,
                ["draft"] = true,
                ["title"] = "[E2E] GnOuGo intention-first agent review fixture",
                ["body"] = "Disposable fixture for validating the generated intention-first review agent. It must never be merged."
            }, ct);
            pullNumber = RequirePullNumber(created);

            var recordingFactory = new WriteDenyingMcpClientFactory(factory, contract.GitHubServers, denyWrites: false);
            var runtimeFactory = new SecureWorkflowRuntimeFactory(
                services.GetRequiredService<LLMRuntimeOptionsStore>(),
                services.GetRequiredService<IKeyVaultRuntimeConfigStore>(),
                services.GetRequiredService<ILoggerFactory>(),
                mcpClientFactoryOverride: recordingFactory,
                backgroundModeCache: services.GetRequiredService<IMemoryCache>(),
                llmCapabilityResolver: services.GetService<ILLMCapabilityResolver>(),
                humanInputProvider: humanInput);
            var smartFlow = ActivatorUtilities.CreateInstance<SmartFlowService>(services, runtimeFactory);
            var inputs = new JsonObject
            {
                [contract.PullRequestUrlInput] = $"https://github.com/{owner}/{repository}/pull/{pullNumber.Value}",
            ["review_instructions"] = "Report the demonstrable division-by-zero correctness defect introduced by the changed fixture line. Publish it as a high-confidence inline finding and submit REQUEST_CHANGES."
            };

            using var responderCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var expectedPullRequestUrl = $"https://github.com/{owner}/{repository}/pull/{pullNumber.Value}";
            var responder = ApprovePublicationAsync(humanInput, expectedPullRequestUrl, responderCancellation.Token);
            var events = await CollectAsync(
                smartFlow.ExecuteAsync(
                    "Review and publish the validated finding for the disposable fixture.",
                    correlationId: $"live-intent-fixture-{runId}",
                    agentName,
                    filesIds: null,
                    workflowInputs: inputs,
                    ct),
                ct);
            responderCancellation.Cancel();
            try
            {
                await responder;
            }
            catch (OperationCanceledException) when (responderCancellation.IsCancellationRequested)
            {
                // A missing gate is asserted below from the recorded write calls.
            }

            var failure = events.FirstOrDefault(static item => item.Type == "error");
            Assert.Null(failure);
            Assert.Contains(events, static item => item.Type == "answer" && !string.IsNullOrWhiteSpace(item.Text));
            Assert.Equal(0, recordingFactory.DeniedWriteAttempts);
            Assert.Contains(recordingFactory.Calls, static call => call.IsGitHub
                && string.Equals(call.Method, "add_comment_to_pending_review", StringComparison.Ordinal));
            Assert.Contains(recordingFactory.Calls, static call => call.IsGitHub
                && string.Equals(call.Method, "pull_request_review_write", StringComparison.Ordinal)
                && string.Equals((call.Arguments as JsonObject)?["method"]?.GetValue<string>(), "submit_pending", StringComparison.Ordinal)
                && string.Equals((call.Arguments as JsonObject)?["event"]?.GetValue<string>(), "REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordingFactory.Calls, static call =>
                call.Method.Contains("merge", StringComparison.OrdinalIgnoreCase)
                || call.Method.Contains("push", StringComparison.OrdinalIgnoreCase)
                || string.Equals((call.Arguments as JsonObject)?["event"]?.GetValue<string>(), "APPROVE", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (pullNumber is not null)
            {
                try
                {
                    await github.CallToolAsync("update_pull_request", new JsonObject
                    {
                        ["owner"] = owner,
                        ["repo"] = repository,
                        ["pullNumber"] = pullNumber.Value,
                        ["state"] = "closed"
                    }, CancellationToken.None);
                }
                catch
                {
                    // Cleanup is best effort; the original live assertion remains primary.
                }
            }

            if (branchPushed && fixtureProjectRoot is not null)
            {
                try
                {
                    await git.CallToolAsync("git_delete_remote_branch", new JsonObject
                    {
                        ["projectRoot"] = fixtureProjectRoot,
                        ["remoteName"] = "origin",
                        ["branchName"] = branchName
                    }, CancellationToken.None);
                }
                catch
                {
                    // Cleanup is best effort; the original live assertion remains primary.
                }
            }

            DeleteIsolatedDirectory(workspaceRoot, fixtureCloneRelative, "workflows/e2e");
            DeleteNewWorkflowWorkspaces(workflowWorkspacesRoot, existingWorkflowWorkspaces);
        }
    }

    private static async Task ApprovePublicationAsync(
        AgentHumanInputProvider humanInput,
        string expectedPullRequestUrl,
        CancellationToken ct)
    {
        await foreach (var request in humanInput.PendingRequests.ReadAllAsync(ct))
        {
            Assert.True(request.Mode is HumanInputContract.ModeConfirm or HumanInputContract.ModeChoice,
                $"Unexpected publication approval mode '{request.Mode}' for step '{request.StepId}'.");
            var visibleContext = request.Prompt + "\n" + (request.Context?.ToJsonString() ?? string.Empty);
            var mentionedUrls = Regex.Matches(visibleContext, "https://github\\.com/[^\\s\\\"'<>]+/pull/\\d+", RegexOptions.IgnoreCase)
                .Select(static match => match.Value.TrimEnd('.', ',', ')'))
                .ToArray();
            Assert.All(mentionedUrls, url => Assert.Equal(expectedPullRequestUrl, url));
            var affirmativeChoice = request.Choices?.FirstOrDefault(static choice =>
                choice.Contains("approve", StringComparison.OrdinalIgnoreCase)
                || choice.Contains("publish", StringComparison.OrdinalIgnoreCase)
                || choice.Equals("yes", StringComparison.OrdinalIgnoreCase));
            JsonNode response = new JsonObject
            {
                ["response"] = request.Mode == HumanInputContract.ModeConfirm ? true : affirmativeChoice ?? "approve",
                ["confirmed"] = true,
                ["approved"] = true,
                ["decision"] = "approve"
            };
            Assert.True(humanInput.TrySubmitResponse(request.RunId, request.StepId, response));
            return;
        }
    }

    private static async Task<JsonObject> CallSessionToolAsync(
        IMcpSession session,
        string method,
        JsonNode? arguments,
        CancellationToken ct)
    {
        var result = await session.CallToolAsync(method, arguments, ct);
        Assert.False(result.IsError, $"MCP tool '{method}' failed.");
        return Assert.IsType<JsonObject>(result.Content);
    }

    private static async Task<string> ResolveFixtureGitServerAsync(
        IMcpClientFactory factory,
        string? preferredServer,
        CancellationToken ct)
    {
        var candidates = string.IsNullOrWhiteSpace(preferredServer)
            ? factory.ServerMetadata.Select(static server => server.Name)
            : new[] { preferredServer }.Concat(factory.ServerMetadata.Select(static server => server.Name));
        foreach (var serverName in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await using var session = await factory.GetClientAsync(serverName, ct);
                var tools = (await session.ListToolsAsync(ct)).Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                if (new[] { "git_clone", "git_create_branch", "git_stage", "git_commit", "git_push", "git_delete_remote_branch" }
                    .All(tools.Contains))
                {
                    return serverName;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Continue discovery; the selected fixture server must prove the complete contract.
            }
        }
        throw new InvalidOperationException("No configured MCP server exposes the complete writable Git fixture contract.");
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

            // Disposable correctness fixture used only by the live intention-first agent test.
            public static class IntentReviewFixture
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

    private static async Task<(string Owner, string Repository, string RemoteUrl)> ReadOriginAsync(
        string sourceRoot,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = sourceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("remote");
        startInfo.ArgumentList.Add("get-url");
        startInfo.ArgumentList.Add("origin");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git to resolve the source repository origin.");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.Equal(0, process.ExitCode);
        var normalized = output.Trim();
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        string path;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath.Trim('/');
        else
            path = normalized[(normalized.LastIndexOf(':') + 1)..].Trim('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 2, "Could not parse owner/repository from the source origin URL.");
        return (parts[^2], parts[^1], $"https://github.com/{parts[^2]}/{parts[^1]}.git");
    }

    private static int RequirePullNumber(JsonNode node)
    {
        foreach (var value in EnumerateNodes(node))
        {
            if (value is JsonObject obj)
            {
                foreach (var property in new[] { "number", "pullNumber", "pull_number" })
                {
                    if (obj[property] is JsonValue scalar && scalar.TryGetValue<int>(out var number))
                        return number;
                }
            }
            if (value is JsonValue textValue && textValue.TryGetValue<string>(out var text))
            {
                var match = Regex.Match(text, @"/pull/(?<number>\d+)(?:\D|$)", RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups["number"].Value, out var number))
                    return number;
            }
        }
        throw new InvalidOperationException("The create_pull_request result omitted the pull-request number.");
    }

    private static string RequireNestedString(JsonNode node, string property)
    {
        foreach (var value in EnumerateNodes(node).OfType<JsonObject>())
        {
            var match = value.FirstOrDefault(item => string.Equals(item.Key, property, StringComparison.OrdinalIgnoreCase)).Value;
            if (match is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
        }
        throw new InvalidOperationException($"MCP response omitted '{property}'.");
    }

    private static IEnumerable<JsonNode> EnumerateNodes(JsonNode root)
    {
        yield return root;
        var children = root switch
        {
            JsonObject obj => obj.Select(static property => property.Value).OfType<JsonNode>(),
            JsonArray array => array.OfType<JsonNode>(),
            _ => []
        };
        foreach (var child in children)
        foreach (var nested in EnumerateNodes(child))
            yield return nested;
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

    private static void DeleteNewWorkflowWorkspaces(string workflowWorkspacesRoot, IReadOnlySet<string> existing)
    {
        if (!Directory.Exists(workflowWorkspacesRoot))
            return;

        var newDirectories = Directory
            .EnumerateDirectories(workflowWorkspacesRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(directory => !existing.Contains(directory)
                                && GnOuGoWorkspace.IsPathWithinRoot(directory, workflowWorkspacesRoot))
            .OrderBy(static directory => directory.Length)
            .ToArray();
        foreach (var directory in newDirectories)
        {
            if (newDirectories.Any(parent => parent.Length < directory.Length
                                             && directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                continue;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlySet<string> SnapshotWorkflowWorkspaces(string workflowWorkspacesRoot)
        => Directory.Exists(workflowWorkspacesRoot)
            ? Directory.EnumerateDirectories(workflowWorkspacesRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static async Task RejectPublicationAsync(AgentHumanInputProvider humanInput, CancellationToken ct)
    {
        await foreach (var request in humanInput.PendingRequests.ReadAllAsync(ct))
        {
            var negativeChoice = request.Choices?.FirstOrDefault(static choice =>
                choice.Contains("reject", StringComparison.OrdinalIgnoreCase)
                || choice.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                || choice.Equals("no", StringComparison.OrdinalIgnoreCase));
            JsonNode response = new JsonObject
            {
                ["response"] = request.Mode == HumanInputContract.ModeConfirm
                    ? false
                    : negativeChoice ?? "reject",
                ["confirmed"] = false,
                ["approved"] = false,
                ["decision"] = "reject"
            };
            Assert.True(humanInput.TrySubmitResponse(request.RunId, request.StepId, response));
            return;
        }
    }

    private static IEnumerable<StepDef> EnumerateSteps(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var nested in EnumerateSteps(step.Steps ?? []))
                yield return nested;
            foreach (var branch in step.Branches ?? [])
            foreach (var nested in EnumerateSteps(branch.Steps))
                yield return nested;
            foreach (var item in step.Cases ?? [])
            foreach (var nested in EnumerateSteps(item.Steps))
                yield return nested;
            foreach (var nested in EnumerateSteps(step.Default ?? []))
                yield return nested;
        }
    }

    private static IEnumerable<StepDef> EnumerateReachableSteps(WorkflowDocument document, string entrypoint)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(entrypoint);

        while (pending.TryDequeue(out var workflowName))
        {
            if (!visited.Add(workflowName) || !document.Workflows.TryGetValue(workflowName, out var workflow))
                continue;

            var steps = EnumerateSteps(workflow.Steps)
                .Concat(EnumerateSteps(workflow.Finally))
                .ToArray();
            foreach (var step in steps)
            {
                yield return step;
                if (step.Type != "workflow.call"
                    || step.Input?["ref"] is not JsonObject reference
                    || !string.Equals(reference["kind"]?.GetValue<string>() ?? "local", "local", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(reference["name"]?.GetValue<string>()))
                {
                    continue;
                }

                pending.Enqueue(reference["name"]!.GetValue<string>());
            }
        }
    }

    private static async Task<List<SmartFlowEvent>> CollectAsync(
        IAsyncEnumerable<SmartFlowEvent> events,
        CancellationToken ct,
        Action<SmartFlowEvent>? observer = null)
    {
        var result = new List<SmartFlowEvent>();
        await foreach (var item in events.WithCancellation(ct))
        {
            observer?.Invoke(item);
            result.Add(item);
        }
        return result;
    }

    private static void WriteLiveProgress(
        string stage,
        int? generation = null,
        int? providerAttempt = null,
        SmartFlowEvent? flowEvent = null)
    {
        var path = Environment.GetEnvironmentVariable(ProgressPathVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        var entry = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["stage"] = stage
        };
        if (generation.HasValue)
            entry["generation"] = generation.Value;
        if (providerAttempt.HasValue)
            entry["provider_attempt"] = providerAttempt.Value;
        if (flowEvent != null)
        {
            entry["event_type"] = flowEvent.Type;
            entry["error_code"] = flowEvent.ErrorCode;
            entry["retryable"] = flowEvent.Retryable;
        }

        try
        {
            lock (ProgressFileLock)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(path, entry.ToJsonString() + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Optional live-test diagnostics must not change the acceptance result.
        }
        catch (UnauthorizedAccessException)
        {
            // Optional live-test diagnostics must not change the acceptance result.
        }
    }

    private static string RequireString(JsonObject value, string property)
        => value[property]?.GetValue<string>()
           ?? throw new InvalidOperationException($"Live Agent MCP response omitted '{property}'.");

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GnOuGo.Agent.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the GnOuGo source root.");
    }

    private sealed record GeneratedAgentContract(
        string PullRequestUrlInput,
        bool HasNativeLlmCall,
        IReadOnlySet<string> GitHubServers,
        string? GitServer);

    private sealed record RecordedMcpCall(string Server, string Method, JsonNode? Arguments, bool IsGitHub)
    {
        public bool IsPotentialWrite
        {
            get
            {
                if (!IsGitHub)
                    return false;

                if (Method.Contains("read", StringComparison.OrdinalIgnoreCase)
                    || Method.StartsWith("get", StringComparison.OrdinalIgnoreCase)
                    || Method.StartsWith("list", StringComparison.OrdinalIgnoreCase)
                    || Method.StartsWith("search", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var selector = (Arguments as JsonObject)?["method"]?.GetValue<string>();
                return selector is null
                       || !(selector.StartsWith("get", StringComparison.OrdinalIgnoreCase)
                            || selector.StartsWith("list", StringComparison.OrdinalIgnoreCase)
                            || selector.StartsWith("search", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private sealed class WriteDenyingMcpClientFactory(
        IMcpClientFactory inner,
        IReadOnlySet<string> githubServers,
        bool denyWrites = true) : IMcpClientFactory
    {
        private readonly List<RecordedMcpCall> _calls = [];
        private int _deniedWriteAttempts;

        public IReadOnlyList<McpServerMetadata> ServerMetadata => inner.ServerMetadata;

        public IReadOnlyList<RecordedMcpCall> Calls
        {
            get
            {
                lock (_calls)
                    return _calls.ToArray();
            }
        }

        public int DeniedWriteAttempts => Volatile.Read(ref _deniedWriteAttempts);

        public async Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
            => new WriteDenyingMcpSession(
                await inner.GetClientAsync(serverName, ct),
                githubServers.Contains(serverName),
                Record);

        private void Record(RecordedMcpCall call)
        {
            lock (_calls)
                _calls.Add(call);
            if (!denyWrites || !call.IsPotentialWrite)
                return;

            Interlocked.Increment(ref _deniedWriteAttempts);
            throw new InvalidOperationException(
                $"Live read-only acceptance blocked external write '{call.Server}/{call.Method}'.");
        }
    }

    private sealed class WriteDenyingMcpSession(
        IMcpSession inner,
        bool isGitHub,
        Action<RecordedMcpCall> record) : IMcpSession
    {
        public string ServerName => inner.ServerName;

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => inner.ListToolsAsync(ct);

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => inner.ListResourcesAsync(ct);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => inner.ListPromptsAsync(ct);

        public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
        {
            record(new RecordedMcpCall(ServerName, toolName, arguments?.DeepClone(), isGitHub));
            return inner.CallToolAsync(toolName, arguments, ct);
        }

        public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
            => inner.GetPromptAsync(promptName, arguments, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
