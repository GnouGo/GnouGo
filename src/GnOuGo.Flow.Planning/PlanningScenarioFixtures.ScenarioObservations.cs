using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningScenarioFixtures
{
    internal static IReadOnlyList<PlanningNode> ScenarioObservationSources(PlanningWorkflow workflow, PlanningNode loop)
    {
        var nodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToDictionary(n => n.Key, StringComparer.Ordinal);
        var candidates = PlanningGraphCompiler.Enumerate(loop.Steps).Where(n => n.Type == "mcp.call").ToDictionary(n => n.Key, StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Producer(string? key)
        {
            if (key is null || !visited.Add(key) || !nodes.TryGetValue(key, out var node)) return;
            if (candidates.ContainsKey(key)) { sources.Add(key); return; }
            Visit(node.Input); if (node.Expr is not null) Visit(node.Expr);
            foreach (var child in node.Steps.Concat(node.Default).Concat(node.Cases.SelectMany(c => c.Steps)).Concat(node.Branches.SelectMany(b => b.Steps))) Producer(child.Key);
        }
        void Visit(PlanningValue value)
        {
            foreach (var reference in PlanningDataflow.References(value))
                if (reference.Kind == "output") Producer(reference.Source);
                else if (reference.Kind == "loop_previous" && reference.Source == loop.Key)
                {
                    if (reference.Path.Count > 0) Producer(reference.Path[0]);
                    else foreach (var child in loop.Steps) Producer(child.Key);
                }
        }
        foreach (var condition in loop.Input.Members.Where(m => m.Name == "while")) Visit(condition.Value);
        return candidates.Values.Where(n => sources.Contains(n.Key)).ToArray();
    }

    // Observation sequences are private test fixtures, never executable defaults.
    // Static schema samples cannot model an external continuation that changes over time.
    internal async Task<bool> PrepareObservationsAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.Validation.FixturesEstablished) return true;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workflow in state.Graph!.Workflows)
            foreach (var loop in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n =>
                         n.Type == "loop.sequential" && n.Input.Members.Any(m => m.Name == "while")))
                foreach (var node in ScenarioObservationSources(workflow, loop))
                {
                    var key = (workflow.Key == state.Graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..16]) + ":n_" + PlanningGraphCompiler.Fingerprint(node.Key)[..16];
                    retained.Add(key);
                    var contract = PlanningGraphValidation.ResolveValueContract(state.Graph, workflow, new() { Kind = "output", Source = node.Key, ResultChannel = "envelope" }, state.Preparation!);
                    var context = JsonSerializer.Serialize(loop, PlanningJsonContext.Default.PlanningNode);
                    var fingerprint = PlanningGraphCompiler.Fingerprint(context + contract.ToJsonString());
                    if (state.Validation.Observations[key] is JsonObject cached && cached["fingerprint"]?.ToString() == fingerprint &&
                        cached["responses"] is JsonArray existing && existing.Count is >= 2 and <= 3 &&
                        (cached["totalObservations"] is null || cached["totalObservations"]!.GetValue<int>() == existing.Count) && existing.All(s => PlanningContractValidation.ValidateInstance(s, contract).Count == 0)) continue;
                    var samples = new JsonArray(); var total = 0;
                    if (state.Validation.Observations[key] is JsonObject partial && partial["fingerprint"]?.ToString() == fingerprint &&
                        partial["totalObservations"] is JsonValue count && count.TryGetValue<int>(out var expected) && expected is 2 or 3 &&
                        partial["responses"] is JsonArray retainedSamples && retainedSamples.Count < expected &&
                        retainedSamples.All(s => PlanningContractValidation.ValidateInstance(s, contract).Count == 0))
                    { samples = (JsonArray)retainedSamples.DeepClone(); total = expected; }
                    state.CurrentPhase = "scenario_observations";
                    var behavior = new JsonObject
                    {
                        ["condition"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(loop.Input.Members.Single(m => m.Name == "while").Value, PlanningJsonContext.Default.PlanningValue)),
                        ["producer"] = node.Purpose,
                        ["task"] = "Provide a synthetic observation satisfying this loop condition. Preserve raw and structured result channels. Fixture values never assert real observations or human consent."
                    };
                    if (total == 0)
                    {
                        var id = "observation_count_" + PlanningGraphCompiler.Fingerprint(key)[..16];
                        var countChoice = await PlanningDecisionPages.ResolveAsync(state, runtime, "scenario_observations", workflow.Key,
                            [new(id, PlanningHoleRequests.Enum("2", "3"), behavior.DeepClone().AsObject(), fingerprint)], ct);
                        total = int.Parse(countChoice[id]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    while (samples.Count < total)
                    {
                        behavior["position"] = samples.Count; behavior["final"] = samples.Count + 1 == total;
                        behavior["previous"] = samples.LastOrDefault()?.DeepClone();
                        var sample = await FixtureAsync(state, runtime, "scenario_observations", workflow.Key, "/scenarioObservations/" + key + "/" + samples.Count,
                            contract, behavior, ct);
                        if (PlanningContractValidation.ValidateInstance(sample, contract).Count != 0)
                        {
                            state.Diagnostics = [new("SCENARIO_OBSERVATION_INVALID", key, "The assembled observation violates the producer contract.")];
                            state.Status = PlanningStatus.Stopped; return false;
                        }
                        samples.Add(sample);
                        state.Validation.Observations[key] = new JsonObject { ["fingerprint"] = fingerprint, ["schema"] = contract.DeepClone(), ["responses"] = samples.DeepClone(), ["totalObservations"] = total };
                        await runtime.CheckpointAsync(state, ct);
                    }
                }
        foreach (var key in state.Validation.Observations.Select(p => p.Key).Where(k => !retained.Contains(k)).ToArray())
        { state.Validation.Observations.Remove(key); }
        return true;
    }
}
