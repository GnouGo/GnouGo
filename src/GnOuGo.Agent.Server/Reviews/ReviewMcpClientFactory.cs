using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Reviews;

/// <summary>Host integration: original producer evidence enters here; review writes leave only through the publisher.</summary>
internal sealed class ReviewMcpClientFactory(IMcpClientFactory inner, ReviewPublicationService reviews) : IMcpClientFactory, IMcpExecutionHooks, IAsyncDisposable
{
    internal const string Server = "GnOuGo.Review";
    private readonly AsyncLocal<McpCallExecutionContext?> _call = new();
    private IMcpClientFactory Inner => inner;
    private ReviewPublicationService Reviews => reviews;
    private bool Enabled => inner.ServerMetadata.Any(s => s.Name.Equals(reviews.Settings.GitHubServer, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<McpServerMetadata> ServerMetadata => Enabled
        ? [.. inner.ServerMetadata, new() { Name = Server, Description = "Evaluate requested review checks and publish the stored review after separate human confirmation and a fresh head check." }]
        : inner.ServerMetadata;

    public async Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
    {
        if (serverName == Server)
        {
            if (!Enabled) throw new InvalidOperationException("Configure the host's GitHub MCP integration before publishing reviews.");
            return new ReviewSession(this);
        }
        return new ObservedSession(this, await inner.GetClientAsync(serverName, ct));
    }
    public IDisposable BeginCall(McpCallExecutionContext context)
    {
        var previous = _call.Value; _call.Value = context;
        var nested = (inner as IMcpExecutionHooks)?.BeginCall(context);
        return new Scope(() => { nested?.Dispose(); _call.Value = previous; });
    }
    public string FormatFailureDiagnostics(string serverName, Exception exception) => (inner as IMcpExecutionHooks)?.FormatFailureDiagnostics(serverName, exception) ?? exception.Message;
    public async ValueTask DisposeAsync() { if (inner is IAsyncDisposable disposable) await disposable.DisposeAsync(); }

    private (string Run, string Step) Context()
    {
        var correlation = _call.Value?.Correlation;
        var run = correlation?.ExecutionId ?? correlation?.RunId;
        if (correlation?.TenantId != reviews.Tenant || string.IsNullOrWhiteSpace(run))
            throw new InvalidOperationException("Review operations require the host execution tenant and a stable run identity.");
        return (run, correlation?.StepId ?? "review_publication");
    }
    private bool Allowed(string server, McpToolInfo tool) => !Enabled ||
        (server.Equals(reviews.Settings.GitHubServer, StringComparison.OrdinalIgnoreCase) ? tool.EffectKind is "read" or "none" :
         !server.Equals(reviews.Settings.CopilotServer, StringComparison.OrdinalIgnoreCase) || tool.Name != "copilot_review_publication_gate");

    private sealed class ObservedSession(ReviewMcpClientFactory owner, IMcpSession session) : IMcpSession, ILiveMcpToolDiscoverySession
    {
        public string ServerName => session.ServerName;
        public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => (await session.ListToolsAsync(ct)).Where(t => owner.Allowed(ServerName, t)).ToArray();
        public async Task<IReadOnlyList<McpToolInfo>> EnsureToolsDiscoveredAsync(CancellationToken ct) => session is ILiveMcpToolDiscoverySession live
            ? (await live.EnsureToolsDiscoveredAsync(ct)).Where(t => owner.Allowed(ServerName, t)).ToArray() : await ListToolsAsync(ct);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => session.ListResourcesAsync(ct);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => session.ListPromptsAsync(ct);
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? args, CancellationToken ct) => session.GetPromptAsync(name, args, ct);
        public async Task<McpCallResult> CallToolAsync(string name, JsonNode? args, CancellationToken ct)
        {
            if (owner.Enabled && ServerName.Equals(owner.Reviews.Settings.GitHubServer, StringComparison.OrdinalIgnoreCase) &&
                !(await EnsureToolsDiscoveredAsync(ct)).Any(t => t.Name == name))
                throw new InvalidOperationException("This GitHub integration exposes declared reads only. Use GnOuGo.Review review_evaluate, then review_publish for review writes.");
            if (ServerName.Equals(owner.Reviews.Settings.CopilotServer, StringComparison.OrdinalIgnoreCase) && name == "copilot_review_publication_gate")
                throw new InvalidOperationException("The caller-controlled publication gate has been removed.");
            var result = await session.CallToolAsync(name, args, ct);
            if (!result.IsError && ServerName.Equals(owner.Reviews.Settings.CopilotServer, StringComparison.OrdinalIgnoreCase) && owner._call.Value is not null)
                await owner.Reviews.CaptureAsync(owner.Context().Run, name, ReviewPublicationService.Payload(result.Content), ct);
            return result;
        }
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private sealed class ReviewSession(ReviewMcpClientFactory owner) : IMcpSession
    {
        public string ServerName => Server;
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>([
            Tool("review_evaluate", "Create a stored review draft. Declare every requested check separately with its exact expected command arguments. Pass unchanged original Copilot review and tool execution observations from this run. Missing checks and unobserved commands remain blocked; never group several requested checks into one result. WorkingDirectory is the absolute directory from the original execution observations.", "none",
                ReviewPublicationJsonContext.Default.ReviewDraftRequest, ReviewPublicationJsonContext.Default.ReviewDraft),
            Tool("review_publish", "Publish the exact stored draft. The host displays its content and obtains separate human confirmation, then re-reads the PR head and submits once. No caller approval boolean, event, body, head override or transport target is accepted. Uncertain dispatches stop without retrying; inspect status. Does not merge or deploy.", "write",
                ReviewPublicationJsonContext.Default.ReviewPublishRequest, ReviewPublicationJsonContext.Default.ReviewPublishResult)
        ]);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? args, CancellationToken ct) => throw new NotSupportedException();
        public async Task<McpCallResult> CallToolAsync(string name, JsonNode? args, CancellationToken ct)
        {
            var context = owner.Context();
            if (name == "review_evaluate")
            {
                var request = JsonSerializer.Deserialize(args, ReviewPublicationJsonContext.Default.ReviewDraftRequest) ?? throw new ArgumentException("Review arguments are required.");
                var result = await owner.Reviews.EvaluateAsync(context.Run, request, ct);
                return new() { Content = JsonSerializer.SerializeToNode(result, ReviewPublicationJsonContext.Default.ReviewDraft) };
            }
            if (name == "review_publish")
            {
                if (args is not JsonObject fields || fields.Count != 1 || fields["draftId"] is null)
                    throw new ArgumentException("Only the stored draftId may be supplied to publication.");
                var request = JsonSerializer.Deserialize(args, ReviewPublicationJsonContext.Default.ReviewPublishRequest)!;
                await using var github = await owner.Inner.GetClientAsync(owner.Reviews.Settings.GitHubServer, ct);
                if (github is ILiveMcpToolDiscoverySession live) await live.EnsureToolsDiscoveredAsync(ct);
                var result = await owner.Reviews.PublishAsync(context.Run, context.Step, request.DraftId, github, ct, call: owner._call.Value);
                return new() { Content = JsonSerializer.SerializeToNode(result, ReviewPublicationJsonContext.Default.ReviewPublishResult) };
            }
            throw new ArgumentException("Unknown review operation.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static McpToolInfo Tool(string name, string description, string effect, JsonTypeInfo input, JsonTypeInfo output)
        {
            var schema = output.GetJsonSchemaAsNode();
            return new() { Name = name, Description = description, EffectKind = effect, InputSchema = input.GetJsonSchemaAsNode(), OutputSchema = schema,
                ArtifactContract = new(new(1,
                    name == "review_evaluate" ? [new("review.draft", "/draftId", "materialize")] : [],
                    name == "review_publish" ? [new("review.draft", "/draftId", true)] : []), []),
                OutputContract = new(schema.DeepClone(), McpOutputContractSources.ProtocolSchema, true, []) };
        }
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
