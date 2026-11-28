using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class WorkflowCopilotHumanInput(AgentTaskContext owner, IHumanInputProvider provider, CancellationToken lifetime)
    : ICopilotHumanInputProvider
{
    private readonly SemaphoreSlim _registration = new(1, 1);

    public async Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest input, CancellationToken ct)
    {
        if (input.Context.TenantId != owner.TenantId || input.Context.RunId != owner.RunId || input.Context.StepId != owner.InvocationId)
            throw new UnauthorizedAccessException("The SDK dialog belongs to a different invocation.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        var schema = input.RequestedSchema is { } json ? JsonNode.Parse(json.GetRawText())?.AsObject() : null;
        var context = new JsonObject { ["tenant_id"] = owner.TenantId, ["runner"] = owner.Task.Runner, ["kind"] = input.Kind };
        if (input.ProjectRead is { } read)
        {
            context["project_tool"] = read.Tool;
            context["project_path"] = read.Path;
        }
        if (input.RequestedHostTool is { } hostTool) context["host_tool"] = hostTool;
        if (input.ProjectWrite is { } write)
        {
            context["project_write_tool"] = write.Tool;
            context["project_write_paths"] = new JsonArray(write.Paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        }
        var request = new HumanInputRequest
        {
            RunId = owner.RunId, StepId = owner.InvocationId + "/human/" + Guid.NewGuid().ToString("N"),
            ParentInvocationId = owner.InvocationId, Prompt = string.IsNullOrWhiteSpace(input.Details) ? input.Prompt : input.Prompt + "\n" + input.Details,
            Mode = schema is not null ? HumanInputContract.ModeForm : input.AllowFreeform ? HumanInputContract.ModeText : HumanInputContract.ModeChoice,
            Choices = input.Choices.ToList(), AllowAbandon = true,
            Context = context,
            TimeoutMs = owner.Task.Budget.MaxElapsedMilliseconds
        };
        if (schema?["properties"] is JsonObject properties)
            request.Fields = properties.Select(p => new HumanInputFieldDef
            {
                Name = p.Key, Description = p.Value?["description"]?.ToString(), Type = p.Value?["enum"] is JsonArray ? "select" : p.Value?["type"]?.ToString() ?? "string",
                Options = (p.Value?["enum"] as JsonArray)?.Select(n => n!.ToString()).ToList(),
                Required = (schema["required"] as JsonArray)?.Any(n => n?.ToString() == p.Key) == true
            }).ToList();
        // Parallel SDK tool calls can request many dialogs against one journal revision.
        // Serialize their registrations while allowing independent answers to remain pending.
        await _registration.WaitAsync(deadline.Token);
        try { await provider.PrepareAsync(request, deadline.Token); }
        finally { _registration.Release(); }
        owner.HumanInput?.Invoke(request, "Waiting");
        try
        {
            var response = await provider.RequestInputAsync(request, deadline.Token);
            if (response is null || HumanInputContract.IsAbandoned(response)) return new(false);
            if (schema is not null)
            {
                if (PlanningContractValidation.ValidateInstance(response, schema).Count != 0) return new(false);
                using var content = JsonDocument.Parse(response.ToJsonString());
                return new(true, Content: content.RootElement.Clone());
            }
            var answer = (response is JsonObject obj ? obj["answer"] ?? obj["response"] : response)?.GetValue<string>();
            var selected = answer is not null && input.Choices.Contains(answer, StringComparer.Ordinal);
            return new(answer is not null && (selected || input.AllowFreeform), answer, WasFreeform: !selected);
        }
        finally { owner.HumanInput?.Invoke(request, "Resumed"); }
    }
}
