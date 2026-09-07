using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Preserves actionable contract findings without copying transport payloads.</summary>
internal static class PlanningPreparationDiagnostics
{
    public static List<PlanningDiagnostic> FromException(WorkflowRuntimeException error)
    {
        var findings = new List<PlanningDiagnostic>
        {
            new(error.Code, "/preparation", error.Message, ValidationStage: PlanningPhase.Capabilities)
        };
        if (error.Details?["unavailable_servers"] is JsonArray servers)
            foreach (var server in servers.OfType<JsonValue>())
                if (server.TryGetValue<string>(out var name)) findings.Add(new(error.Code, "/preparation/discovery", "The configured catalog could not be discovered: " + name + ". Restore its connection before retrying.", ValidationStage: PlanningPhase.Capabilities));
        foreach (var collection in new[] { "matching_issues", "contract_issues", "rejected_matching_issues" })
        {
            if (error.Details?[collection] is not JsonArray issues) continue;
            foreach (var (item, index) in issues.Take(64).Select((item, index) => (item, index)))
            {
                if (item is not JsonObject issue) continue;
                var code = Read(issue, "validation_issue") ?? Read(issue, "code") ?? Read(issue, "reason_code")
                    ?? (Read(issue, "status") == "ambiguous" ? "CAPABILITY_CHOICE_UNRESOLVED" : "CAPABILITY_CONTRACT_UNRESOLVED");
                var message = Read(issue, "reason") ?? Read(issue, "message") ?? "The capability contract could not be established.";
                if (Read(issue, "description") is { } description) message = description + "\n" + message;
                if (Read(issue, "operation_id") is { } operation) message += "\nOperation: " + operation;
                if (Read(issue, "decision_operation_id") is { } decision) message += "\nDeclared decision source: " + decision;
                if (Read(issue, "hint") is { } hint) message += "\nRepair: " + hint;
                var required = issue["required"] is not JsonValue value || !value.TryGetValue<bool>(out var flag) || flag;
                findings.Add(new(code, "/preparation/" + collection + "/" + index, message, required, PlanningPhase.Capabilities));
            }
        }
        return findings;
    }

    private static string? Read(JsonObject value, string key) => value[key] is JsonValue node
        && node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
