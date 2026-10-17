using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.SmartFlow;

public sealed partial class ConfigureProvidersService
{
    private const string CopilotServerName = "GnOuGo.GithubCopilot.Mcp";
    private const string AllowAgentPermissions = "Allow All including sandbox bypass";
    private const string RemoveAgentPermissions = "Remove persistent approval";

    private async IAsyncEnumerable<SmartFlowEvent> EditCopilotAgentPermissionsAsync(
        string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        var listed = await CallConfigurationToolAsync("GnOuGo.Agent.Mcp", "agent_list", null, runId, ct);
        if (listed["success"]?.GetValue<bool>() != true || listed["agents"] is not JsonArray agents)
        {
            yield return new("answer", "❌ Could not load agents. Permission grants were not changed.");
            yield break;
        }
        var choices = agents.OfType<JsonObject>()
            .Where(a => !string.IsNullOrWhiteSpace(a["id"]?.ToString()) && !string.IsNullOrWhiteSpace(a["name"]?.ToString()))
            .Select(a => new { Id = a["id"]!.ToString(), Name = a["name"]!.ToString() })
            .DistinctBy(a => a.Id).ToDictionary(a => $"{a.Name} ({a.Id})", StringComparer.Ordinal);
        if (choices.Count == 0)
        {
            yield return new("answer", "No agents are available. Create an agent before configuring persistent approval.");
            yield break;
        }
        JsonNode? response = null;
        var select = CreateChoiceRequest(runId, "mcp_edit.permission_agent", "Select the agent whose Copilot permissions you want to configure:",
            choices.Keys.Order(StringComparer.Ordinal).Append("Cancel").ToArray());
        await foreach (var evt in EmitHumanInputRequestAsync(select, value => response = value, ct)) yield return evt;
        if (!choices.TryGetValue(ReadChoiceResponse(response) ?? "", out var agent)) yield break;

        var grants = await CallConfigurationToolAsync(CopilotServerName, "copilot_permission_grants_list", null, runId, ct);
        if (grants["success"]?.GetValue<bool>() != true || grants["grants"] is not JsonArray saved)
        {
            yield return new("answer", "❌ Could not inspect the current permission grant. Permission grants were not changed.");
            yield break;
        }
        var current = saved.OfType<JsonObject>().SingleOrDefault(g => g["agentId"]?.ToString() == agent.Id && g["tenantId"]?.ToString() == _tenantId);
        var currentPolicy = current is null ? "No persistent approval" : current["allowSandboxBypass"]?.GetValue<bool>() == true
            ? "Allow All including sandbox bypass" : "Allow All; sandbox bypass still requires approval";
        var policy = CreateChoiceRequest(runId, "mcp_edit.permission_policy", $"Copilot permissions for {agent.Name}",
            ["Keep current", AllowAgentPermissions, RemoveAgentPermissions],
            JsonValue.Create($"Current: {currentPolicy}. Applies only to agent {agent.Id} in tenant {_tenantId}. Host restrictions remain enforced."));
        await foreach (var evt in EmitHumanInputRequestAsync(policy, value => response = value, ct)) yield return evt;
        var action = ReadChoiceResponse(response);
        if (action is not (AllowAgentPermissions or RemoveAgentPermissions)) yield break;

        // Recheck the tenant-owned ID rather than resolving the displayed name again.
        var rechecked = await CallConfigurationToolAsync("GnOuGo.Agent.Mcp", "agent_list", null, runId, ct);
        if (rechecked["success"]?.GetValue<bool>() != true || rechecked["agents"] is not JsonArray remaining
            || !remaining.OfType<JsonObject>().Any(a => a["id"]?.ToString() == agent.Id))
        {
            yield return new("answer", "❌ The selected agent is no longer available. Permission grants were not changed.");
            yield break;
        }
        if (action == RemoveAgentPermissions)
        {
            var revoked = await CallConfigurationToolAsync(CopilotServerName, "copilot_permission_grants_revoke_agent",
                new() { ["agentId"] = agent.Id }, runId, ct);
            yield return new("answer", revoked["success"]?.GetValue<bool>() == true
                ? $"✅ Persistent Copilot approval removed for {agent.Name}. Other agents' grants are unchanged."
                : "❌ Could not remove persistent approval. Inspect the existing grant before retrying.");
            yield break;
        }

        // These are host gates, not grants. Persist even when the editor displayed a default.
        foreach (var name in new[] { "EnableApproveAll", "EnableSandboxBypassGrants" })
            await _keyVaultStore.SaveSecretValueAsync($"LLM--McpServerOverrides--{CopilotServerName}--Code--Copilot--{name}", "true", ct);
        _optionsStore.ReplaceRuntimeOptions(await _keyVaultStore.BuildEffectiveOptionsAsync(_optionsStore.Current, ct));

        var events = Channel.CreateUnbounded<SmartFlowEvent>();
        var creation = CreateGrantAsync();
        await foreach (var evt in events.Reader.ReadAllAsync(ct)) yield return evt;
        var result = await creation;
        if (result["success"]?.GetValue<bool>() != true)
        {
            yield return new("answer", "Copilot host gates were enabled, but persistent approval was not saved: "
                + (result["errorMessage"]?.ToString() ?? "the operation failed."));
            yield break;
        }
        var verified = await CallConfigurationToolAsync(CopilotServerName, "copilot_permission_grants_list", null, runId, ct);
        var active = verified["success"]?.GetValue<bool>() == true && verified["grants"] is JsonArray verifiedGrants
            && verifiedGrants.OfType<JsonObject>().Any(g => g["id"]?.ToString() == result["grantId"]?.ToString()
                && g["tenantId"]?.ToString() == _tenantId && g["agentId"]?.ToString() == agent.Id && g["allowSandboxBypass"]?.GetValue<bool>() == true);
        yield return new("answer", active
            ? $"✅ Allow All including sandbox bypass is saved for {agent.Name} ({agent.Id}). Future runs approve permissions from the first request. Host restrictions and task boundaries remain enforced. Revoke through this editor or `/mcp copilot permissions revoke {result["grantId"]}`."
            : "❌ The grant operation returned, but its saved state could not be verified. Inspect `/mcp copilot permissions` before retrying.");

        async Task<JsonObject> CreateGrantAsync()
        {
            try
            {
                return await CallConfigurationToolAsync(CopilotServerName, "copilot_permission_grant_create",
                    new() { ["agentId"] = agent.Id, ["agentName"] = agent.Name, ["allowSandboxBypass"] = true }, runId, ct,
                    signal =>
                    {
                        if (signal.Phase == McpHumanInputSignalPhase.Waiting)
                            events.Writer.TryWrite(new("human_input_request", BuildHumanInputPayload(signal.Request).ToJsonString()));
                    });
            }
            finally { events.Writer.TryComplete(); }
        }
    }

