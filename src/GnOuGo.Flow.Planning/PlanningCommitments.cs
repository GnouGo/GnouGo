using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Non-executable obligations. Coverage never discovers intent or grants a capability.
internal static class PlanningCommitments
{
    internal const string MissingOperation = "REQUIREMENT_OPERATION_MISSING";
    internal const string DisconnectedResult = "REQUIREMENT_RESULT_DISCONNECTED";

    internal static IEnumerable<PlanningCapability> Contracts(PlanningSession state) =>
        (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).DistinctBy(c => (c.Id, c.Version));

    internal static PlanningCapability? Contract(PlanningSession state, string operation)
    {
        var matches = Contracts(state).Where(c => TaskOperations.Describe(c).Id == operation).ToArray();
        return matches.Length == 1 && TaskOperations.Validate(matches[0]).Count == 0 ? matches[0] : null;
    }

    internal static bool CanComplete(PlanningSession state) => state.Plan is null &&
        state.Request.Baseline is null;

    internal static bool SameOutcomes(PlanningSession state, PlanningRequirements next)
    {
        var old = state.Requirements!.Outcomes.OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var proposed = next.Outcomes.OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        if (old.Length != proposed.Length) return false;
        return old.Zip(proposed).All(pair => JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(CanComplete(state) && pair.First.Commitments is null
                ? pair.First with { Commitments = pair.Second.Commitments } : pair.First, PlanningJsonContext.Default.PlanningRequirement),
            JsonSerializer.SerializeToNode(pair.Second, PlanningJsonContext.Default.PlanningRequirement)));
    }

    internal static List<PlanningDiagnostic> Validate(PlanningSession state, PlanningRequirements requirements, bool final)
    {
        var findings = new List<PlanningDiagnostic>();
        for (var i = 0; i < requirements.Outcomes.Count; i++)
        {
            var seen = new HashSet<(string, string?, string?)>();
            var commitments = requirements.Outcomes[i].Commitments ?? [];
            for (var j = 0; j < commitments.Count; j++)
            {
                var obligation = commitments[j]; var location = $"/requirements/outcomes/{i}/commitments/{j}";
                void Reject(string path, string message) => findings.Add(new("REQUIREMENTS_COMMITMENT_INVALID", location + path, message));
                if (string.IsNullOrWhiteSpace(obligation.Operation))
                { Reject("/operation", "The commitment operation is missing. Name one exact inspected operation ID."); continue; }
                if (!seen.Add((obligation.Operation, obligation.Result?.Port, obligation.Result?.Output)))
                { Reject("", "Duplicate commitment: this operation and result obligation is already declared in this requirement."); continue; }
                if (obligation.Result is { } result)
                {
                    if (string.IsNullOrWhiteSpace(result.Port))
                        Reject("/result/port", "The producer result port is missing. Name one exact declared operation port.");
                    if (string.IsNullOrWhiteSpace(result.Output))
                        Reject("/result/output", "The public-output name is missing. " + OutputNames());
                    else if (requirements.Outputs?.Count(o => o.Name == result.Output) != 1)
                        Reject("/result/output", "The result output must name exactly one declared public output, not a path, value or description. " + OutputNames());
                }
                var contract = Contract(state, obligation.Operation);
                if (final && contract is null)
                    Reject("/operation", "The exact committed operation is not unambiguously inspected. Inspect it before accepting the first TaskPlan. Inspected operation IDs: " +
                        Names(Contracts(state).Where(c => TaskOperations.Validate(c).Count == 0).Select(c => TaskOperations.Describe(c).Id)));
                if (contract is not null && obligation.Result is { } committed && !string.IsNullOrWhiteSpace(committed.Port) &&
                    TaskOperations.Describe(contract).Outputs.Count(p => p.Name == committed.Port) != 1)
                    Reject("/result/port", "The result port is not declared by the inspected operation. Declared result ports: " +
                        Names(TaskOperations.Describe(contract).Outputs.Select(p => p.Name)));
            }
        }
        return findings;

        string OutputNames() => "Declared public-output names: " + Names((requirements.Outputs ?? []).Select(o => o.Name));
    }

    // Bounded contract names only, never result values. Matching above stays literal;
    // truncation is solely diagnostic presentation, including for very large catalogs.
    private static string Names(IEnumerable<string> names)
    {
        var sample = names.Distinct(StringComparer.Ordinal).Take(9).ToArray();
        return new JsonArray(sample.Take(8).Select(n => (JsonNode?)JsonValue.Create(n.Length <= 96 ? n : n[..96] + "…")).ToArray()).ToJsonString() +
            (sample.Length > 8 ? " (first 8 shown)." : ".");
    }

    internal static List<PlanningValidationResult> Assess(PlanningSession state)
    {
        if (state.Requirements is null || state.Plan is null) return [];
        var results = new List<PlanningValidationResult>();
        var bindings = new CommitmentBindings(state.Plan);
        for (var i = 0; i < state.Requirements.Outcomes.Count; i++)
        {
            var requirement = state.Requirements.Outcomes[i];
            var prefix = "coverage:" + Uri.EscapeDataString(requirement.Id);
            var commitments = requirement.Commitments ?? [];
            for (var j = 0; j < commitments.Count; j++)
            {
                var clause = commitments[j]; var contract = Contract(state, clause.Operation);
                var location = $"/requirements/outcomes/{i}/commitments/{j}";
                var occurrences = bindings.Operations(clause.Operation);
                var reference = string.Join(", ", occurrences.Select(o => "/tasks/" + o.Task.Id));
                if (contract is null)
                { Add("operation", "unverified", "The exact inspected contract is unavailable."); continue; }
                if (occurrences.Count == 0)
                    Add("operation", "violation", "No reachable task invokes the committed operation. Transforms and uncalled groups do not perform it.",
                        new(MissingOperation, location, $"Committed operation '{clause.Operation}' has no reachable invocation. Add only this inspected operation; preserve existing work.", Rule: clause.Operation));
                else Add("operation", occurrences.Count == 1 && !occurrences[0].Conditional ? "verified" : "unverified",
                    "Declared invocation at " + reference + (occurrences.Count != 1 ? "; occurrences are ambiguous." :
                    occurrences[0].Conditional ? "; conditional, repeated or potentially empty execution is not established." : "; presence only, execution unobserved."));
                if (clause.Result is { } result)
                {
                    var export = state.Plan.Root.Outputs.SingleOrDefault(o => o.Name == result.Output);
                    var lineage = export is null ? CommitmentBindings.Evidence.Disconnected : bindings.Result(export.Value, clause.Operation, result.Port);
                    if (occurrences.Count != 1 || occurrences.Any(o => o.Conditional))
                        lineage = lineage == CommitmentBindings.Evidence.Disconnected ? lineage : CommitmentBindings.Evidence.Unknown;
                    var path = "/root/outputs/" + result.Output;
                    Add("result", lineage switch { CommitmentBindings.Evidence.Established => "verified", CommitmentBindings.Evidence.Disconnected => "violation", _ => "unverified" },
                        $"Public output '{result.Output}' must retain producer '{clause.Operation}.{result.Port}' at {path}. " +
                        (lineage == CommitmentBindings.Evidence.Established ? "Supported direct data lineage established; result content and execution remain unobserved." :
                        lineage == CommitmentBindings.Evidence.Disconnected ? "The binding is disconnected or manufactures a producer-owned result." : "Ambiguous or unsupported lineage cannot establish this obligation."),
                        lineage == CommitmentBindings.Evidence.Disconnected ? new(DisconnectedResult, path,
                            $"Public output '{result.Output}' must derive from committed operation '{clause.Operation}', port '{result.Port}'. Literal or inferred replacements do not establish producer ownership.", Rule: clause.Operation) : null);
                }
                void Add(string suffix, string outcome, string description, PlanningDiagnostic? diagnostic = null) => results.Add(
                    new(prefix + ":" + j + ":" + suffix, outcome, $"{requirement.Id}: {clause.Operation} — {description}", diagnostic is null ? [] : [diagnostic]));
            }
            results.Add(new(prefix + ":limits", "unverified", requirement.Id + (commitments.Count == 0
                ? ": No contractual commitments declared. Business coverage is unverified; no obligations were inferred from the plan."
                : ": Verified clauses do not prove the complete prose requirement, correct arguments, per-item execution, relevant extraction or successful effects. Human acknowledgment is not verification or execution evidence."), []));
        }
        foreach (var occurrence in bindings.ReachableOperations)
        {
            var task = occurrence.Task;
            var requirements = state.Requirements.Outcomes.Where(r => r.Commitments?.Any(c => c.Operation == task.Operation) == true).ToArray();
            var established = requirements.Length == 1 && !occurrence.Conditional && bindings.Operations(task.Operation!).Count == 1;
            results.Add(new("association:" + task.Id, established ? "declared" : "unverified",
                $"Operation '{task.Operation}', effect '{Contract(state, task.Operation!)?.EffectKind ?? "unknown"}'. " +
                (Contract(state, task.Operation!) is { } contract ? PlanningReviewFormatter.OperationOutputs(contract) : "Output contract unavailable. ") +
                (requirements.Length == 0 ? "No exact requirement association established." : "Declared by: " + string.Join(", ", requirements.Select(r => r.Id)) + ".") +
                (established ? " Presence only; intent, arguments and execution remain unverified." : " Association is unverified; this does not establish an unauthorized effect."), []));
        }
        return results;
    }
}
