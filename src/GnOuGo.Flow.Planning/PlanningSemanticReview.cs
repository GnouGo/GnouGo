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
        var targets = SemanticTargets(graph);
        var shape = PlanningSchemas.Review(graph.Workflows.Select(w => w.Key), targets.Keys);
        var context = new JsonObject
        {
            ["sources"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject { ["sourceId"] = source.Id, ["kind"] = source.Kind, ["text"] = source.Text }).ToArray()),
            ["graph"] = JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph),
            ["capabilities"] = SemanticCapabilities(graph, state.Preparation!),
            ["values"] = SemanticValueContracts(graph, state.Preparation!),
            ["priorFindings"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)
        };
        var prompt = "Review the typed graph against the requested observable behavior. Return concrete findings with one exact nonempty excerpt from a supplied intent source. " +
            "Check effects, cardinality, ordering, confirmations, uncertainty, and cleanup. Choose an exact supplied workflow and location. " +
            "Use /input, /expr, /onError, or /functions for implementation defects; /behavior for a required topology change; /preparation for absent observations or unsuitable locked capabilities. " +
            "Producer schemas and metadata are authoritative. Never invent fields of opaque results or external API assumptions. " +
            "Typed node references are logical compiler-owned identifiers. Resolved value contracts establish their channels and scope. " +
            "Preserve every unresolved prior finding with its code and location. Machine findings and generated contracts are not user intent evidence. " +
            PlanningPromptContext.Instructions + "\n" + PlanningPromptContext.Share(context).ToJsonString();
        var fingerprint = PlanningGraphCompiler.Fingerprint(graph);
        if (state.Validation.Assessment.Fingerprint != fingerprint) state.Validation.Assessment = new() { Fingerprint = fingerprint };
        var assessment = state.Validation.Assessment;
        var invalid = assessment.Diagnostics; JsonObject? response = assessment.Candidate;
        for (var attempt = assessment.Attempts; attempt < 2; attempt++)
        {
            var repair = invalid.Count == 0 ? "" : "\nRepair the assessment contract only. Keep supported findings; do not modify the workflow.\nCandidate:\n" + response?.ToJsonString() + "\nInvalid fields:\n" + JsonSerializer.Serialize(invalid, PlanningJsonContext.Default.ListPlanningDiagnostic);
            if (PlanningJsonTransport.EstimateInputTokens(prompt + repair, shape) > state.Request.Generation.MaxInputTokensPerRequest)
                throw new SemanticAssessmentException([new("PREPARATION_REVIEW_CONTEXT_TOO_LARGE", "/preparation", "The affected observation assessment exceeds its configured context limit; no request was dispatched.")]);
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
                    var changes = await PlanningModelCalls.StructuredAsync(state, runtime, "semantic_review", patchPrompt, patch.Schema, ct);
                    response = patch.Apply(response, changes);
                }
                else response = await PlanningModelCalls.StructuredAsync(state, runtime, "semantic_review", prompt + repair, shape, ct);
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
        throw new SemanticAssessmentException(invalid);
    }

    internal static JsonObject SemanticCapabilities(PlanningGraph graph, PlanningPreparation preparation)
    {
        var owned = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Select(n => n.CapabilityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var schemas = new JsonObject();
        JsonObject Reference(JsonObject schema)
        {
            var id = "s_" + PlanningGraphCompiler.Fingerprint(schema.ToJsonString());
            if (!schemas.ContainsKey(id)) schemas[id] = schema.DeepClone();
            return new() { ["$ref"] = "#/schemas/" + id };
        }
        var capabilities = new JsonArray(preparation.Capabilities.Where(c => owned.Contains(c.Id)).Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["stepType"] = c.StepType,
            ["inputSchema"] = Reference(c.InputSchema),
            ["outputSchema"] = Reference(c.OutputSchema),
            ["fixedInput"] = c.FixedInput.DeepClone(),
            ["requestBindings"] = new JsonArray(c.RequestBindings.Select(b => (JsonNode)new JsonObject { ["path"] = b.Path, ["value"] = b.Value?.DeepClone() }).ToArray())
        }).ToArray());
        return new() { ["capabilities"] = capabilities, ["schemas"] = schemas };
    }

    internal sealed class SemanticAssessmentException(List<PlanningDiagnostic> diagnostics) : Exception
    { internal List<PlanningDiagnostic> Diagnostics { get; } = diagnostics; }

    private static Dictionary<string, (string Workflow, bool Behavior)> SemanticTargets(PlanningGraph graph)
    {
        var targets = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        for (var i = 0; i < graph.Workflows.Count; i++)
        {
            var workflow = graph.Workflows[i]; var root = "/workflows/" + i;
            targets[root + "/behavior"] = (workflow.Key, true);
            targets[root + "/functions"] = (workflow.Key, false);
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                targets[path + "/behavior"] = (workflow.Key, true);
                targets[path + "/preparation"] = (workflow.Key, false);
                foreach (var field in new[] { "input", "onError", "outputSchema", "structuredOutput" }) targets[path + "/" + field] = (workflow.Key, false);
                if (node.Expr is not null || node.Type == "switch") targets[path + "/expr"] = (workflow.Key, false);
            }
        }
        return targets;
    }
}
