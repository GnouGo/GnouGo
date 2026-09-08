using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Revisit synthesized contracts when a repaired consumer still cannot bind a locked dependency.</summary>
internal static class PlanningProducerRepair
{
    internal const string DiagnosticCode = "PRODUCER_CONSUMER_CONTRACT_REVIEW";

    internal static bool Schedule(PlanningSnapshot state)
    {
        var graph = state.Graph!; var preparation = state.Preparation!;
        foreach (var consumer in state.ConstructionUnits.Where(u => u.Kind == "implementation" && u.Status is not ("validated" or "superseded") &&
            u.RepairCalls > 0 && !u.WaitingForProducerReview && u.Candidate is not null && u.Diagnostics.Any(d => d.Code is "OPERATION_INPUT_BINDING_MISSING" or "BUSINESS_INPUT_BINDING_MISSING")))
        {
            PlanningGraph candidate;
            try { candidate = PlanningConstruction.Apply(graph, consumer, consumer.Candidate!, preparation); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { continue; }
            var workflow = candidate.Workflows.Single(w => w.Key == consumer.WorkflowKey);
            var located = PlanningGraphValidation.Located(workflow.Steps, "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, "/finally")).ToArray();
            var missing = consumer.NodeKeys.SelectMany(key =>
            {
                var node = located.Single(p => p.Node.Key == key).Node;
                return PlanningOperationCompositions.RequiredInputs(workflow, node, preparation)
                    .Except(PlanningDataflow.OperationDependencies(workflow, node, preparation, candidate).Operations, StringComparer.Ordinal);
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var accepted = state.BehaviorPlan?.Workflows.FirstOrDefault(w => w.Key == workflow.Key);
            var businessInputs = accepted is null ? new Dictionary<string, string[]>(StringComparer.Ordinal) : PlanningBehaviorPlans.Enumerate(accepted.Steps.Concat(accepted.Finally))
                .Where(n => consumer.NodeKeys.Contains(n.Key)).Select(n => (n.Key, Missing: (n.InputDependencies ?? []).Except(PlanningDataflow.BusinessInputs(workflow, located.Single(p => p.Node.Key == n.Key).Node), StringComparer.Ordinal).ToArray()))
                .Where(n => n.Missing.Length > 0).ToDictionary(n => n.Key, n => n.Missing, StringComparer.Ordinal);
            if (missing.Length == 0 && businessInputs.Count == 0) continue;
            var fingerprint = PlanningGraphCompiler.Fingerprint(consumer.Candidate!.ToJsonString() + "\n" + string.Join("\n", missing) + "\n" +
                string.Join("\n", businessInputs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + string.Join(",", p.Value.Order(StringComparer.Ordinal)))));
            if (consumer.ConsumerContractReviews.Contains(fingerprint) || consumer.ConsumerContractReviews.Count >= state.Request.MaxRepairs) continue;
            // A container's operation owns the contributions of its children. Catalog
            // results are immutable; only already declared synthesized results qualify.
            var firstConsumer = Array.FindIndex(located, p => consumer.NodeKeys.Contains(p.Node.Key));
            var contributors = located.Take(firstConsumer).Where(p => p.Node.StructuredOutput is not null &&
                located.Any(parent => (p.Path == parent.Path || p.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal)) &&
                    parent.Node.OperationIds.Concat(preparation.Capabilities.FirstOrDefault(c => c.Id == parent.Node.CapabilityId)?.OperationIds ?? []).Intersect(missing).Any()))
                .Select(p => p.Node.Key).ToHashSet(StringComparer.Ordinal);
            // A native transformation can also have an incomplete result contract.
            // Revisit only its own extensible inline object, never a catalog schema
            // or a field fabricated merely to satisfy dependency counting.
            contributors.UnionWith(located.Where(p => businessInputs.ContainsKey(p.Node.Key) && p.Node.Type == "set" &&
                p.Node.OutputSchema is { CapabilityId: null, Type: "object" }).Select(p => p.Node.Key));
            var producers = state.ConstructionUnits.Where(u => u.WorkflowKey == consumer.WorkflowKey && u.Kind == "contracts" && u.Status == "validated" &&
                u.Candidate is not null && u.NodeKeys.Any(contributors.Contains)).ToArray();
            if (producers.Length == 0) continue;
            consumer.ConsumerContractReviews.Add(fingerprint); consumer.WaitingForProducerReview = true;
            foreach (var producer in producers)
            {
                producer.ProducerReviewBaseline = producer.Candidate!.DeepClone().AsObject();
                producer.Status = "invalid"; producer.RepairCallsAtRetry = producer.RepairCalls; producer.DispatchDiagnostics.Clear();
                var wi = graph.Workflows.FindIndex(w => w.Key == producer.WorkflowKey);
                var original = graph.Workflows[wi];
                producer.Diagnostics = PlanningGraphValidation.Located(original.Steps, "/workflows/" + wi + "/steps")
                    .Concat(PlanningGraphValidation.Located(original.Finally, "/workflows/" + wi + "/finally"))
                    .Where(p => producer.NodeKeys.Contains(p.Node.Key) && contributors.Contains(p.Node.Key))
                    .Select(p => new PlanningDiagnostic(DiagnosticCode, p.Path + (p.Node.Type == "set" ? "/outputSchema" : "/structuredOutput/schema"),
                        "Consumer " + string.Join(", ", consumer.NodeKeys) + " still cannot consume required operations [" + string.Join(", ", missing) + "] and accepted business inputs [" + string.Join(", ", businessInputs.SelectMany(p => p.Value).Distinct(StringComparer.Ordinal)) + "]" +
                        ". Review this producer's contribution against its accepted input dependencies, purpose and declared consumer argument contracts. Add only fields that this operation can establish and its behavior or consumers need. Do not add an unused input copy to bypass dependency validation. Preserve every existing field, type, constraint and requiredness. " +
                        "Return the existing schema unchanged if this producer cannot supply the missing data; do not copy values from unrelated sources or invent observations. The consumer must still establish its dependency after this review.", ValidationStage: "dataflow")).ToList();
                if (!consumer.Dependencies.Contains(producer.Key)) consumer.Dependencies.Add(producer.Key);
            }
            state.Attempts.Add(new(fingerprint, "producer_contract_review_scheduled", 0, false, consumer.Diagnostics.ToList()));
            return true; // Persist one dependency closure before another model request.
        }
        return false;
    }

    internal static void ResumeConsumers(PlanningSnapshot state)
    {
        foreach (var unit in state.ConstructionUnits.Where(u => u.Status != "superseded" && u.WaitingForProducerReview && u.Dependencies.All(k => state.ConstructionUnits.Single(p => p.Key == k).Status == "validated")))
        {
            unit.WaitingForProducerReview = false; unit.RepairCallsAtRetry = unit.RepairCalls;
            unit.Status = "invalid"; unit.DispatchDiagnostics.Clear();
            if (unit.Candidate is not null && state.Graph is { } graph && state.Preparation is { } preparation)
            {
                var workflow = graph.Workflows.Single(w => w.Key == unit.WorkflowKey);
                var shape = PlanningConstruction.ShapeFindings(unit.Candidate, PlanningConstruction.Schema(workflow, unit, preparation, graph), unit);
                // Added contract fields need implementation first. Preserve valid
                // existing coordinates instead of resending the old whole-input finding.
                if (shape.Count > 0) unit.Diagnostics = shape;
            }
        }
    }

    internal static void Preserve(JsonObject baseline, JsonObject candidate)
    {
        Check(baseline, candidate);
        static void Check(JsonNode? before, JsonNode? after)
        {
            if (JsonNode.DeepEquals(before, after)) return;
            if (before is not JsonObject oldObject || after is not JsonObject newObject || !oldObject.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(newObject.Select(p => p.Key)))
                throw new InvalidOperationException("Producer review may add schema properties but cannot remove or change existing declarations.");
            foreach (var (key, value) in oldObject)
            {
                if (key == "properties" && oldObject["kind"]?.ToString() == "inline" && oldObject["type"]?.ToString() == "object" && value is JsonArray oldFields && newObject[key] is JsonArray newFields)
                {
                    var fields = newFields.OfType<JsonObject>().ToArray();
                    if (fields.Length != newFields.Count || fields.Any(f => f["name"] is null) || fields.Select(f => f["name"]!.ToString()).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                        throw new InvalidOperationException("Producer review contains invalid or duplicate properties.");
                    foreach (var field in oldFields) Check(field, fields.SingleOrDefault(f => f["name"]!.ToString() == field?["name"]?.ToString()));
                }
                else Check(value, newObject[key]);
            }
        }
    }
}
