using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    private const int TechnicalHistoryBytes = 6144;

    // Called only at the deterministic preflight boundary, before business
    // progress findings. Retained observations grant no current repair authority.
    internal static void RetainTechnicalRejections(PlanningSession state)
    {
        if (state.Construction?.Candidate is not { Diagnostics.Count: > 0 } candidate) return;
        var retained = candidate.Reviews.SingleOrDefault(review => review["technicalRejections"] is JsonObject);
        if (retained is null)
        {
            retained = new() { ["technicalRejections"] = new JsonObject { ["entries"] = new JsonArray(), ["omitted"] = 0 } };
            candidate.Reviews.Add(retained);
        }
        var history = retained["technicalRejections"]!.AsObject();
        var entries = history["entries"]!.AsArray();
        var sourceHash = DefectSources(state);
        foreach (var diagnostic in candidate.Diagnostics.Distinct())
        {
            var target = TechnicalTarget(candidate.Plan, diagnostic.Location, out var value, out var available);
            var observation = new JsonObject
            {
                ["baseline"] = candidate.BaseHash, ["sourceAndContractsHash"] = sourceHash,
                ["diagnostic"] = DefectSnapshot(Node(new List<PlanningDiagnostic> { diagnostic }, PlanningJsonContext.Default.ListPlanningDiagnostic)![0]),
                ["target"] = DefectSnapshot(target), ["valueAvailable"] = available,
                ["rejectedValue"] = available ? DefectSnapshot(value) : null
            };
            var id = DefectHash(observation);
            if (entries.Any(entry => entry?["id"]?.ToString() == id)) continue;
            observation["id"] = id;
            observation["candidate"] = candidate.Hash;
            observation["candidateIdentity"] = FailureIdentity(state, candidate);
            entries.Add((JsonNode)observation);
            BoundTechnicalHistory(history);
        }
    }

    internal static JsonObject? TechnicalRejectionContext(PlanningSession state)
    {
        var candidate = state.Construction!.Candidate;
        if (candidate is { Diagnostics.Count: 0 }) return null;
        var reviews = candidate?.Reviews ?? ReplacementDefectReviews(state);
        if (reviews.SingleOrDefault(review => review["technicalRejections"] is JsonObject)?["technicalRejections"] is not JsonObject retained) return null;
        var context = retained.DeepClone().AsObject();
        context["meaning"] = "Historical deterministic rejections, not current failures, business findings, or edit authority. Current diagnostics and the issued repair contract govern this attempt; changed values still require validation.";
        foreach (var observation in context["entries"]!.AsArray().OfType<JsonObject>())
        {
            var sameBaseline = observation["baseline"]?.ToString() == Hash(state.Plan) &&
                (candidate is null || candidate.BaseHash == Hash(state.Plan));
            var sameSource = observation["sourceAndContractsHash"]?.ToString() == DefectSources(state);
            var location = observation["diagnostic"]?["value"]?["location"]?.ToString();
            var target = candidate is not null && location is not null ? TechnicalTarget(candidate.Plan, location, out _, out _) : null;
            observation["sameBaseline"] = sameBaseline;
            observation["sameSourceAndContracts"] = sameSource;
            observation["sameTarget"] = target is not null && DefectHash(target) == observation["target"]?["hash"]?.ToString();
            observation["status"] = !sameBaseline || !sameSource ? "stale_basis" : target is null || observation["sameTarget"]!.GetValue<bool>() == false
                ? "target_unverified" : "historical";
        }
        BoundTechnicalHistory(context);
        return context;
    }

    private static void BoundTechnicalHistory(JsonObject history)
    {
        var entries = history["entries"]!.AsArray();
        while (entries.Count > 3 || Encoding.UTF8.GetByteCount(history.ToJsonString()) > TechnicalHistoryBytes)
        {
            if (entries.Count == 0) break;
            entries.RemoveAt(0);
            history["omitted"] = history["omitted"]!.GetValue<int>() + 1;
        }
    }

    private static JsonObject? TechnicalTarget(TaskPlan plan, string location, out JsonNode? value, out bool available)
    {
        var symbols = new TaskPlanSymbols(plan);
        // Exact compiler locations name tasks and arguments, never array offsets.
        // Unsupported or ambiguous locations retain the diagnostic without a value.
        available = symbols.Values.TryGetValue(location, out var binding);
        value = available ? PlanningJsonTransport.TaskPlanPart(Node(binding!.Value, PlanningJsonContext.Default.TaskValue)) : null;
        if (!available) return null;
        var taskId = location.Split('/') is ["", "tasks", var id, ..] ? id : null;
        var task = taskId is not null ? symbols.Tasks.GetValueOrDefault(taskId).Task : null;
        return new JsonObject
        {
            ["location"] = location, ["scope"] = binding!.Scope.Path,
            ["task"] = taskId, ["phase"] = task is null ? null : symbols.Phase(taskId!),
            ["kind"] = task?.Kind, ["operation"] = task?.Operation
        };
    }
}
