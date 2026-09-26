using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Persist an answer before a host acknowledges it or releases its in-process waiter.</summary>
public static class WorkflowRunHumanResponses
{
    public static async Task PrepareAsync(IWorkflowRunStore store, string tenant, HumanInputRequest request, CancellationToken ct)
    {
        if (request.ParentInvocationId is null) return;
        for (var attempt = 0; ; attempt++)
        {
            var run = await store.ReadAsync(tenant, request.RunId, ct);
            // Some host dialogs occur outside a durable workflow run.
            if (run is null) return;
            if (run.Invocations.TryGetValue(request.StepId, out var existing))
            {
                if (existing.ParentInvocationId != request.ParentInvocationId || existing.Status != "waiting_for_human" ||
                    !JsonNode.DeepEquals(existing.ResolvedInput, HumanInputContract.BuildRequestPayload(request)))
                    throw new WorkflowRunConflictException("The dialog identity cannot be reused for another request.");
                return;
            }
            try { await store.RequestInputAsync(tenant, request.RunId, run.Revision, request, ct); return; }
            catch (WorkflowRunConflictException) when (attempt < 4) { }
        }
    }
    public static async Task RecordAsync(IWorkflowRunStore store, string tenant, string runId, string invocationId,
        JsonNode? response, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var run = await store.ReadAsync(tenant, runId, ct);
            // Hosts also use this provider for configuration and adapter permission dialogs outside human.input.
            if (run is null || !run.Invocations.TryGetValue(invocationId, out var invocation) || invocation.Recovery != StepRecovery.HumanInput) return;
            try { await store.AnswerAsync(tenant, runId, run.Revision, invocationId, response, ct); return; }
            catch (WorkflowRunConflictException) when (attempt < 4 && invocation.Status == "waiting_for_human" && !invocation.Control.ContainsKey("human_response")) { }
        }
    }
}
