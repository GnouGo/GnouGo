using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    // Runtime and scenario validators use executable input field names. Reuse the
    // same small, exact-binding patches when those later stages find a value defect.
    private async Task<bool> RepairConstructionFieldsAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.ConstructionUnits.Count == 0) return false;
        var graph = state.Graph!;
        var repairDiagnostics = state.Diagnostics.Select(d => ConstructionRepairDiagnostic(d, graph)).ToArray();
        var located = graph.Workflows.SelectMany((w, wi) => PlanningGraphValidation.Located(w.Steps, "/workflows/" + wi + "/steps")
            .Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + wi + "/finally")).Select(n => (Workflow: w, n.Node, n.Path))).ToArray();
        // Nested runtime telemetry also reports the failed container. Repair the
        // actual innermost failing operation, not an immutable routing wrapper.
        var finding = repairDiagnostics.Where(d => d.Code != "SCENARIO_UNREACHED" && located.Any(n => d.Location == n.Path || new[] { "input", "expr", "onError", "outputSchema", "structuredOutput" }.Any(field => d.Location == n.Path + "/" + field || d.Location.StartsWith(n.Path + "/" + field + "/", StringComparison.Ordinal))))
            .OrderByDescending(d => d.Location.Count(c => c == '/')).FirstOrDefault();
        var helperUnit = finding is null ? state.ConstructionUnits.FirstOrDefault(u => u.Kind == "implementation" && u.Status != "superseded" &&
            state.Diagnostics.Any(d => d.Location.StartsWith("/workflows/" + graph.Workflows.FindIndex(w => w.Key == u.WorkflowKey) + "/functions/u_" + PlanningGraphCompiler.Fingerprint(u.Key)[..8] + "_", StringComparison.Ordinal))) : null;
        if (finding is null && helperUnit is null) return false;
        var owner = helperUnit is not null ? located.First(n => n.Workflow.Key == helperUnit.WorkflowKey && helperUnit.NodeKeys.Contains(n.Node.Key, StringComparer.Ordinal))
            : located.Where(n => finding!.Location == n.Path || finding.Location.StartsWith(n.Path + "/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).First();
        if (finding is null && helperUnit is not null)
            finding = state.Diagnostics.First(d => d.Location.StartsWith("/workflows/" + graph.Workflows.FindIndex(w => w.Key == helperUnit.WorkflowKey) + "/functions/u_" + PlanningGraphCompiler.Fingerprint(helperUnit.Key)[..8] + "_", StringComparison.Ordinal));
        if (finding is null) return false;
        var unitKind = finding.Location == owner.Path + "/outputSchema" || finding.Location.StartsWith(owner.Path + "/outputSchema/", StringComparison.Ordinal) || finding.Location == owner.Path + "/structuredOutput" || finding.Location.StartsWith(owner.Path + "/structuredOutput/", StringComparison.Ordinal) ? "contracts" : "implementation";
        var unit = helperUnit ?? state.ConstructionUnits.FirstOrDefault(u => u.WorkflowKey == owner.Workflow.Key && u.Kind == unitKind && u.Status != "superseded" && u.NodeKeys.Contains(owner.Node.Key, StringComparer.Ordinal));
        if (unit is null) return false;
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(owner.Workflow, unit, state.Preparation), state.Preparation!);
        unit.CandidateHash = PlanningGraphCompiler.Fingerprint(unit.Candidate.ToJsonString());
        var helperPath = "/workflows/" + graph.Workflows.FindIndex(w => w.Key == unit.WorkflowKey) + "/functions/u_" + PlanningGraphCompiler.Fingerprint(unit.Key)[..8] + "_";
        unit.Diagnostics = repairDiagnostics.Where(d => helperUnit is not null ? d.Location.StartsWith(helperPath, StringComparison.Ordinal)
            : located.Any(n => n.Workflow.Key == unit.WorkflowKey && unit.NodeKeys.Contains(n.Node.Key, StringComparer.Ordinal) && (d.Location == n.Path || d.Location.StartsWith(n.Path + "/", StringComparison.Ordinal))))
            .Select(d => d with { Location = PlanningLocation(d.Location, graph) }).ToList();
        var preparation = UnitPreparation(state.Preparation!, owner.Workflow, unit);
        var schema = PlanningConstruction.Schema(owner.Workflow, unit, preparation, graph);
        var patch = PlanningUnitPatches.Create(graph, unit, schema, state.Preparation);
        var prompt = UnitRepairPrompt(state, owner.Workflow, unit, preparation, patch);
        while (PlanningConstruction.EstimateInputTokens(prompt, patch.Schema) > state.Request.Generation.MaxInputTokensPerUnit)
        {
            var narrowed = patch.Narrow(); if (ReferenceEquals(patch, narrowed)) break;
            patch = narrowed; prompt = UnitRepairPrompt(state, owner.Workflow, unit, preparation, patch);
        }
        unit.EstimatedInputTokens = PlanningConstruction.EstimateInputTokens(prompt, patch.Schema); unit.InputTokenLimit = state.Request.Generation.MaxInputTokensPerUnit;
        if (unit.EstimatedInputTokens > unit.InputTokenLimit)
        {
            unit.DispatchOutcome = "not_dispatched";
            unit.DispatchDiagnostics = [new("UNIT_CONTEXT_TOO_LARGE", "/units/" + PlanningSchemaReferences.Escape(unit.Key), $"The smallest field repair needs {unit.EstimatedInputTokens} estimated input tokens; the limit is {unit.InputTokenLimit}. No repair request was sent.", ValidationStage: "generation")];
            state.Diagnostics.AddRange(unit.DispatchDiagnostics); state.RepairAttempt = Math.Max(0, state.RepairAttempt - 1); state.Status = PlanningStatus.Recovery; return true;
        }
        state.CurrentPhase = "repair_unit";
        var generator = state.Request.Options["generator"];
        var request = PlanningGenerationPolicy.Apply(new LLMRequest { Provider = generator?["provider"]?.GetValue<string>(), Model = generator?["model"]?.GetValue<string>() ?? "",
            Prompt = prompt, StructuredOutputSchema = patch.Schema, StructuredOutputStrict = true, UseBackgroundMode = true }, state.Request.Generation);
        unit.RequestHashes.Add(PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)));
        unit.DispatchDiagnostics.Clear(); unit.DispatchOutcome = "dispatched"; unit.Calls++; unit.RepairCalls++;
        LLMResponse response;
        try { response = await runtime.CallAsync(request, "repair_unit", ct); }
        catch { unit.DispatchOutcome = "transport_failed"; throw; }
        unit.DispatchOutcome = "received";
        if (response.CompletionStatus == "output_limit")
        {
            RecordUnitOutputLimit(state, unit, response, "repair_unit");
            state.Diagnostics.AddRange(unit.DispatchDiagnostics); unit.Status = "recovery"; state.Status = PlanningStatus.Recovery; return true;
        }
        try
        {
            var received = response.Json as JsonObject;
            if (received is not null && unit.ContractVersion >= PlanningConstructionSchemas.Version) received = PlanningConstructionSchemas.Compact(received);
            var candidate = patch.Apply(unit.Candidate, received);
            if (PlanningHelperDocumentation.OnlyDocumentation(unit.Diagnostics))
                PlanningHelperDocumentation.RequireUnchangedExecutable(unit.Candidate["functions"]?.GetValue<string>(), candidate["functions"]?.GetValue<string>());
            var repaired = PlanningConstruction.Apply(graph, unit, candidate, state.Preparation!);
            if (unit.Kind == "implementation") repaired.Workflows.Single(w => w.Key == unit.WorkflowKey).Functions = MergeFunctions(state, unit, candidate["functions"]?.GetValue<string>());
            var diagnostics = UnitFindings(repaired, state.Preparation!, unit).Concat(InputObligationFindings(state, repaired, unit)).ToList();
            if (state.BehaviorPlan is not null) diagnostics.AddRange(PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan, repaired, state.Preparation!));
            if (diagnostics.Count != 0 && ScheduleRejectedProducerRepair(state, unit, candidate, diagnostics))
            {
                state.Events.Add(new("producer_contract_review_scheduled", "repair_unit", _time.GetUtcNow()));
                return true;
            }
            if (diagnostics.Count != 0) throw new InvalidOperationException(string.Join("\n", diagnostics.Select(d => d.Code + " at " + d.Location + ": " + d.Message)));
            state.Graph = repaired; unit.Candidate = candidate; unit.CandidateHash = PlanningGraphCompiler.Fingerprint(candidate.ToJsonString());
            if (unit.Kind == "implementation") unit.Functions = candidate["functions"]?.GetValue<string>();
            unit.Diagnostics.Clear(); unit.Fingerprint = UnitFingerprint(state, unit); state.Status = PlanningStatus.Validating;
        }
        catch (InvalidOperationException ex)
        {
            state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(response.Json?.ToJsonString() ?? response.Text), "repair_unit", 0, false, [new("UNIT_PATCH_REJECTED", "/units/" + PlanningSchemaReferences.Escape(unit.Key), ex.Message)]));
            if (state.RepairAttempt >= state.Request.MaxRepairs) state.Status = PlanningStatus.Recovery;
            else { state.RepairAttempt++; state.Status = PlanningStatus.Generating; }
        }
        return true;
    }

    internal static bool ScheduleRejectedProducerRepair(PlanningSnapshot state, PlanningConstructionUnit unit, JsonObject candidate, List<PlanningDiagnostic> diagnostics)
    {
        var previous = (unit.Candidate, unit.CandidateHash, unit.Status, unit.Diagnostics);
        unit.Candidate = candidate; unit.CandidateHash = PlanningGraphCompiler.Fingerprint(candidate.ToJsonString());
        unit.Status = "invalid"; unit.Diagnostics = diagnostics;
        if (!PlanningProducerRepair.Schedule(state))
        {
            (unit.Candidate, unit.CandidateHash, unit.Status, unit.Diagnostics) = previous;
            return false;
        }
        // Retain the invalid unit checkpoint and its exact findings without publishing
        // it as the validated graph. The regular dependency queue repairs the producer.
        state.Attempts.Add(new(unit.CandidateHash, "repair_unit", 0, false, diagnostics.ToList()));
        state.Diagnostics = state.ConstructionUnits.Where(u => u.Status == "invalid").SelectMany(u => u.Diagnostics).ToList();
        state.CurrentPhase = "repair_unit"; state.Status = PlanningStatus.Generating; state.RepairAttempt = 0;
        return true;
    }

    internal static PlanningDiagnostic ConstructionRepairDiagnostic(PlanningDiagnostic diagnostic, PlanningGraph graph)
    {
        if (diagnostic.Code != "SCENARIO_OBSERVATIONS_UNCONSUMED") return diagnostic;
        var loops = graph.Workflows.SelectMany((w, wi) => PlanningGraphValidation.Located(w.Steps, "/workflows/" + wi + "/steps")
            .Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + wi + "/finally")))
            .Where(n => n.Node.Type is "loop.sequential" or "loop.parallel" && diagnostic.Location.StartsWith(n.Path + "/", StringComparison.Ordinal));
        var owner = loops.OrderByDescending(n => n.Path.Length).FirstOrDefault();
        if (owner.Node is null) return diagnostic;
        var controls = owner.Node.Type == "loop.sequential"
            ? " An items array bounds the total iterations even when while remains true."
            : " The items array determines the iteration count.";
        return diagnostic with { Location = owner.Path + "/input", Message = diagnostic.Message +
            " Repair the containing loop's iteration controls." + controls + " Preserve complete traversal and termination; do not limit the loop to the fixture's response count." };
    }

    internal static string PlanningLocation(string location, PlanningGraph graph)
    {
        var nodes = graph.Workflows.SelectMany((w, wi) => PlanningGraphValidation.Located(w.Steps, "/workflows/" + wi + "/steps").Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + wi + "/finally")));
        var owner = nodes.Where(n => location.StartsWith(n.Path + "/input/", StringComparison.Ordinal)).OrderByDescending(n => n.Path.Length).FirstOrDefault();
        if (owner.Node is null) return location;
        var suffix = location[(owner.Path.Length + "/input/".Length)..];
        if (suffix.StartsWith("members/", StringComparison.Ordinal)) return location;
        var path = owner.Path + "/input"; var value = owner.Node.Input;
        foreach (var part in suffix.Split('/'))
        {
            var name = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.Kind == "object" && value.Members.FindIndex(m => m.Name == name) is var index && index >= 0)
            { path += "/members/" + index + "/value"; value = value.Members[index].Value; }
            else if (value.Kind == "array" && int.TryParse(part, out var item) && item >= 0 && item < value.Items.Count)
            { path += "/items/" + item; value = value.Items[item]; }
            else return path;
        }
        return path;
    }
}
