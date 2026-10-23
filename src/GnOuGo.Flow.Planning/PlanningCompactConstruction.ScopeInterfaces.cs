using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // This is evidence inside the selected scope, never an externally consumable container result.
    internal static JsonObject? ScopeInterface(PlanningSession state, bool candidate, int offset = 0, bool navigationOnly = false, TaskConstructionValidation? verified = null)
    {
        var construction = state.Construction!;
        var step = State(state).CurrentStep;
        var plan = candidate ? construction.Candidate?.Plan : state.Plan;
        if (step is null || plan is null) return null;
        var open = candidate ? construction.Candidate!.OpenScopes : construction.OpenScopes;
        var scope = new TaskPlanSymbols(plan).Scopes.SingleOrDefault(s => s.Path == step.Scope);
        if (scope is null || offset < 0 || offset > 0 && offset >= scope.Source.Outputs.Count) return null;
        if (!candidate && scope.Source.Outputs.Count == 0) return null;
        var outputs = scope.Source.Outputs.Skip(offset).Take(8).ToArray();
        var frontier = step.Scope + "/always"; // Scope outputs are resolved after its cleanup frontier.
        var validation = verified;
        if (validation is null)
        {
            var probe = PlanningConstruction.Clone(plan);
            foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            validation = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request,
                open.ToHashSet(StringComparer.Ordinal), new Dictionary<string, IReadOnlyList<TaskValue>>
                    { [frontier] = outputs.Select(o => o.Value).ToArray() });
        }
        if (!validation.Interfaces.TryGetValue(frontier, out var ports) || ports.Count != outputs.Length)
            PlanningConstruction.Fail("CONSTRUCTION_SCOPE_INTERFACE_INVALID", step.Scope, "The selected scope's exact export interfaces must validate before presentation.");
        var version = ScopeInterfaceVersion(plan, open, step.Scope);
        var view = new JsonObject
        {
            ["scope"] = step.Scope, ["basis"] = candidate ? "tentative_candidate" : "accepted_declarations",
            ["open"] = open.Contains(step.Scope, StringComparer.Ordinal), ["consumable"] = false,
            ["version"] = version, ["offset"] = offset, ["outputCount"] = scope.Source.Outputs.Count,
            ["outputs"] = new JsonArray(outputs.Zip(ports!).Select(pair =>
            {
                var output = new JsonObject
                {
                    ["name"] = pair.First.Name,
                    ["value"] = PlanningJsonTransport.TaskPlanPart(PlanningConstruction.Node(pair.First.Value, PlanningJsonContext.Default.TaskValue))
                };
                if (!navigationOnly) output["schema"] = pair.Second.Schema.DeepClone();
                return (JsonNode)output;
            }).ToArray()),
            ["next"] = offset + 8 < scope.Source.Outputs.Count ? ScopeInterfaceAddress(step.Scope, candidate, version, offset + 8) : null
        };
        if (navigationOnly)
        {
            // These accepted declarations are unchanged by this fragment. Its
            // actual typed operands remain in interfaces; the original export
            // types remain available through the existing exact evidence page.
            view["evidence"] = ScopeInterfaceAddress(step.Scope, candidate, version, offset);
        }
        return view;
    }

    private static JsonObject? ReviewInputDeclarations(PlanningSession state)
    {
        var compact = State(state);
        var step = compact.CurrentStep!;
        if (ScopeGroup(state, step.Scope) is not null) return null;
        var sources = step.RequirementIds.Concat(step.CloseRequirementIds)
            .SelectMany(id => compact.RequirementSources.GetValueOrDefault(id) ?? [])
            .Concat(compact.GlobalSourceIds).ToHashSet(StringComparer.Ordinal);
        var declarations = (compact.InterfaceExtractions ?? []).Where(extraction => extraction.Complete &&
                DispositionsComplete(compact, extraction)).SelectMany(extraction => extraction.Dispositions!)
            .Where(disposition => disposition.Kind == "interfaces" &&
                (sources.Contains(disposition.SourceId) || disposition.Evidence.Keys.Any(sources.Contains)))
            .SelectMany(disposition => disposition.Inputs.Select(name => (Name: name, disposition.SourceId)))
            .Distinct().ToArray();
        if (declarations.Length == 0)
            return state.Construction!.Candidate!.Plan.Inputs.Count == 0 ? null : new JsonObject
            {
                ["scope"] = "/root", ["basis"] = "navigation_only", ["count"] = state.Construction.Candidate.Plan.Inputs.Count,
                ["inputs"] = new JsonArray(), ["directory"] = "page:interfaces:0"
            };
        var names = declarations.Select(declaration => declaration.Name).Distinct(StringComparer.Ordinal).ToArray();
        var inputs = state.Construction!.Candidate!.Plan.Inputs;
        var entries = new JsonArray();
        var remainingBytes = 4096;
        foreach (var name in names.Take(8))
        {
            var input = inputs.SingleOrDefault(input => input.Name == name);
            var entry = input is null ? new JsonObject { ["input"] = name, ["scope"] = "/root", ["evidence"] = "input:" + name }
                : InputDirectoryEntry(state, input);
            entry["present"] = input is not null;
            entry["sourceIds"] = new JsonArray(declarations.Where(declaration => declaration.Name == name)
                .Select(declaration => (JsonNode?)JsonValue.Create(declaration.SourceId)).ToArray());
            if (input is not null)
            {
                var original = PlanningConstruction.Node(input, PlanningJsonContext.Default.TaskInput)!;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(original.ToJsonString());
                if (bytes <= remainingBytes) { entry["declaration"] = original; remainingBytes -= bytes; }
                else entry["declarationOmitted"] = true;
            }
            entries.Add((JsonNode)entry);
        }
        return new JsonObject
        {
            ["scope"] = "/root", ["basis"] = "tentative_candidate", ["count"] = names.Length,
            ["inputs"] = entries, ["directory"] = names.Length > 8 ? "page:interfaces:0" : null
        };
    }

    private static string ScopeInterfaceVersion(TaskPlan plan, IEnumerable<string> open, string path) =>
        PlanningGraphCompiler.Fingerprint(PlanningConstruction.Hash(plan) + "\n" + path + "\n" + string.Join('\n', open.Order(StringComparer.Ordinal)));

    private static string ScopeInterfaceAddress(string scope, bool candidate, string version, int offset) =>
        "scope-exports:" + (candidate ? "candidate:" : "accepted:") + Uri.EscapeDataString(scope) + "@" + version + ":" + offset.ToString(CultureInfo.InvariantCulture);

    private static bool LoadScopeInterfaceEvidence(PlanningSession state, string address, out JsonNode? value)
    {
        value = null;
        if (!address.StartsWith("scope-exports:", StringComparison.Ordinal)) return false;
        var pieces = address.Split(':');
        if (pieces is not ["scope-exports", "accepted" or "candidate", var identity, var cursor] ||
            !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0) return true;
        var separator = identity.LastIndexOf('@');
        if (separator <= 0) return true;
        var encoded = identity[..separator]; var scope = Uri.UnescapeDataString(encoded);
        if (Uri.EscapeDataString(scope) != encoded || State(state).CurrentStep?.Scope != scope) return true;
        var candidate = pieces[1] == "candidate";
        var construction = state.Construction!;
        if (candidate && (construction.Candidate is not { Diagnostics.Count: 0 } draft ||
            !draft.Extension.CloseScopes.Contains(scope, StringComparer.Ordinal))) return true;
        var plan = candidate ? construction.Candidate?.Plan : state.Plan;
        if (plan is null) return true;
        var open = candidate ? construction.Candidate!.OpenScopes : construction.OpenScopes;
        var version = ScopeInterfaceVersion(plan, open, scope);
        if (address != ScopeInterfaceAddress(scope, candidate, version, offset)) return true;
        value = ScopeInterface(state, candidate, offset);
        return true;
    }
}
