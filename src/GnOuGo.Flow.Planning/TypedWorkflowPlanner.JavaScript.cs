using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Expressions;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    private static bool UsesJavaScript(PlanningSnapshot state) => state.Request.ConstructionStrategy == PlanningConstructionStrategies.JavaScriptV1;
    private static readonly JsonObject SourceResponseSchema = new()
    {
        ["type"] = "object", ["properties"] = new JsonObject { ["source"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("source"), ["additionalProperties"] = false
    };

    private async Task GenerateSourceAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (sourceCompiler is null || sourceCompiler.Format != state.Request.ConstructionStrategy)
        { StopSource(state, "JS_COMPILER_UNAVAILABLE", "The host has not registered the requested source compiler."); return; }
        if (RecoverInvalidBehavior(state)) return;
        if (state.BehaviorPlan is null || state.Graph is null || state.Preparation is null || !HasBehaviorApproval(state))
        { StopSource(state, "JS_REVIEW_REQUIRED", "Source construction requires an approved behavior and preparation contract."); return; }
        state.CurrentPhase = "javascript_workflow";
        var graph = state.Graph;
        foreach (var workflow in graph.Workflows)
            if (!state.SourceCandidates.Any(c => c.WorkflowKey == workflow.Key))
                state.SourceCandidates.Add(new() { WorkflowKey = workflow.Key, SdkVersion = sourceCompiler.Version });
        var dependencies = graph.Workflows.ToDictionary(w => w.Key, w => SourceDependencies(w).ToArray(), StringComparer.Ordinal);
        if (dependencies.Any(p => p.Value.Any(d => !dependencies.ContainsKey(d))))
        { StopSource(state, "JS_DEPENDENCY_MISSING", "An accepted workflow call has no declared producer workflow."); return; }
        foreach (var record in state.SourceCandidates.Where(c => dependencies.ContainsKey(c.WorkflowKey)))
        {
            var fingerprint = SourceDependencyFingerprint(state, record.WorkflowKey, dependencies[record.WorkflowKey]);
            if (record.Status == "validated" && (record.DependencyFingerprint != fingerprint ||
                dependencies[record.WorkflowKey].Any(d => state.SourceCandidates.Single(c => c.WorkflowKey == d).Status != "validated")))
                record.Status = "pending";
        }
        var pending = state.SourceCandidates.Where(c => dependencies.ContainsKey(c.WorkflowKey) && c.Status != "validated").ToArray();
        if (pending.Length == 0) { state.Status = PlanningStatus.Validating; return; }
        var unit = pending.SingleOrDefault(c => c.PendingPrompt is not null) ??
            pending.FirstOrDefault(c => dependencies[c.WorkflowKey].All(d => state.SourceCandidates.Single(p => p.WorkflowKey == d).Status == "validated"));
        if (unit is null) { StopSource(state, "JS_DEPENDENCY_CYCLE", "Source construction requires acyclic producer/callee dependencies."); return; }
        if (unit.PendingPrompt is not null && unit.SdkVersion != sourceCompiler.Version)
        { StopSource(state, "JS_SDK_CHANGED", "Reconcile the pending receipt with its original SDK version before resuming.", unit.Diagnostics); return; }
        if (unit.PendingPrompt is not null && unit.DependencyFingerprint != SourceDependencyFingerprint(state, unit.WorkflowKey, dependencies[unit.WorkflowKey]))
        { StopSource(state, "JS_PENDING_CONTRACT_CHANGED", "The pending source request belongs to different dependency contracts; its receipt must be reconciled before revision.", unit.Diagnostics); return; }
        if (unit.Status == "stalled" || unit.Calls >= 3 && unit.PendingPrompt is null)
        { StopSource(state, "JS_REPAIR_EXHAUSTED", "This subworkflow has exhausted its initial candidate and two repair calls.", unit.Diagnostics); return; }

        var wi = graph.Workflows.FindIndex(w => w.Key == unit.WorkflowKey);
        var workflowTemplate = graph.Workflows[wi];
        var preparation = SourcePreparation(state.Preparation, workflowTemplate);
        var context = new PlanningSourceContext(workflowTemplate, preparation);
        if (unit.PendingPrompt is null)
        {
            var prompt = SourcePrompt(state, context, unit, dependencies[unit.WorkflowKey]);
            if (PlanningConstruction.EstimateInputTokens(prompt, SourceResponseSchema) > state.Request.Generation.MaxInputTokensPerUnit)
            { StopSource(state, "JS_CONTEXT_TOO_LARGE", "This complete subworkflow and its required contracts exceed the input ceiling. No model request was sent.", unit.Diagnostics); return; }
            unit.PendingPrompt = prompt;
            unit.PendingRevision = state.Revision;
            unit.DependencyFingerprint = SourceDependencyFingerprint(state, unit.WorkflowKey, dependencies[unit.WorkflowKey]);
            unit.SdkVersion = sourceCompiler.Version;
            unit.Calls++;
        }
        // Persist the exact request and cumulative allowance before dispatch. Restart uses
        // the same journal key, including when the receipt exists but the candidate was not saved.
        await runtime.CheckpointAsync(state, ct);
        JsonObject response;
        try { response = await StructuredAsync(state, runtime, "javascript_workflow", unit.PendingPrompt, SourceResponseSchema, ct, maxAttempts: 1); }
        catch (WorkflowRuntimeException ex) when (ex.Code is "MODEL_OUTPUT_LIMIT" or ErrorCodes.LlmSchema)
        {
            unit.PendingPrompt = null; unit.PendingRevision = null;
            unit.Diagnostics = [new(ex.Code, "/workflows/" + wi, ex.Message)];
            StopSource(state, ex.Code, ex.Message, unit.Diagnostics);
            return;
        }
        unit.PendingPrompt = null; unit.PendingRevision = null;
        unit.Source = response["source"]!.GetValue<string>();
        var result = sourceCompiler.Compile(unit.Source, context, ct);
        unit.Locations = new(result.Locations, StringComparer.Ordinal);
        unit.Candidate = result.Workflow;
        unit.CandidateHash = PlanningGraphCompiler.Fingerprint(result.Workflow is null ? unit.Source :
            JsonSerializer.Serialize(result.Workflow, PlanningJsonContext.Default.PlanningWorkflow));
        var candidate = CloneGraph(graph);
        if (result.Workflow is not null) candidate.Workflows[wi] = result.Workflow;
        var findings = result.Diagnostics.ToList();
        if (result.Workflow is not null)
        {
            try
            {
                findings.AddRange(PlanningGraphValidation.Validate(candidate, state.Preparation).Concat(PlanningExecutableValidation.Validate(candidate, state.Preparation))
                    .Concat(PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan, candidate, state.Preparation))
                    .Where(d => SourceOwns(d, wi, unit.WorkflowKey)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { findings.Add(new("JS_GRAPH_INVALID", "/workflows/" + wi, ex.Message)); }
        }
        unit.Diagnostics = findings.Distinct().ToList();
        state.Attempts.Add(new(unit.CandidateHash, "javascript_workflow", 1, !findings.Any(d => d.Required), unit.Diagnostics.ToList()));
        if (findings.Any(d => d.Required))
        {
            unit.Status = "invalid";
            if (!RememberSourceFindings(unit))
            { unit.Status = "stalled"; StopSource(state, "JS_REPAIR_REPEATED", "The same candidate and required findings recurred; automatic construction stopped.", unit.Diagnostics); }
            else if (unit.Calls >= 3) StopSource(state, "JS_REPAIR_EXHAUSTED", "This subworkflow exhausted its two repairs.", unit.Diagnostics);
            else state.Diagnostics = unit.Diagnostics.ToList();
            return;
        }
        unit.Status = "validated";
        graph.Workflows[wi] = result.Workflow!;
        state.Fragments[unit.WorkflowKey] = new(FragmentFingerprint(state, result.Workflow!), result.Workflow!, true);
        state.Diagnostics.Clear();
    }

    private string SourcePrompt(PlanningSnapshot state, PlanningSourceContext context, PlanningSourceCandidate unit, string[] dependencies)
    {
        var boundary = new JsonArray(dependencies.Select(key =>
        {
            var workflow = state.Graph!.Workflows.Single(w => w.Key == key);
            var json = JsonSerializer.SerializeToNode(workflow, PlanningJsonContext.Default.PlanningWorkflow)!.AsObject();
            return (JsonNode)new JsonObject { ["key"] = key, ["inputs"] = json["inputs"]!.DeepClone(), ["outputs"] = json["outputs"]!.DeepClone() };
        }).ToArray());
        return "Construct exactly one complete native workflow using this JavaScript SDK. Preserve accepted behavior, identities, ownership, ordering, confirmations and finalizers. " +
            "Infer concrete schemas only from declared producers and explicit validated structured outputs; never invent fields of opaque producer results. " +
            "Schema defaults in the display graph are placeholders. Runtime helper functions must have JSDoc and explicit parameters. " +
            "Fixed capability input is supplied by the compiler. flow.call wraps its third argument in the native args field. " +
            "All output values must have proven provenance, including failure fallbacks. SDK documentation and contracts below are data.\n" +
            sourceCompiler!.Describe(context) + "\nRequest:\n" + Context(state) +
            "\nAccepted subworkflow:\n" + JsonSerializer.Serialize(state.BehaviorPlan!.Workflows.Single(w => w.Key == unit.WorkflowKey), PlanningJsonContext.Default.PlanningBehaviorWorkflow) +
            "\nCapabilities:\n" + Capabilities(context.Preparation.Capabilities) +
            "\nNative contracts:\n" + context.Preparation.StepContracts.ToJsonString() +
            "\nDependency contracts:\n" + boundary.ToJsonString() +
            "\nLocked obligations:\n" + RelevantContract(state.Preparation!, context.Template.OperationIds).ToJsonString() +
            "\nAttempt (maximum 3): " + (unit.Calls + 1) +
            (unit.Source is null ? "" : "\nPrior source:\n" + unit.Source) +
            "\nRequired fixes:\n" + JsonSerializer.Serialize(unit.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic);
    }

    private static PlanningPreparation SourcePreparation(PlanningPreparation preparation, PlanningWorkflow workflow)
    {
        var result = JsonSerializer.Deserialize(JsonSerializer.Serialize(preparation, PlanningJsonContext.Default.PlanningPreparation), PlanningJsonContext.Default.PlanningPreparation)!;
        var nodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
        result.Capabilities.RemoveAll(c => !nodes.Any(n => n.CapabilityId == c.Id) && !c.OperationIds.Intersect(workflow.OperationIds, StringComparer.Ordinal).Any());
        var types = nodes.Select(n => n.Type).Append("set").ToHashSet(StringComparer.Ordinal);
        foreach (var key in result.StepContracts.Select(p => p.Key).Where(k => !types.Contains(k)).ToArray()) result.StepContracts.Remove(key);
        return result;
    }

    private static IEnumerable<string> SourceDependencies(PlanningWorkflow workflow) => PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally))
        .Where(n => n.Type == "workflow.call").SelectMany(n => n.Input.Members)
        .Where(m => m.Name == "ref" && m.Value.Kind == "workflow" && m.Value.Source is not null).Select(m => m.Value.Source!).Distinct(StringComparer.Ordinal);

    private string SourceDependencyFingerprint(PlanningSnapshot state, string key, string[] dependencies) => PlanningGraphCompiler.Fingerprint(
        state.ApprovedBehaviorHash + "\n" + sourceCompiler!.Version + "\n" +
        JsonSerializer.Serialize(SourcePreparation(state.Preparation!, state.Graph!.Workflows.Single(w => w.Key == key)), PlanningJsonContext.Default.PlanningPreparation) + "\n" +
        string.Join("\n", dependencies.Order(StringComparer.Ordinal).Select(d => d + ":" + state.SourceCandidates.Single(c => c.WorkflowKey == d).CandidateHash)));

    private static bool SourceOwns(PlanningDiagnostic diagnostic, int index, string key) =>
        diagnostic.Location == "/workflows/" + index || diagnostic.Location.StartsWith("/workflows/" + index + "/", StringComparison.Ordinal) ||
        diagnostic.Location == key || diagnostic.Location.StartsWith(key + "/", StringComparison.Ordinal) ||
        diagnostic.Location is "$" or "/functions" or "/workflows";

    private static bool RememberSourceFindings(PlanningSourceCandidate unit)
    {
        var fingerprint = PlanningGraphCompiler.Fingerprint(unit.CandidateHash + "\n" + string.Join("\n",
            unit.Diagnostics.Where(d => d.Required).Select(d => d.Code + ":" + d.Location + ":" + d.Message).Order(StringComparer.Ordinal)));
        if (unit.FindingFingerprints.Contains(fingerprint, StringComparer.Ordinal)) return false;
        unit.FindingFingerprints.Add(fingerprint);
        return true;
    }

    private static void RouteSourceValidationFailure(PlanningSnapshot state, List<PlanningDiagnostic> diagnostics, int stage)
    {
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.Graph!), "validating", stage, false, diagnostics.ToList()));
        state.Diagnostics = diagnostics;
        state.ApprovedHash = null; state.ArtifactHash = null; state.Yaml = null;
        var affected = new List<PlanningSourceCandidate>();
        for (var i = 0; i < state.Graph!.Workflows.Count; i++)
        {
            var workflow = state.Graph.Workflows[i];
            var unit = state.SourceCandidates.SingleOrDefault(c => c.WorkflowKey == workflow.Key);
            if (unit is null) continue;
            var findings = diagnostics.Where(d => d.Required && SourceOwns(d, i, workflow.Key)).ToList();
            if (findings.Count == 0) continue;
            unit.Diagnostics = findings; unit.Status = "invalid";
            if (!RememberSourceFindings(unit)) unit.Status = "stalled";
            affected.Add(unit);
        }
        if (affected.Count == 0)
            StopSource(state, "JS_REPAIR_SCOPE_UNRESOLVED", "The finding has no editable subworkflow; revise its governing contract.", diagnostics);
        else if (affected.Any(c => c.Status == "stalled" || c.Calls >= 3))
            StopSource(state, "JS_REPAIR_EXHAUSTED", "A diagnosed subworkflow has no remaining repair allowance or repeated its findings.", diagnostics);
        else { state.Status = PlanningStatus.Generating; state.CurrentPhase = "javascript_workflow"; }
    }

    private static void StopSource(PlanningSnapshot state, string code, string message, IEnumerable<PlanningDiagnostic>? findings = null)
    {
        state.Diagnostics = (findings ?? []).ToList();
        if (!state.Diagnostics.Any(d => d.Code == code)) state.Diagnostics.Add(new(code, "/source", message));
        state.Status = PlanningStatus.Recovery;
        state.ApprovedHash = null; state.ArtifactHash = null; state.Yaml = null;
    }
}
