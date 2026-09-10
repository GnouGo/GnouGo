using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;

internal static class RuntimePersistenceSmoke
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-planning-native-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = new Client();
            WorkflowPlanningRuntimeFactory Factory() => WorkflowPlanningRuntimeFactory.CreateWorkspace(Path.Combine(directory, "keyvault.db"), Path.Combine(directory, "leases"));
            StepExecutionContext Context() => new()
            {
                Engine = new WorkflowEngine { LLMClient = client },
                Data = new(),
                Step = new() { Source = new StepDef { Id = "plan", Type = "workflow.plan" } },
                Limits = new() { RunId = "native", TenantId = "smoke" }
            };
            PlanningSnapshot Initial() => new() { Request = new() { TenantId = "smoke", Prompt = "private native intent" } };
            LLMRequest request;
            await using (var session = await Factory().OpenAsync(Context(), Initial(), CancellationToken.None))
            {
                request = PlanningGenerationPolicy.Apply(new()
                {
                    Model = "smoke",
                    Prompt = "private native intent",
                    StructuredOutputStrict = true,
                    StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")
                }, new());
                request.ClientRequestId = session.Snapshot.Request.SessionId + ":1:intent:workflow:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                session.Snapshot.Construction.PendingCalls.Add(new() { Id = request.ClientRequestId, Request = request, Phase = "intent" });
                await session.Runtime.CheckpointAsync(session.Snapshot, CancellationToken.None);
                await session.Runtime.CallAsync(request, "intent", CancellationToken.None);
            }
            await using (var resumed = await Factory().OpenAsync(Context(), Initial(), CancellationToken.None))
            {
                if (resumed.Snapshot.Construction.PendingCalls.Count != 1 || resumed.Snapshot.Usage?.Calls != 1)
                    throw new InvalidOperationException("Published session or budget persistence failed.");
                await resumed.Runtime.CallAsync(request, "intent", CancellationToken.None);
                if (client.Calls != 1) throw new InvalidOperationException("Published receipt replay dispatched another model request.");
            }
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("private native intent", StringComparison.Ordinal))
                    throw new InvalidOperationException("Published planning content was stored in plaintext.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Client : ILLMClient
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject(), Usage = new JsonObject { ["total_tokens"] = 2 } }); }
    }
}
