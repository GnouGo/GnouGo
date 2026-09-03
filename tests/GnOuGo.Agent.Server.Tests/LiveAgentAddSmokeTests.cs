using System.Text.Json.Nodes;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveAgentAddSmokeTests
{
    private const string EnableVariable = "GNOU_GO_LIVE_AGENT_ADD_SMOKE";
    private const string SmokeIntent = """
        Create a reusable agent that returns the fixed greeting text "Hello" to the caller.
        """;
    private const string ConditionalRuntimeChoiceIntent = """
        Create a reusable agent that accepts a pull-request URL and review instructions.

        For the supplied change:
        1. Materialize the project once.
        2. Restore dependencies for every modified project.
        3. Run all relevant unit tests and linters.
        4. Review all changed code and retain only high-confidence findings.
        5. Publish an approval only when restoration, tests, lint, complete coverage, and the review all succeed without findings; otherwise request changes with a functional-then-technical summary. Publish nothing when the result cannot be established safely.

        Always remove every directory created by the workflow at the end.
        """;

    [Fact]
    [Trait("Category", "Live")]
    public async Task GenericNoExternalEffectIntent_GeneratesValidPersistedAgent()
        => await RunAgentAddAsync(SmokeIntent, "fixed-output");

    [Fact]
    [Trait("Category", "Live")]
    public async Task ConditionalRuntimeChoiceIntent_FailsClosedWhenCatalogLacksEveryRequiredEffectBranch()
        => await RunAgentAddAsync(
            ConditionalRuntimeChoiceIntent,
            "conditional-choice",
            ErrorCodes.CapabilityPreflightUnavailable);

    private static async Task RunAgentAddAsync(
        string intent,
        string nameQualifier,
        string? expectedErrorCode = null)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = timeout.Token;
        var sourceRoot = FindSourceRoot();
        var agentName = $"live-agent-add-{nameQualifier}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var telemetryDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"gnougo-live-agent-add-smoke-{Guid.NewGuid():N}.db");
        var previousDirectory = Directory.GetCurrentDirectory();
        WebApplication? app = null;
        AgentUserConfigSnapshot? previousConfig = null;
        Exception? testFailure = null;
        var cleanupFailures = new List<Exception>();

        try
        {
            Directory.SetCurrentDirectory(sourceRoot);
            app = GnOuGoAgentWebHost.Build(
                [
                    "--OtlpCollector:Enabled=false",
                    "--OpenTelemetry:Enabled=false",
                    $"--Database:Path={telemetryDatabasePath}"
                ],
                urls: "http://127.0.0.1:0",
                contentRoot: Path.Combine(sourceRoot, "src", "GnOuGo.Agent.Server"),
                enableHttpsRedirection: false);
            await app.StartAsync(ct);

            var configureAgents = app.Services.GetRequiredService<ConfigureAgentsService>();
            var humanInput = app.Services.GetRequiredService<AgentHumanInputProvider>();
            var userConfig = app.Services.GetRequiredService<AgentUserConfigMcpClient>();
            var mcpFactory = app.Services.GetRequiredService<IMcpClientFactory>();
            previousConfig = await userConfig.GetAsync(ct);

            using var responderCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var responder = RespondToAgentCreationAsync(
                humanInput,
                agentName,
                intent,
                responderCancellation.Token);
            var events = await CollectAsync(configureAgents.ExecuteAsync("/gnougo add", ct), ct);
            responderCancellation.Cancel();
            try
            {
                await responder;
            }
            catch (OperationCanceledException) when (responderCancellation.IsCancellationRequested)
            {
                // A failure can end the command before all interactive requests are emitted.
            }

            var failure = events.FirstOrDefault(static item => item.Type == "error");
            var answerFailure = events.FirstOrDefault(static item => item.Type == "answer"
                && item.Text?.StartsWith("❌", StringComparison.Ordinal) == true);
            if (expectedErrorCode is not null)
            {
                var expectedFailure = failure ?? answerFailure;
                Assert.NotNull(expectedFailure);
                Assert.True(
                    string.Equals(expectedErrorCode, expectedFailure.ErrorCode, StringComparison.Ordinal),
                    expectedFailure.Text ?? $"Expected {expectedErrorCode}, received {expectedFailure.ErrorCode}.");
                Assert.DoesNotContain(events, static item => string.Equals(
                    item.ErrorCode,
                    ErrorCodes.CapabilityPreflightInferenceFailed,
                    StringComparison.Ordinal));
                Assert.False(await AgentExistsAsync(mcpFactory, agentName, ct));
                return;
            }

            Assert.True(failure is null && answerFailure is null, failure?.Text ?? answerFailure?.Text);
            Assert.DoesNotContain(events, static item => string.Equals(
                item.ErrorCode,
                ErrorCodes.CapabilityPreflightDiscoveryFailed,
                StringComparison.Ordinal));

            var persisted = await GetAgentAsync(mcpFactory, agentName, ct);
            Assert.Equal(intent.Trim().ReplaceLineEndings("\n"),
                RequireString(persisted, "original_prompt").Trim().ReplaceLineEndings("\n"));
            var yaml = RequireString(persisted, "workflow");
            var document = WorkflowParser.Parse(yaml);
            var compiled = new WorkflowCompiler().Compile(document);
            Assert.False(string.IsNullOrWhiteSpace(compiled.Entrypoint));
            Assert.Contains(compiled.Entrypoint!, document.Workflows.Keys);
        }
        catch (Exception ex)
        {
            testFailure = ex;
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    var mcpFactory = app.Services.GetService<IMcpClientFactory>();
                    if (mcpFactory is not null)
                        await DeleteAgentIfPresentAsync(mcpFactory, agentName, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }

                if (previousConfig is not null)
                {
                    try
                    {
                        var userConfig = app.Services.GetRequiredService<AgentUserConfigMcpClient>();
                        if (string.IsNullOrWhiteSpace(previousConfig.DefaultAgent))
                            await userConfig.SetAsync(clearDefaultAgent: true, ct: CancellationToken.None);
                        else
                            await userConfig.SetAsync(defaultAgent: previousConfig.DefaultAgent, ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(ex);
                    }
                }

                try
                {
                    await app.StopAsync(CancellationToken.None);
                    await app.DisposeAsync();
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }

            Directory.SetCurrentDirectory(previousDirectory);
            foreach (var path in new[]
                     {
                         telemetryDatabasePath,
                         $"{telemetryDatabasePath}-wal",
                         $"{telemetryDatabasePath}-shm"
                     })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }
        }

        if (testFailure is not null || cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "The live /gnougo add smoke test or its cleanup failed.",
                testFailure is null ? cleanupFailures : [testFailure, .. cleanupFailures]);
        }
    }

    private static async Task RespondToAgentCreationAsync(
        AgentHumanInputProvider humanInput,
        string agentName,
        string intent,
        CancellationToken ct)
    {
        await foreach (var request in humanInput.PendingRequests.ReadAllAsync(ct))
        {
            JsonNode response = request.StepId.EndsWith("input_name", StringComparison.Ordinal)
                ? Submit(new JsonObject { ["agent_name"] = agentName })
                : request.StepId.EndsWith("input_prompt", StringComparison.Ordinal)
                    ? Submit(new JsonObject { ["description"] = intent })
                    : request.StepId.Contains(":intent_clarification:", StringComparison.Ordinal)
                        ? BuildRecommendedResponse(request)
                        : request.StepId.Contains(":capability_clarification:", StringComparison.Ordinal)
                            ? BuildCapabilityResponse(request)
                            : request.StepId.EndsWith("review_workflow", StringComparison.Ordinal)
                                ? Submit(new JsonObject { ["response"] = "approve" })
                                : throw new InvalidOperationException(
                                    $"Unexpected agent-generation human input step '{request.StepId}'.");
            Assert.True(humanInput.TrySubmitResponse(request.RunId, request.StepId, response));
            if (request.StepId.EndsWith("review_workflow", StringComparison.Ordinal))
                return;
        }
    }

    private static JsonObject BuildRecommendedResponse(HumanInputRequest request)
    {
        var response = new JsonObject();
        foreach (var field in request.Fields ?? [])
        {
            response[field.Name] = field.Default
                                   ?? field.OptionDefinitions?.FirstOrDefault(static option => option.Recommended)?.Value
                                   ?? field.Options?.FirstOrDefault()
                                   ?? "Preserve the observable behavior, effect boundaries, and failure policy from the original request.";
        }
        return Submit(response);
    }

    private static JsonObject BuildCapabilityResponse(HumanInputRequest request)
    {
        var response = new JsonObject();
        foreach (var field in request.Fields ?? [])
        {
            response[field.Name] = field.Default
                                   ?? field.OptionDefinitions?.FirstOrDefault(static option => option.Recommended)?.Value
                                   ?? field.Options?.FirstOrDefault()
                                   ?? "Preserve the original requested behavior without weakening its guarantees.";
        }
        return Submit(response);
    }

    private static JsonObject Submit(JsonObject response)
    {
        response[HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit;
        return response;
    }

    private static async Task<List<SmartFlowEvent>> CollectAsync(
        IAsyncEnumerable<SmartFlowEvent> events,
        CancellationToken ct)
    {
        var collected = new List<SmartFlowEvent>();
        await foreach (var item in events.WithCancellation(ct))
            collected.Add(item);
        return collected;
    }

    private static async Task<JsonObject> GetAgentAsync(
        IMcpClientFactory factory,
        string name,
        CancellationToken ct)
    {
        await using var session = await factory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
        var result = await session.CallToolAsync(
            "agent_get_by_name",
            new JsonObject { ["name"] = name },
            ct);
        Assert.False(result.IsError);
        var payload = Assert.IsType<JsonObject>(result.Content);
        Assert.True(payload["success"]?.GetValue<bool>() == true, payload.ToJsonString());
        return Assert.IsType<JsonObject>(payload["agent"]);
    }

    private static async Task<bool> AgentExistsAsync(
        IMcpClientFactory factory,
        string name,
        CancellationToken ct)
    {
        await using var session = await factory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
        var result = await session.CallToolAsync(
            "agent_get_by_name",
            new JsonObject { ["name"] = name },
            ct);
        var payload = Assert.IsType<JsonObject>(result.Content);
        return !result.IsError && payload["success"]?.GetValue<bool>() == true;
    }

    private static async Task DeleteAgentIfPresentAsync(
        IMcpClientFactory factory,
        string name,
        CancellationToken ct)
    {
        await using var session = await factory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
        var lookup = await session.CallToolAsync(
            "agent_get_by_name",
            new JsonObject { ["name"] = name },
            ct);
        var lookupPayload = Assert.IsType<JsonObject>(lookup.Content);
        if (lookupPayload["success"]?.GetValue<bool>() != true)
            return;

        var agent = Assert.IsType<JsonObject>(lookupPayload["agent"]);
        var deleted = await session.CallToolAsync(
            "agent_delete",
            new JsonObject { ["id"] = RequireString(agent, "id") },
            ct);
        Assert.False(deleted.IsError);
        var deletePayload = Assert.IsType<JsonObject>(deleted.Content);
        Assert.True(deletePayload["success"]?.GetValue<bool>() == true, deletePayload.ToJsonString());

        var afterDelete = await session.CallToolAsync(
            "agent_get_by_name",
            new JsonObject { ["name"] = name },
            ct);
        var afterDeletePayload = Assert.IsType<JsonObject>(afterDelete.Content);
        Assert.True(afterDelete.IsError, afterDeletePayload.ToJsonString());
        Assert.Equal("NOT_FOUND", afterDeletePayload["error_code"]?.GetValue<string>());
    }

    private static string RequireString(JsonObject value, string property)
        => value[property]?.GetValue<string>()
           ?? throw new InvalidOperationException($"Live Agent MCP response omitted '{property}'.");

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GnOuGo.Agent.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the GnOuGo source root.");
    }
}
