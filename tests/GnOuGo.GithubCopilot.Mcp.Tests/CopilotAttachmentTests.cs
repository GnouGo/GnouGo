using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Mcp.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

[Collection("Copilot tool discovery")]
public sealed class CopilotAttachmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ConcurrentPermissionCallbacksPreserveIndividualAnswersAndSerializeElicitation()
    {
        var active = 0; var completed = 0;
        await using var fixture = await Fixture.CreateAsync(new McpClientOptions
        {
            Handlers = new()
            {
                ElicitationHandler = async (request, ct) =>
                {
                    if (Interlocked.Increment(ref active) != 1)
                        throw new InvalidOperationException("Concurrent client requests are forbidden by this transport.");
                    try
                    {
                        await Task.Delay(50, ct);
                        Interlocked.Increment(ref completed);
                        return new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement>()
                        { ["answer"] = JsonSerializer.SerializeToElement(request!.Message == "permission-0" ? "Refuse" : "Allow once") } };
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
            }
        });
        fixture.Host.OnSend = async (configuration, handle, _, ct) =>
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => configuration.HumanInputProvider!.RequestAsync(
                new(new("fixture-tenant"), "permission", "permission-" + i, ["Allow once", "Refuse"], false), ct)));
            Assert.False(answers[0].Accepted); Assert.Equal("Refuse", answers[0].Answer);
            Assert.All(answers.Skip(1), answer => { Assert.True(answer.Accepted); Assert.Equal("Allow once", answer.Answer); });
            return new(handle, "mock-session", "answers retained", "mock", []);
        };
        var result = await fixture.Call("copilot_interactive_one_shot", new() { ["projectRoot"] = "workflows/project", ["prompt"] = "Concurrent permissions" }, "fixture-tenant");
        Assert.False(result.IsError == true, JsonSerializer.Serialize(result.Content));
        Assert.Equal(8, completed);
    }

    public static TheoryData<string> InvalidAttachments => new()
    {
        "{}", "\"[]\"", "[null]", "[true]", "[{}]", "[{\"type\":\"FILE\",\"path\":\"readme.md\"}]",
        "[{\"type\":\"file\"}]", "[{\"type\":\"file\",\"path\":null}]", "[{\"type\":\"file\",\"path\":\" \"}]",
        "[{\"type\":\"file\",\"path\":1}]", "[{\"type\":\"file\",\"path\":\"x\",\"content\":\"eA==\"}]",
        "[{\"type\":\"blob\"}]", "[{\"type\":\"blob\",\"content\":null}]",
        "[{\"type\":\"blob\",\"content\":\"private-invalid-base64\"}]",
        "[{\"type\":\"blob\",\"content\":\"eA==\\n\"}]",
        "[{\"type\":\"blob\",\"content\":\"eA==\",\"mimeType\":1}]",
        "[{\"type\":\"blob\",\"content\":\"eA==\",\"private-extra-field\":true}]"
    };

    [Theory]
    [MemberData(nameof(InvalidAttachments))]
    public async Task InvalidAttachmentsAreRejectedByDiscoveryAndTransportBeforeAnySession(string json)
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var name in Fixture.Methods)
        {
            var schema = fixture.Schemas[name]["properties"]!["attachments"]!;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse(json), schema));
            var result = await fixture.Call(name, new() { ["attachments"] = JsonSerializer.Deserialize<JsonElement>(json) });
            Assert.True(result.IsError);
            Assert.Equal("INVALID_INPUT", result.StructuredContent!.Value.GetProperty("code").GetString());
            var message = result.StructuredContent.Value.GetProperty("message").GetString()!;
            Assert.StartsWith("attachments", message);
            Assert.DoesNotContain("private", message); Assert.DoesNotContain("System.", message);
            Assert.DoesNotContain("BytePosition", message);
        }
        Assert.Equal(0, fixture.Host.SessionsCreated); Assert.Equal(0, fixture.Host.Sends);
    }

    [Fact]
    public async Task RemovedArgumentIsRejectedEvenWhenNullAndBeforeMissingTenantOrHandle()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var name in Fixture.Methods)
        foreach (var legacy in new object?[] { null, "[]", "{}" })
        {
            Assert.Null(fixture.Schemas[name]["properties"]?["attachmentsJson"]);
            var result = await fixture.Call(name, new() { ["attachmentsJson"] = legacy });
            Assert.True(result.IsError);
            Assert.Contains("attachmentsJson: This argument was removed", result.StructuredContent!.Value.GetProperty("message").GetString());
        }
        Assert.Equal(0, fixture.Host.SessionsCreated); Assert.Equal(0, fixture.Host.Sends);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"path\":\"docs/Ünicode file.md\",\"type\":\"file\"},{\"mimeType\":\"text/plain\",\"content\":\"aGVsbG8=\",\"type\":\"blob\",\"path\":\"note.txt\"}]")]
    [InlineData("[{\"type\":\"blob\",\"content\":\"\",\"path\":null,\"mimeType\":null}]")]
    public async Task TypedInputsReachOnlyMockedSdkAndRetainOrderDefaultsAndTenant(string? json)
    {
        await using var fixture = await Fixture.CreateAsync();
        var args = new Dictionary<string, object?> { ["projectRoot"] = "workflows/project", ["prompt"] = "Inspect supplied data only" };
        if (json is not null) args["attachments"] = JsonSerializer.Deserialize<JsonElement>(json);
        foreach (var name in Fixture.Methods)
        {
            if (json is not null) Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse(json), fixture.Schemas[name]["properties"]!["attachments"]!));
            if (name == "copilot_session_send")
            {
                var session = await fixture.Host.Manager.CreateAsync(new(new("fixture-tenant"),
                    new(fixture.Project, "mock") { UseSessionFileSystem = true }, CopilotSessionKind.Managed, CopilotPermissionMode.Deny), Ct);
                args.Remove("projectRoot"); args["handle"] = session.Handle;
            }
            var result = await fixture.Call(name, args, tenant: "fixture-tenant");
            Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            Assert.Equal("fixture-tenant", fixture.Host.LastRequest!.Context.TenantId);
            var expected = json is null ? null : CopilotAttachmentContract.ToCore(JsonSerializer.Deserialize(json, CodeMcpJsonContext.Default.IReadOnlyListCopilotAttachmentInput));
            Assert.Equal(expected, fixture.Host.LastRequest.Attachments);
        }
        Assert.Equal(3, fixture.Host.Sends); Assert.Equal(3, fixture.Host.SessionsCreated);
        Assert.Equal(2, fixture.Host.DisposedSessions); Assert.Single(fixture.Host.Manager.List("fixture-tenant"));
    }

    [Fact]
    public async Task SendFailureCancellationAndTenantRefusalKeepExistingLifecycle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var args = new Dictionary<string, object?> { ["projectRoot"] = "workflows/project", ["prompt"] = "mock" };
        var missing = await fixture.Call("copilot_one_shot", args);
        Assert.True(missing.IsError); Assert.Equal(0, fixture.Host.SessionsCreated);
        foreach (var cancelled in new[] { false, true })
        {
            fixture.Host.OnSend = (_, _, _, _) => throw (cancelled ? new OperationCanceledException("Mocked cancellation") : new UnauthorizedAccessException("Mocked permission refusal"));
            var result = await fixture.Call("copilot_one_shot", args, "fixture-tenant");
            Assert.True(result.IsError); // SDK effects are mocked; one-shot finally still deletes the managed session.
        }
        Assert.Equal(2, fixture.Host.Sends); Assert.Equal(2, fixture.Host.DisposedSessions);
        Assert.Empty(fixture.Host.Manager.List("fixture-tenant"));
        var owned = await fixture.Host.Manager.CreateAsync(new(new("fixture-tenant"), new(fixture.Project, "mock"), CopilotSessionKind.Managed, CopilotPermissionMode.Deny), Ct);
        var other = await fixture.Call("copilot_session_send", new() { ["handle"] = owned.Handle, ["prompt"] = "mock" }, "other-tenant");
        Assert.True(other.IsError); Assert.Equal(2, fixture.Host.Sends);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly string[] Methods = ["copilot_one_shot", "copilot_interactive_one_shot", "copilot_session_send"];
        internal readonly string Root = Directory.CreateTempSubdirectory("attachment-contract-").FullName;
        internal string Project => Path.Combine(Root, "workflows", "project");
        internal CopilotTestHost Host = null!;
        internal readonly Dictionary<string, JsonNode> Schemas = [];
        private McpServer server = null!; private McpClient client = null!; private Task running = null!;
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        internal static async Task<Fixture> CreateAsync(McpClientOptions? clientOptions = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Project);
            var settings = new CodeServerSettings { DefaultWorkingDirectory = f.Root, AllowedWorkingRoots = [f.Root], Copilot = new() { Model = "mock" } };
            var policy = new CodePolicy(settings, f.Root); var options = Options.Create(settings);
            f.Host = new(settings, f.Root, policy);
            f.Host.OnSend = (_, handle, _, _) => Task.FromResult(new CopilotSendResult(handle, "mock-session", "mock result", "mock", []));
            var target = new CopilotTools(f.Host.Manager, new(f.Host.Manager), policy, options, f.Host.Trace, f.Host.Human,
                new(f.Host.Trace), null!, new(policy, options, f.Host.Trace));
            var serverOptions = new McpServerOptions { ServerInfo = new() { Name = "attachments-fixture", Version = "1" }, ToolCollection = [] };
            serverOptions.AddGnOuGoToolErrorNormalizer(); CopilotAttachmentContract.Configure(serverOptions);
            serverOptions.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
            {
                using var scope = f.Host.Trace.Push(CodeMcpTraceContext.FromMcpMeta(request.Params.Meta));
                return await next(request, ct);
            });
            foreach (var method in typeof(CopilotTools).GetMethods().Where(m => Methods.Contains(m.GetCustomAttribute<McpServerToolAttribute>()?.Name)))
                serverOptions.ToolCollection.Add(McpServerTool.Create(method, target, new() { SerializerOptions = CodeMcpJson.SerializerOptions }));
            var incoming = new Pipe(); var outgoing = new Pipe();
            f.server = McpServer.Create(new StreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream()), serverOptions);
            f.running = f.server.RunAsync(f.stop.Token);
            f.client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()), clientOptions, cancellationToken: Ct);
            foreach (var tool in await f.client.ListToolsAsync(cancellationToken: Ct)) f.Schemas.Add(tool.Name, JsonNode.Parse(tool.JsonSchema.GetRawText())!);
            return f;
        }

        internal ValueTask<CallToolResult> Call(string name, Dictionary<string, object?> args, string? tenant = null)
            => client.CallToolAsync(name, args, progress: null, new RequestOptions { Meta = tenant is null ? null : new JsonObject { ["gnougo"] = new JsonObject { ["tenantId"] = tenant } } }, Ct);

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync(); await stop.CancelAsync(); await running; await server.DisposeAsync();
            await Host.Manager.DisposeAsync(); stop.Dispose(); Directory.Delete(Root, true);
        }
    }
}
