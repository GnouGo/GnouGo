using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningSemanticReview
{

    internal async Task<List<PlanningDiagnostic>> ReviewAsync(PlanningSnapshot state, PlanningGraph graph, IPlanningRuntime runtime, CancellationToken ct)
    {
        var sources = PlanningIntentAssessment.IntentSources(state);
        var targets = SemanticTargets(graph, state.Preparation!);
        var shape = PlanningSchemas.Review(graph.Workflows.Select(w => w.Key), targets.Keys);
        var context = new JsonObject
        {
            ["sources"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject { ["sourceId"] = source.Id, ["kind"] = source.Kind, ["text"] = source.Text }).ToArray()),
            ["graph"] = PlanningSemanticContext.Executable(graph),
            ["capabilities"] = SemanticCapabilities(graph, state.Preparation!),
            ["values"] = SemanticValueContracts(graph, state.Preparation!),
            ["priorFindings"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)
        };
        var prompt = "Review the typed graph against the requested observable behavior. Return concrete findings with one exact nonempty excerpt from a supplied intent source. " +
            "Check effects, cardinality, ordering, confirmations, uncertainty, and cleanup. Choose an exact supplied workflow and location. " +
            "Locate implementation defects at the exact supplied executable field; use /behavior for a required topology or established contract change and /preparation for unsuitable locked capabilities. " +
            "Producer schemas and metadata are authoritative. Never invent fields of opaque results or external API assumptions. " +
            "Workflow sequences contain node IDs from their nodes index. Each node's location is its authoritative scope; a sibling after a switch runs after every selected outcome. Resolved value contracts establish reference channels and scope. " +
            "Preserve every unresolved prior finding with its code and location. Machine findings and generated contracts are not user intent evidence. " +
            PlanningPromptContext.Instructions + "\n" + PlanningPromptContext.Share(context).ToJsonString();
        var fingerprint = PlanningGraphCompiler.Fingerprint(graph);
        if (state.Validation.Assessment.Fingerprint != fingerprint) state.Validation.Assessment = new() { Fingerprint = fingerprint };
        var assessment = state.Validation.Assessment;
        var invalid = assessment.Diagnostics; JsonObject? response = assessment.Candidate;
        while (true)
        {
            var tokens = PlanningJsonTransport.EstimateInputTokens(prompt, shape);
            if (response is null && tokens > state.Request.Generation.MaxInputTokensPerRequest)
                throw new SemanticAssessmentException([new("SEMANTIC_REVIEW_CONTEXT_TOO_LARGE", "/semanticReview", "The semantic assessment needs approximately " + tokens + " input tokens; the ceiling is " + state.Request.Generation.MaxInputTokensPerRequest + ". No request was dispatched.")]);
            try
            {
                if (response is not null && invalid.Count > 0)
                {
                    var patch = new PlanningReviewEvidenceRepair(response, invalid,
                        sources.SelectMany(source => source.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)),
                        targets.ToDictionary(p => p.Key, p => p.Value.Workflow, StringComparer.Ordinal));
                    var patchPrompt = "Repair the assessment contract only. Select an exact allowed value for each diagnosed field. " +
                        "All other findings and fields are locked. Keep the requirement associated with each finding; never infer intent from model-written questions or generated contracts. " +
                        "\nAffected findings:\n" + patch.Context.ToJsonString() + "\nAuthoritative sources and roles:\n" + context["sources"]!.ToJsonString();
                    if (PlanningJsonTransport.EstimateInputTokens(patchPrompt, patch.Schema) > state.Request.Generation.MaxInputTokensPerRequest)
                        throw new SemanticAssessmentException([new("SEMANTIC_REVIEW_CONTEXT_TOO_LARGE", "/semanticReview", "The focused assessment repair exceeds its input ceiling; no request was dispatched.")]);
                    var changes = await RequestAsync(state, runtime, patchPrompt, patch.Schema, true, ct);
                    response = patch.Apply(response, changes);
                }
                else response = await RequestAsync(state, runtime, (assessment.Attempts > 0 ? "Repair the assessment contract only.\n" : "") + prompt, shape, assessment.Attempts > 0, ct);
            }
            catch (WorkflowRuntimeException ex) when (ex.Code == ErrorCodes.LlmSchema)
            {
                assessment.Attempts++;
                if (invalid.Count == 0) invalid = [new("SEMANTIC_REVIEW_INVALID", "/semanticReview", "The semantic assessment response did not match its declared schema.")];
                assessment.Diagnostics = invalid; await runtime.CheckpointAsync(state, ct); continue;
            }
            assessment.Attempts++; assessment.Candidate = response;
            invalid.Clear(); var diagnostics = new List<PlanningDiagnostic>(); var index = 0;
            foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
            {
                var workflow = finding["workflow"]!.GetValue<string>();
                var location = finding["location"]!.GetValue<string>();
                var evidence = finding["evidence"]!.GetValue<string>();
                if (string.IsNullOrWhiteSpace(evidence) || !sources.Any(source => source.Text.Contains(evidence, StringComparison.Ordinal)))
                    invalid.Add(new("SEMANTIC_REVIEW_EVIDENCE_INVALID", "/semanticReview/findings/" + index + "/evidence", "Copy one exact excerpt from the supplied request for this finding. Model assessment failures cannot justify executable changes."));
                if (targets[location].Workflow != workflow)
                    invalid.Add(new("SEMANTIC_REVIEW_LOCATION_INVALID", "/semanticReview/findings/" + index + "/location", "The target must belong to the named workflow."));
                diagnostics.Add(new(finding["code"]!.GetValue<string>(), location, finding["message"]!.GetValue<string>(), finding["blocking"]!.GetValue<bool>()));
                index++;
            }
            if (invalid.Count == 0) { state.Validation.Assessment = new(); return diagnostics; }
            assessment.Diagnostics = invalid; await runtime.CheckpointAsync(state, ct);
        }
    }

    private static async Task<JsonObject> RequestAsync(PlanningSnapshot state, IPlanningRuntime runtime, string prompt, JsonObject schema, bool repair, CancellationToken ct)
    {
        const string owner = "$plan";
        const string phase = "semantic_review";
        var pending = state.Construction.PendingCalls.Any(c => c.Phase == phase && c.WorkflowKey == owner);
        if (repair && !pending && PlanningRepairAllowances.Get(state, owner, PlanningGates.Response).Attempts >= state.Request.MaxRepairsPerWorkflowGate)
            throw new SemanticAssessmentException(state.Validation.Assessment.Diagnostics.Concat(new[] { new PlanningDiagnostic("REPAIR_EXHAUSTED", "/semanticReview", "The assessment response repair allowance is exhausted.") }).ToList());
        var sequence = state.Construction.ModelSequence;
        var call = PlanningModelCalls.Reserve(state, phase, owner, PlanningModelCalls.Request(state, prompt, schema), PlanningGates.Semantic,
            PlanningGraphCompiler.Fingerprint(state.Validation.Assessment.Fingerprint + schema.ToJsonString()));
        if (repair && sequence != state.Construction.ModelSequence) PlanningRepairAllowances.Reserved(state, owner, PlanningGates.Response);
        await runtime.CheckpointAsync(state, ct);
        var response = await PlanningModelCalls.DispatchAsync(state, runtime, call, ct);
        state.Construction.PendingCalls.Remove(call);
        PlanningModelCalls.RequireComplete(response, call.Request.MaxTokens);
        if (response.Json is not JsonObject json || PlanningContractValidation.ValidateInstance(json, schema).Count != 0)
            throw new WorkflowRuntimeException(ErrorCodes.LlmSchema, "The semantic assessment did not match its exact response contract.");
        return json;
    }

    internal static JsonObject SemanticCapabilities(PlanningGraph graph, PlanningPreparation preparation)
    {
        var owned = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Select(n => n.CapabilityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var schemas = new JsonObject(); var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject Reference(JsonObject schema)
        {
            var fingerprint = schema.ToJsonString();
            if (!identities.TryGetValue(fingerprint, out var id))
            { id = "c" + identities.Count; identities[fingerprint] = id; schemas[id] = schema.DeepClone(); }
            return new() { ["$ref"] = "#/schemas/" + id };
        }
        var capabilities = new JsonArray(preparation.Capabilities.Where(c => owned.Contains(c.Id)).Select(c =>
        {
            var contract = new JsonObject { ["id"] = c.Id, ["stepType"] = c.StepType, ["resolution"] = c.Resolution };
            // A local operation uses the graph's established typed value contracts.
            // The native executor's generic definition is not a business-output schema.
            if (c.Resolution != "local")
            {
                contract["inputSchema"] = Reference(c.InputSchema);
                contract["outputSchema"] = Reference(c.OutputSchema);
            }
            if (c.FixedInput.Count > 0) contract["fixedInput"] = c.FixedInput.DeepClone();
            if (c.RequestBindings.Count > 0)
                contract["requestBindings"] = new JsonArray(c.RequestBindings.Select(b => (JsonNode)new JsonObject { ["path"] = b.Path, ["value"] = b.Value?.DeepClone() }).ToArray());
            return (JsonNode)contract;
        }).ToArray());
        return new() { ["capabilities"] = capabilities, ["schemas"] = schemas };
    }

    internal static bool ReassessInvalidTargets(PlanningSnapshot state)
    {
        if (state.Graph is null || state.Preparation is null || state.BehaviorPlan is null || state.Validation.Stage != 4 ||
            state.Construction.PendingCalls.Count != 0 ||
            state.Validation.GraphFingerprint != PlanningGraphCompiler.Fingerprint(state.Graph) ||
            state.Validation.ContractFingerprint != PlanningContext.Contracts(state) ||
            state.Validation.FixtureFingerprint != PlanningContext.Fixtures(state) ||
            state.ApprovedBehaviorHash != PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan)) return false;
        var findings = state.Diagnostics.Where(d => d.Required && d.Code != "GOVERNING_CONTRACT_REVIEW_REQUIRED").ToArray();
        var targets = SemanticTargets(state.Graph, state.Preparation);
        if (findings.Length == 0 || findings.Any(d => !d.Location.EndsWith("/preparation", StringComparison.Ordinal) || targets.ContainsKey(d.Location))) return false;
        // Reassess an invalid review contract, with no executable write permission.
        // Earlier gates and fixtures are checked again; existing allowances persist.
        state.Validation.Assessment = new()
        {
            Fingerprint = state.Validation.GraphFingerprint,
            Attempts = 1,
            Diagnostics = [new("SEMANTIC_REVIEW_LOCATION_INVALID", "/semanticReview", "The findings must use the current authoritative review targets.")]
        };
        return true;
    }

    internal sealed class SemanticAssessmentException(List<PlanningDiagnostic> diagnostics) : Exception
    { internal List<PlanningDiagnostic> Diagnostics { get; } = diagnostics; }

    internal static Dictionary<string, (string Workflow, bool Behavior)> SemanticTargets(PlanningGraph graph, PlanningPreparation preparation)
    {
        var targets = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        for (var i = 0; i < graph.Workflows.Count; i++)
        {
            var workflow = graph.Workflows[i]; var root = "/workflows/" + i;
            targets[root + "/behavior"] = (workflow.Key, true);
            var fields = new List<PlanningDiagnostic>();
            for (var pi = 0; pi < workflow.Inputs.Count; pi++)
                fields.Add(new("scope", root + "/inputs/" + pi + "/default", ""));
            for (var pi = 0; pi < workflow.Outputs.Count; pi++)
                fields.Add(new("scope", root + "/outputs/" + pi + "/value", ""));
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                targets[path + "/behavior"] = (workflow.Key, true);
                if (node.CapabilityId is { } capabilityId && preparation.Capabilities.Any(c => c.Id == capabilityId && c.Resolution != "local"))
                    targets[path + "/preparation"] = (workflow.Key, false);
                if (node.InternalRole is not null) continue;
                foreach (var field in new[] { "input", "expr" }) fields.Add(new("scope", path + "/" + field, ""));
            }
            foreach (var target in PlanningPatches.Targets(graph, PlanningPatches.Scope(graph, fields)))
                targets[target.Path] = (workflow.Key, false);
        }
        return targets;
    }
}
