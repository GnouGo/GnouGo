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

    internal static bool CanComplete(PlanningSession state) => state.IntentVersion == 3 && state.Plan is null &&
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
        if (state.IntentVersion != 3) return [];
        var findings = new List<PlanningDiagnostic>();
        for (var i = 0; i < requirements.Outcomes.Count; i++)
        {
            var seen = new HashSet<(string, string?, string?)>();
            var commitments = requirements.Outcomes[i].Commitments ?? [];
            for (var j = 0; j < commitments.Count; j++)
            {
                var obligation = commitments[j]; var location = $"/requirements/outcomes/{i}/commitments/{j}";
                void Reject(string message) => findings.Add(new("REQUIREMENTS_COMMITMENT_INVALID", location, message));
                if (string.IsNullOrWhiteSpace(obligation.Operation) || !seen.Add((obligation.Operation, obligation.Result?.Port, obligation.Result?.Output)))
                { Reject("Declare distinct commitments using exact inspected operation identities."); continue; }
                if (obligation.Result is { } result && (string.IsNullOrWhiteSpace(result.Port) || string.IsNullOrWhiteSpace(result.Output) ||
                    requirements.Outputs?.Count(o => o.Name == result.Output) != 1))
                    Reject("A result commitment must name one declared operation port and one accepted public output.");
                var contract = Contract(state, obligation.Operation);
                if (final && contract is null)
                    Reject("Inspect the exact committed operation contract before accepting the first TaskPlan; names and types do not establish an operation.");
                if (contract is not null && obligation.Result is { } committed && TaskOperations.Describe(contract).Outputs.Count(p => p.Name == committed.Port) != 1)
                    Reject("The committed result port is not declared by the inspected operation.");
            }
        }
        return findings;
    }

    internal static List<PlanningValidationResult> Assess(PlanningSession state)
    {
        if (state.IntentVersion != 3 || state.Requirements is null || state.Plan is null) return [];
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
        return results;
    }
}
