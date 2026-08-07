using System.Reflection;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using McpProtocolServer = ModelContextProtocol.Server.McpServer;
using McpProtocolServerOptions = ModelContextProtocol.Server.McpServerOptions;
using McpProtocolServerTool = ModelContextProtocol.Server.McpServerTool;
using McpProtocolServerToolCreateOptions = ModelContextProtocol.Server.McpServerToolCreateOptions;
using McpStreamServerTransport = ModelContextProtocol.Server.StreamServerTransport;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class ConfiguredMcpClientFactoryTests
{
    [Fact]
    public async Task ServerMetadata_IncludesConfiguredDiscoveryTimeout()
    {
        await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["slow"] = new()
            {
                Type = "stdio",
                Description = "Slow cold-start server",
                DiscoveryTimeoutSeconds = 90,
                CallTimeoutSeconds = 1200,
                Command = "dotnet"
            }
        });

        var metadata = Assert.Single(factory.ServerMetadata);
        Assert.Equal("slow", metadata.Name);
        Assert.Equal("Slow cold-start server", metadata.Description);
        Assert.Equal(90, metadata.DiscoveryTimeoutSeconds);
        Assert.Equal(1200, metadata.CallTimeoutSeconds);
    }

    [Fact]
    public void CreateClientOptions_LeavesProtocolVersionUnpinnedForAutomaticFallback()
    {
        var options = ConfiguredMcpClientFactory.CreateClientOptions();

        Assert.Null(options.ProtocolVersion);
        Assert.Equal("GnOuGo.Flow", options.ClientInfo?.Name);
        Assert.Equal("1.0.0", options.ClientInfo?.Version);
    }

    [Fact]
    public async Task ElicitationHandler_MapsChoiceEnvelopeToRequestedFieldAndDropsProviderMetadata()
    {
        var provider = new ScriptedHumanInputProvider(new JsonObject
        {
            ["response"] = "Allow once",
            ["source"] = "blazor",
            ["unexpected"] = "discard-me"
        });
        var options = ConfiguredMcpClientFactory.CreateClientOptions(provider);
        var handler = options.Handlers?.ElicitationHandler;
        Assert.NotNull(handler);

        var result = await handler!(new ElicitRequestParams
        {
            Mode = "form",
            Message = "Allow this operation?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["answer"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                    {
                        Enum = ["Allow once", "Refuse"],
                        Description = "Shell command: dotnet test"
                    }
                },
                Required = ["answer"]
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal("accept", result.Action);
        Assert.NotNull(result.Content);
        Assert.Equal("Allow once", result.Content["answer"].GetString());
        Assert.DoesNotContain("source", result.Content.Keys);
        Assert.DoesNotContain("unexpected", result.Content.Keys);
        Assert.Equal(HumanInputContract.ModeChoice, provider.LastRequest?.Mode);
        Assert.Equal("Shell command: dotnet test", Assert.Single(provider.LastRequest!.Fields!).Description);
    }

    [Fact]
    public async Task ElicitationHandler_PublishesCallScopedWaitingAndResumedSignals()
    {
        var provider = new ScriptedHumanInputProvider(new JsonObject { ["response"] = "Allow once" });
        var correlation = new McpCorrelationContext
        {
            CorrelationId = "correlation-1",
            RunId = "run-1",
            StepId = "copilot-step",
            StepType = "mcp.call",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot",
            Kind = "tool"
        };
        var signals = new List<McpHumanInputSignal>();
        using var correlationScope = ConfiguredMcpClientFactory.PushCorrelationContext(correlation);
        using var signalScope = ConfiguredMcpClientFactory.PushHumanInputHandler(correlation, signals.Add);
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider).Handlers!.ElicitationHandler!;

        var result = await handler(new ElicitRequestParams
        {
            Mode = "form",
            Message = "Allow this operation?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["answer"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                    {
                        Enum = ["Allow once", "Refuse"],
                        Description = "Shell command: dotnet test"
                    }
                },
                Required = ["answer"]
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal("accept", result.Action);
        Assert.Equal([McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Resumed], signals.Select(static signal => signal.Phase));
        Assert.All(signals, signal => Assert.Same(correlation, signal.Correlation));
        Assert.Equal("run-1", signals[0].Request.RunId);
        Assert.Equal("copilot-step", signals[0].Request.StepId);
        Assert.Equal("copilot", signals[0].Request.Context!["mcp_server"]!.GetValue<string>());
        Assert.Equal("copilot_interactive_one_shot", signals[0].Request.Context!["mcp_method"]!.GetValue<string>());
    }

    [Fact]
    public async Task ElicitationHandler_PublishesCancelledSignalWhenProviderIsCancelled()
    {
        var provider = new CancellingHumanInputProvider();
        var correlation = new McpCorrelationContext
        {
            CorrelationId = "correlation-2",
            RunId = "run-2",
            StepId = "copilot-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var signals = new List<McpHumanInputSignal>();
        using var correlationScope = ConfiguredMcpClientFactory.PushCorrelationContext(correlation);
        using var signalScope = ConfiguredMcpClientFactory.PushHumanInputHandler(correlation, signals.Add);
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider).Handlers!.ElicitationHandler!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler(
            new ElicitRequestParams { Mode = "form", Message = "Allow this operation?" },
            cancellation.Token));

        Assert.Equal([McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Cancelled], signals.Select(static signal => signal.Phase));
    }

    [Fact]
    public async Task ElicitationHandler_PublishesRefusedSignalAndSupportsSequentialPermissions()
    {
        var provider = new SequencedHumanInputProvider(
            new JsonObject { ["response"] = "Allow once" },
            new JsonObject { ["response"] = "Refuse" });
        var correlation = new McpCorrelationContext
        {
            CorrelationId = "correlation-sequential",
            RunId = "run-sequential",
            StepId = "copilot-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var signals = new List<McpHumanInputSignal>();
        using var signalScope = ConfiguredMcpClientFactory.PushHumanInputHandler(correlation, signals.Add);
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider, "copilot").Handlers!.ElicitationHandler!;
        var request = new ElicitRequestParams { Mode = "form", Message = "Allow this operation?", Meta = BuildCorrelationMeta(correlation) };

        var accepted = await handler(request, TestContext.Current.CancellationToken);
        var refused = await handler(request, TestContext.Current.CancellationToken);

        Assert.Equal("accept", accepted.Action);
        Assert.Equal("accept", refused.Action);
        Assert.Equal(
            [McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Resumed, McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Refused],
            signals.Select(static signal => signal.Phase));
    }

    [Fact]
    public async Task ElicitationHandler_ProviderTimeoutPublishesCancelledSignal()
    {
        var provider = new BlockingHumanInputProvider();
        var correlation = new McpCorrelationContext
        {
            CorrelationId = "correlation-timeout",
            RunId = "run-timeout",
            StepId = "copilot-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var signals = new List<McpHumanInputSignal>();
        using var signalScope = ConfiguredMcpClientFactory.PushHumanInputHandler(correlation, signals.Add);
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider, "copilot").Handlers!.ElicitationHandler!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler(
            new ElicitRequestParams { Mode = "form", Message = "Allow?", Meta = BuildCorrelationMeta(correlation) },
            cancellation.Token));

        Assert.Equal([McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Cancelled], signals.Select(static signal => signal.Phase));
    }

    [Fact]
    public void HumanInputSignal_UsesExactCorrelationWithConcurrentCallsToSameTool()
    {
        var first = new McpCorrelationContext
        {
            CorrelationId = "correlation-first",
            RunId = "run-first",
            StepId = "copilot-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var second = new McpCorrelationContext
        {
            CorrelationId = "correlation-second",
            RunId = "run-second",
            StepId = "copilot-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var firstSignals = new List<McpHumanInputSignal>();
        var secondSignals = new List<McpHumanInputSignal>();
        using var firstScope = ConfiguredMcpClientFactory.PushHumanInputHandler(first, firstSignals.Add);
        using var secondScope = ConfiguredMcpClientFactory.PushHumanInputHandler(second, secondSignals.Add);

        var delivered = ConfiguredMcpClientFactory.PublishHumanInput(new McpHumanInputSignal(
            first,
            new HumanInputRequest { RunId = first.RunId, StepId = first.StepId, Prompt = "Allow?" },
            McpHumanInputSignalPhase.Waiting));

        Assert.True(delivered);
        Assert.Single(firstSignals);
        Assert.Empty(secondSignals);
    }

    [Fact]
    public async Task ElicitationHandler_PrefersCallMetadataOverCachedClientAsyncLocalContext()
    {
        var provider = new ScriptedHumanInputProvider(new JsonObject { ["response"] = "Allow once" });
        var staleCorrelation = new McpCorrelationContext
        {
            CorrelationId = "stale-correlation",
            RunId = "stale-run",
            StepId = "stale-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var activeCorrelation = new McpCorrelationContext
        {
            CorrelationId = "active-correlation",
            RunId = "active-run",
            StepId = "active-step",
            ServerName = "copilot",
            MethodName = "copilot_interactive_one_shot"
        };
        var staleSignals = new List<McpHumanInputSignal>();
        var activeSignals = new List<McpHumanInputSignal>();
        using var correlationScope = ConfiguredMcpClientFactory.PushCorrelationContext(staleCorrelation);
        using var staleScope = ConfiguredMcpClientFactory.PushHumanInputHandler(staleCorrelation, staleSignals.Add);
        using var activeScope = ConfiguredMcpClientFactory.PushHumanInputHandler(activeCorrelation, activeSignals.Add);
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider).Handlers!.ElicitationHandler!;

        var result = await handler(new ElicitRequestParams
        {
            Mode = "form",
            Message = "Allow this operation?",
            Meta = new JsonObject
            {
                ["gnougo"] = new JsonObject
                {
                    ["correlationId"] = activeCorrelation.CorrelationId,
                    ["runId"] = activeCorrelation.RunId,
                    ["stepId"] = activeCorrelation.StepId,
                    ["mcpServer"] = activeCorrelation.ServerName,
                    ["mcpMethod"] = activeCorrelation.MethodName,
                    ["mcpKind"] = "tool"
                }
            },
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["answer"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                    {
                        Enum = ["Allow once", "Refuse"]
                    }
                },
                Required = ["answer"]
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal("accept", result.Action);
        Assert.Empty(staleSignals);
        Assert.Equal([McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Resumed], activeSignals.Select(static signal => signal.Phase));
        Assert.Equal("active-run", provider.LastRequest!.RunId);
        Assert.Equal("active-step", provider.LastRequest.StepId);
    }

    [Fact]
    public async Task ElicitationHandler_UsesSoleActiveCallWhenExternalServerOmitsCorrelationMetadata()
    {
        var provider = new ScriptedHumanInputProvider(new JsonObject { ["response"] = "Allow once" });
        var activeCorrelation = new McpCorrelationContext
        {
            CorrelationId = "active-correlation",
            RunId = "active-run",
            StepId = "active-step",
            ServerName = "external-server",
            MethodName = "request_permission"
        };
        var signals = new List<McpHumanInputSignal>();
        using var activeScope = ConfiguredMcpClientFactory.PushHumanInputHandler(activeCorrelation, signals.Add);
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider, "external-server").Handlers!.ElicitationHandler!;

        var result = await handler(
            new ElicitRequestParams { Mode = "form", Message = "Allow?" },
            TestContext.Current.CancellationToken);

        Assert.Equal("accept", result.Action);
        Assert.Equal("active-run", provider.LastRequest!.RunId);
        Assert.Equal("active-step", provider.LastRequest.StepId);
        Assert.Equal([McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Resumed], signals.Select(static signal => signal.Phase));
    }

    [Fact]
    public async Task ElicitationHandler_RejectsAmbiguousUncorrelatedConcurrentCalls()
    {
        var provider = new ScriptedHumanInputProvider(new JsonObject { ["response"] = "Allow once" });
        var first = new McpCorrelationContext
        {
            CorrelationId = "first",
            RunId = "first-run",
            StepId = "first-step",
            ServerName = "external-server",
            MethodName = "request_permission"
        };
        var second = new McpCorrelationContext
        {
            CorrelationId = "second",
            RunId = "second-run",
            StepId = "second-step",
            ServerName = "external-server",
            MethodName = "request_permission"
        };
        using var firstScope = ConfiguredMcpClientFactory.PushHumanInputHandler(first, static _ => { });
        using var secondScope = ConfiguredMcpClientFactory.PushHumanInputHandler(second, static _ => { });
        var handler = ConfiguredMcpClientFactory.CreateClientOptions(provider, "external-server").Handlers!.ElicitationHandler!;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler(
            new ElicitRequestParams { Mode = "form", Message = "Allow?" },
            TestContext.Current.CancellationToken));

        Assert.Contains("without call correlation metadata", exception.Message, StringComparison.Ordinal);
        Assert.Null(provider.LastRequest);
    }

    [Fact]
    public async Task ElicitationHandler_RoundTripsOverBidirectionalMcpStreamsAndResumesToolCall()
    {
        var provider = new DeferredHumanInputProvider();
        var correlation = new McpCorrelationContext
        {
            CorrelationId = "stream-correlation",
            RunId = "stream-run",
            StepId = "stream-step",
            ServerName = "stream-server",
            MethodName = "request_permission",
            Kind = "tool"
        };
        var signals = new List<McpHumanInputSignal>();
        using var signalScope = ConfiguredMcpClientFactory.PushHumanInputHandler(correlation, signals.Add);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new McpStreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "stream-server");
        await using var server = McpProtocolServer.Create(serverTransport, new McpProtocolServerOptions
        {
            ServerInfo = new Implementation { Name = "stream-server", Version = "1.0.0" },
            ToolCollection =
            [
                McpProtocolServerTool.Create(
                    (Func<McpProtocolServer, CancellationToken, Task<string>>)RequestPermissionOverMcpAsync,
                    new McpProtocolServerToolCreateOptions { Name = "request_permission" })
            ]
        });
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serverTask = server.RunAsync(serverCancellation.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            ConfiguredMcpClientFactory.CreateClientOptions(provider),
            cancellationToken: TestContext.Current.CancellationToken);

        var callTask = client.CallToolAsync(
            "request_permission",
            arguments: null,
            progress: null,
            new RequestOptions
            {
                Meta = BuildCorrelationMeta(correlation)
            },
            TestContext.Current.CancellationToken);
        var request = await provider.RequestSeen.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(callTask.IsCompleted);
        Assert.Equal("stream-run", request.RunId);
        Assert.Equal("stream-step", request.StepId);
        provider.Response.TrySetResult(new JsonObject { ["response"] = "Allow once" });
        var result = await callTask;

        Assert.NotEqual(true, result.IsError);
        Assert.Equal([McpHumanInputSignalPhase.Waiting, McpHumanInputSignalPhase.Resumed], signals.Select(static signal => signal.Phase));

        serverCancellation.Cancel();
        await serverTask;
    }

    private static async Task<string> RequestPermissionOverMcpAsync(
        McpProtocolServer server,
        CancellationToken cancellationToken)
    {
        var trace = new McpCorrelationContext
        {
            CorrelationId = "stream-correlation",
            RunId = "stream-run",
            StepId = "stream-step",
            ServerName = "stream-server",
            MethodName = "request_permission",
            Kind = "tool"
        };
        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Mode = "form",
            Message = "Allow the streamed MCP operation?",
            Meta = BuildCorrelationMeta(trace),
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["answer"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                    {
                        Enum = ["Allow once", "Refuse"]
                    }
                },
                Required = ["answer"]
            }
        }, cancellationToken);
        return result.Action;
    }

    private static JsonObject BuildCorrelationMeta(McpCorrelationContext correlation)
        => new()
        {
            ["gnougo"] = new JsonObject
            {
                ["correlationId"] = correlation.CorrelationId,
                ["runId"] = correlation.RunId,
                ["stepId"] = correlation.StepId,
                ["stepType"] = "mcp.call",
                ["mcpServer"] = correlation.ServerName,
                ["mcpMethod"] = correlation.MethodName,
                ["mcpKind"] = correlation.Kind
            }
        };

    [Fact]
    public void IsUnexpectedServerExit_ReturnsTrue_ForNestedProcessExitMessage()
    {
        var ex = new InvalidOperationException(
            "outer",
            new Exception("MCP server process exited unexpectedly Server's stderr tail: ..."));

        Assert.True(InvokeIsUnexpectedServerExit(ex));
    }

    private sealed class ScriptedHumanInputProvider(JsonNode? response) : IHumanInputProvider
    {
        public HumanInputRequest? LastRequest { get; private set; }

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(response?.DeepClone());
        }
    }

    private sealed class CancellingHumanInputProvider : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
            => Task.FromCanceled<JsonNode?>(ct);
    }

    private sealed class DeferredHumanInputProvider : IHumanInputProvider
    {
        public TaskCompletionSource<HumanInputRequest> RequestSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonNode?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            RequestSeen.TrySetResult(request);
            return await Response.Task.WaitAsync(ct);
        }
    }

    private sealed class BlockingHumanInputProvider : IHumanInputProvider
    {
        public async Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
    }

    private sealed class SequencedHumanInputProvider(params JsonNode?[] responses) : IHumanInputProvider
    {
        private int _index;

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _index) - 1;
            return Task.FromResult(responses[index]?.DeepClone());
        }
    }

    [Theory]
    [InlineData("The pipe is broken.")]
    [InlineData("The connection is closed.")]
    [InlineData("Cannot access a disposed object.")]
    public void IsUnexpectedServerExit_ReturnsTrue_ForKnownDisconnectedTransportMessages(string message)
    {
        Assert.True(InvokeIsUnexpectedServerExit(new Exception(message)));
    }

    [Fact]
    public void IsUnexpectedServerExit_ReturnsFalse_ForUnrelatedErrors()
    {
        Assert.False(InvokeIsUnexpectedServerExit(new Exception("validation failed")));
    }

    [Fact]
    public void BuildContent_PrefersStructuredContent_WhenPresent()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                title = "Structured",
                count = 3
            }),
            Content = [new TextContentBlock { Text = "fallback text" }]
        };

        var content = Assert.IsType<JsonObject>(InvokeBuildContent(result));

        Assert.Equal("Structured", content["title"]!.GetValue<string>());
        Assert.Equal(3, content["count"]!.GetValue<int>());
    }

    [Fact]
    public void FormatMcpFailureDiagnostics_IncludesLaunchExceptionChainAndStderrTail()
    {
        const string serverName = "diagnostic-browser";
        InvokeCreateStdioTransport(serverName, new McpServerOptions
        {
            Type = "stdio",
            Command = "tools/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp",
            Args = ["--sample"]
        });
        InvokeCaptureStdioErrorLine(serverName, "first stderr line");
        InvokeCaptureStdioErrorLine(serverName, "fatal browser crash");

        var ex = new InvalidOperationException(
            "The server shut down unexpectedly.",
            new IOException("The pipe is broken."));

        var diagnostics = InvokeFormatMcpFailureDiagnostics(serverName, ex);

        Assert.Contains("The server shut down unexpectedly.", diagnostics);
        Assert.Contains("System.InvalidOperationException", diagnostics);
        Assert.Contains("System.IO.IOException", diagnostics);

        Assert.Contains("configuredCommand=tools/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp", diagnostics);
        Assert.Contains("command=", diagnostics);
        Assert.Contains("args=--sample", diagnostics);
        Assert.Contains("workingDirectory=", diagnostics);
        Assert.Contains("first stderr line", diagnostics);
        Assert.Contains("fatal browser crash", diagnostics);
    }

    [Fact]
    public void ResolveStdioCommand_PreservesBarePathCommand()
    {
        var resolution = InvokeResolveStdioCommand("dotnet", AppContext.BaseDirectory);

        Assert.Equal("dotnet", resolution.Command);
        Assert.Null(resolution.WorkingDirectory);
    }

    [Theory]
    [InlineData("GnOuGo.Browser.Mcp")]
    [InlineData("GnOuGo.Cmd.Mcp")]
    [InlineData("GnOuGo.Document.Mcp")]
    [InlineData("GnOuGo.GithubCopilot.Mcp")]
    public void ResolveStdioCommand_ResolvesRelativeBundledToolExecutable_ForAllBundledMcpTools(string toolName)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-stdio-command-" + Guid.NewGuid().ToString("N"));
        var toolDirectory = Path.Combine(root, "tools", toolName);
        Directory.CreateDirectory(toolDirectory);
        var executableName = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        var executable = Path.Combine(toolDirectory, executableName);
        File.WriteAllText(executable, string.Empty);

        try
        {
            var resolution = InvokeResolveStdioCommand($"tools/{toolName}/{toolName}", root);

            Assert.Equal(executable, resolution.Command);
            Assert.Equal(toolDirectory, resolution.WorkingDirectory);
            if (OperatingSystem.IsWindows())
                Assert.EndsWith(".exe", resolution.Command, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void ConvertArguments_PreservesJsonArraysAndNestedObjects()
    {
        var arguments = new JsonObject
        {
            ["name"] = "slimfaas",
            ["schedules"] = new JsonArray(),
            ["metadata"] = new JsonObject
            {
                ["enabled"] = true,
                ["tags"] = new JsonArray("web", "summary")
            }
        };

        var result = InvokeConvertArguments(arguments);

        Assert.NotNull(result);
        Assert.Equal("slimfaas", result["name"]);

        var schedules = Assert.IsType<List<object?>>(result["schedules"]);
        Assert.Empty(schedules);

        var metadata = Assert.IsType<Dictionary<string, object?>>(result["metadata"]);
        Assert.Equal(true, metadata["enabled"]);

        var tags = Assert.IsType<List<object?>>(metadata["tags"]);
        Assert.Equal(["web", "summary"], tags);
    }

    [Fact]
    public void ConvertArguments_KeepsScalarValuesTyped()
    {
        var arguments = new JsonObject
        {
            ["text"] = "hello",
            ["flag"] = true,
            ["count"] = 3,
            ["ratio"] = 0.5
        };

        var result = InvokeConvertArguments(arguments);

        Assert.NotNull(result);
        Assert.IsType<string>(result["text"]);
        Assert.IsType<bool>(result["flag"]);
        Assert.True(result["count"] is int or long);
        Assert.IsType<double>(result["ratio"]);
    }

    [Fact]
    public void BuildCurrentCorrelationMeta_IncludesTraceParentAndParentSpanId()
    {
        using var activity = new Activity("test-mcp-call");
        activity.SetParentId("00-00112233445566778899aabbccddeeff-0123456789abcdef-01");
        activity.Start();

        using var _ = ConfiguredMcpClientFactory.PushCorrelationContext(new McpCorrelationContext
        {
            CorrelationId = "corr-1",
            RunId = "run-1",
            StepId = "step-1",
            StepType = "mcp.call",
            ServerName = "GnOuGo.GithubCopilot.Mcp",
            MethodName = "code_suggest_change",
            Kind = "tool",
            TenantId = "tenant-1",
            ExecutionId = "execution-1",
            AgentId = "agent-1",
            AgentName = "Reviewer",
            Context = new JsonObject
            {
                ["workspace"] = "catalog-a",
                ["operationRevision"] = 42,
                ["labels"] = new JsonArray("priority", "batch")
            }
        });

        var meta = InvokeBuildCurrentCorrelationMeta();

        Assert.NotNull(meta);
        Assert.Equal(activity.Id, meta["traceparent"]!.GetValue<string>());
        var gnougo = Assert.IsType<JsonObject>(meta["gnougo"]);
        Assert.Equal("corr-1", gnougo["correlationId"]!.GetValue<string>());
        Assert.Equal(activity.TraceId.ToString(), gnougo["traceId"]!.GetValue<string>());
        Assert.Equal(activity.SpanId.ToString(), gnougo["spanId"]!.GetValue<string>());
        Assert.Equal(activity.ParentSpanId.ToString(), gnougo["parentSpanId"]!.GetValue<string>());
        Assert.Equal("tenant-1", gnougo["tenantId"]!.GetValue<string>());
        Assert.Equal("execution-1", gnougo["executionId"]!.GetValue<string>());
        Assert.Equal("agent-1", gnougo["agentId"]!.GetValue<string>());
        Assert.Equal("Reviewer", gnougo["agentName"]!.GetValue<string>());
        var context = Assert.IsType<JsonObject>(gnougo["context"]);
        Assert.Equal("catalog-a", context["workspace"]!.GetValue<string>());
        Assert.Equal(42, context["operationRevision"]!.GetValue<int>());
        Assert.Equal(2, Assert.IsType<JsonArray>(context["labels"]).Count);
        Assert.False(gnougo.ContainsKey("repository"));
        Assert.False(gnougo.ContainsKey("pullRequestNumber"));
        Assert.False(gnougo.ContainsKey("headSha"));
    }

    [Fact]
    public void TransportCorrelation_LeavesExplicitContextOutOfHeadersAndEnvironment()
    {
        var correlation = new McpCorrelationContext
        {
            CorrelationId = "corr-transport",
            TenantId = "tenant-transport",
            Context = new JsonObject
            {
                ["workspace"] = "catalog-a",
                ["operationRevision"] = 7
            }
        };
        using var request = new HttpRequestMessage();

        InvokeAddCorrelationHeaders(request, correlation);
        var environment = InvokeBuildCorrelationEnvironment(correlation);

        Assert.Equal("corr-transport", Assert.Single(request.Headers.GetValues("x-gnougo-correlation-id")));
        Assert.DoesNotContain(request.Headers, header => header.Key.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(request.Headers.SelectMany(header => header.Value), value => value.Contains("catalog-a", StringComparison.Ordinal));
        Assert.Equal("corr-transport", environment!["GNouGo__CorrelationId"]);
        Assert.DoesNotContain(environment, item => item.Key.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(environment.Values, value => value?.Contains("catalog-a", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void StdioEnvironment_IncludesOnlyNonSecretHostDefaultLlmIdentity()
    {
        var environment = InvokeBuildStdioEnvironment(
            new McpServerOptions
            {
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["SERVER_SETTING"] = "enabled"
                }
            },
            "OpenAi",
            "gpt-test");

        Assert.NotNull(environment);
        Assert.Equal("OpenAi", environment["GNouGo__DefaultLlmProvider"]);
        Assert.Equal("gpt-test", environment["GNouGo__DefaultLlmModel"]);
        Assert.Equal("enabled", environment["SERVER_SETTING"]);
        Assert.DoesNotContain(environment.Keys, static key =>
            key.Contains("key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveStdioWorkingDirectory_ReturnsExecutableDirectory_ForExistingCommandPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-stdio-working-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        File.WriteAllText(executable, string.Empty);

        try
        {
            var result = InvokeResolveStdioWorkingDirectory(executable);

            Assert.Equal(root, result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static bool InvokeIsUnexpectedServerExit(Exception ex)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "IsUnexpectedServerExit",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        var value = method.Invoke(null, new object[] { ex });
        Assert.NotNull(value);
        return (bool)value;
    }

    private static Dictionary<string, object?> InvokeConvertArguments(JsonNode arguments)
    {
        var adapterType = typeof(ConfiguredMcpClientFactory).Assembly.GetType("GnOuGo.Flow.Core.Runtime.McpSessionAdapter");
        Assert.NotNull(adapterType);

        var method = adapterType.GetMethod(
            "ConvertArguments",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var value = method.Invoke(null, [arguments]);
        return Assert.IsType<Dictionary<string, object?>>(value);
    }

    private static JsonNode? InvokeBuildContent(CallToolResult result)
    {
        var adapterType = typeof(ConfiguredMcpClientFactory).Assembly.GetType("GnOuGo.Flow.Core.Runtime.McpSessionAdapter");
        Assert.NotNull(adapterType);

        var method = adapterType.GetMethod(
            "BuildContent",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return method.Invoke(null, [result]) as JsonNode;
    }

    private static JsonObject? InvokeBuildCurrentCorrelationMeta()
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "BuildCurrentCorrelationMeta",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        return method.Invoke(null, []) as JsonObject;
    }

    private static void InvokeAddCorrelationHeaders(HttpRequestMessage request, McpCorrelationContext correlation)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "AddCorrelationHeaders",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, [request.Headers, correlation]);
    }

    private static Dictionary<string, string?>? InvokeBuildCorrelationEnvironment(McpCorrelationContext correlation)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "BuildCorrelationEnvironment",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, [correlation]) as Dictionary<string, string?>;
    }

    private static Dictionary<string, string?>? InvokeBuildStdioEnvironment(
        McpServerOptions options,
        string? defaultLlmProvider,
        string? defaultLlmModel)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "BuildStdioEnvironment",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, [options, null, defaultLlmProvider, defaultLlmModel]) as Dictionary<string, string?>;
    }

    private static string? InvokeResolveStdioWorkingDirectory(string command)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "ResolveStdioWorkingDirectory",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        return (string?)method.Invoke(null, [command]);
    }

    private static (string Command, string? WorkingDirectory) InvokeResolveStdioCommand(string command, string baseDirectory)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(m => m.Name == "ResolveStdioCommand" && m.GetParameters().Length == 2);

        var value = method.Invoke(null, [command, baseDirectory]);
        Assert.NotNull(value);

        var type = value.GetType();
        var commandProperty = type.GetProperty("Command");
        var workingDirectoryProperty = type.GetProperty("WorkingDirectory");
        Assert.NotNull(commandProperty);
        Assert.NotNull(workingDirectoryProperty);

        return (
            Assert.IsType<string>(commandProperty.GetValue(value)),
            (string?)workingDirectoryProperty.GetValue(value));
    }

    private static void InvokeCreateStdioTransport(string serverName, McpServerOptions options)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "CreateStdioTransport",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(null, [serverName, options, null, null, null]);
    }

    private static void InvokeCaptureStdioErrorLine(string serverName, string line)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "CaptureStdioErrorLine",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(null, [serverName, line]);
    }

    private static string InvokeFormatMcpFailureDiagnostics(string serverName, Exception exception)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "FormatMcpFailureDiagnostics",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        var value = method.Invoke(null, [serverName, exception]);
        return Assert.IsType<string>(value);
    }
}