    private async Task<JsonObject> CallConfigurationToolAsync(string server, string method, JsonObject? arguments,
        string runId, CancellationToken ct, Action<McpHumanInputSignal>? onHuman = null)
    {
        try
        {
            var options = await _keyVaultStore.BuildEffectiveOptionsAsync(_optionsStore.Current, ct);
            // An injected factory is a test/host override. Production opens a fresh process
            // after configuration changes, so no cached startup policy can authorize a grant.
            await using var owned = _mcpFactory is null ? new ConfiguredMcpClientFactory(options.McpServers, _humanInput,
                options.DefaultProvider, options.DefaultModel) : null;
            var correlation = new McpCorrelationContext { TenantId = _tenantId, CorrelationId = runId, RunId = runId,
                StepId = "configure/" + method, ServerName = server, MethodName = method, Kind = "tool" };
            using var context = ConfiguredMcpClientFactory.PushCorrelationContext(correlation);
            using var callbacks = ConfiguredMcpClientFactory.PushHumanInputHandler(correlation, onHuman ?? (_ => { }));
            await using var session = await (_mcpFactory ?? owned!).GetClientAsync(server, ct);
            var result = await session.CallToolAsync(method, arguments, ct);
            if (result.Content is JsonObject body && (!result.IsError || body["success"]?.GetValue<bool>() == false)) return body;
            return new() { ["success"] = false, ["errorMessage"] = "The MCP management operation failed." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP configuration operation {Method} failed.", method);
            return new() { ["success"] = false, ["errorMessage"] = "The MCP management operation is unavailable. See host diagnostics." };
        }
    }
}
