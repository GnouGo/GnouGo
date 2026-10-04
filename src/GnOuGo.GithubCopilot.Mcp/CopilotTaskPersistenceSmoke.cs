using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

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
        Console.WriteLine("{\"encrypted_task_recovery\":true,\"tenant_isolation\":true,\"unknown_completion_stopped\":true,\"model_calls\":0}");
    }
}
