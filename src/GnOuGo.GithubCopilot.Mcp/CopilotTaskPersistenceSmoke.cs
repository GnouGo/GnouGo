using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

/// <summary>Published-binary encrypted task serialization check; no SDK or inference is created.</summary>
internal static class CopilotTaskPersistenceSmoke
{
    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        var path = Path.Combine(root, "vault.db"); var leases = Path.Combine(root, "leases");
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(path, root);
        var trace = new CodeMcpTraceContextAccessor();
        using var tenant = trace.Push(CodeMcpTraceContext.FromMcpMeta(new() { ["gnougo"] = new JsonObject { ["tenantId"] = "task-persistence-smoke" } }));
        string completedId, abandonedId;
        await using (var owner = new KeyVaultCopilotTaskStore(records, trace, leases))
        {
            using var invocation = owner.BeginInvocation(); completedId = (await owner.CreateTaskAsync(ct)).TaskId;
            await owner.UpdateOperationAsync(completedId, value => { value["objective"] = "private-task-smoke"; value["reservedTokens"] = 1234; }, ct);
            using var input = JsonDocument.Parse("{\"message\":\"Choose\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{}}}");
            await owner.SetInputRequestsAsync(completedId, new Dictionary<string, InputRequest>
                { ["question"] = new() { Method = "elicitation/create", Params = input.RootElement.Clone() } }, ct);
            using var answer = JsonDocument.Parse("{\"action\":\"decline\"}");
            await owner.ResolveInputRequestsAsync(completedId, new Dictionary<string, InputResponse> { ["question"] = new() { RawValue = answer.RootElement.Clone() } }, ct);
            using var result = JsonDocument.Parse("{\"content\":[],\"isError\":false}");
            await owner.SetCompletedAsync(completedId, result.RootElement, ct);
            abandonedId = (await owner.CreateTaskAsync(ct)).TaskId;
        }
        await using var recovered = new KeyVaultCopilotTaskStore(KeyVaultRecordStoreFactory.CreateWorkspaceStore(path, root), trace, leases);
        if ((await recovered.GetTaskAsync(completedId, ct))?.Status != McpTaskStatus.Completed ||
            (await recovered.GetTaskAsync(abandonedId, ct))?.Status != McpTaskStatus.Failed ||
            (await recovered.ReadOperationAsync(completedId, ct))?["reservedTokens"]?.GetValue<int>() != 1234 ||
            !(await recovered.AnswersAsync(completedId, ct)).ToJsonString().Contains("decline", StringComparison.Ordinal))
            throw new InvalidOperationException("Published task recovery failed.");
        using (trace.Push(CodeMcpTraceContext.FromMcpMeta(new() { ["gnougo"] = new JsonObject { ["tenantId"] = "different-tenant" } })))
            if (await recovered.GetTaskAsync(completedId, ct) is not null) throw new InvalidOperationException("Task tenant isolation failed.");
        if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct)).Contains("private-task-smoke", StringComparison.Ordinal))
            throw new InvalidOperationException("Task encryption failed.");
        using var wireRequest = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
        { Content = new StringContent("""{"model":"configured","reasoning_effort":"high","messages":[{"role":"user","content":"controlled smoke"}],"tools":[{"type":"function","function":{"name":"probe","parameters":{"type":"object"}}}]}""") };
        using var prepared = await new CopilotStructuredToolAdapter().PrepareAsync(wireRequest, "published-smoke", ct);
        var wire = JsonNode.Parse(await prepared.Request.Content!.ReadAsStringAsync(ct))!;
        using var providerResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"action\":{\"kind\":\"tools\",\"calls\":[{\"name\":\"probe\",\"arguments\":\"{}\"}]}}"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""") };
        using var sdkResponse = await prepared.RestoreResponseAsync(providerResponse, ct);
        var decoded = JsonNode.Parse(await sdkResponse.Content.ReadAsStringAsync(ct))!;
        var capture = JsonSerializer.SerializeToNode(new CopilotRuntimeConfiguration(root, "configured")
            { InferenceTransport = CopilotStructuredToolAdapter.TransportName }, CopilotCoreJsonContext.Default.CopilotRuntimeConfiguration)!;
        if (wire["tools"] is not null || wire["response_format"]?["type"]?.ToString() != "json_schema" ||
            decoded["choices"]?[0]?["message"]?["tool_calls"]?[0]?["function"]?["name"]?.ToString() != "probe" ||
            capture.Deserialize(CopilotCoreJsonContext.Default.CopilotRuntimeConfiguration)?.InferenceTransport != CopilotStructuredToolAdapter.TransportName)
            throw new InvalidOperationException("Published structured wire adaptation failed.");
        Console.WriteLine("{\"encrypted_task_recovery\":true,\"tenant_isolation\":true,\"unknown_completion_stopped\":true,\"controlled_structured_wire_roundtrip\":true,\"model_calls\":0}");
    }
}
