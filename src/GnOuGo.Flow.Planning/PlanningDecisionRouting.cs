using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningDecisionRouting
{
    private const string BooleanGuard = "if (typeof condition !== 'boolean') throw new Error('Decision conditions must return a boolean.'); ";

    internal static IEnumerable<PlanningDiagnostic> ConditionFindings(PlanningNode node, PlanningPreparation preparation, string location)
    {
        if (LocalContracts(node, preparation).Length == 0) yield break;
        JsonObject conditions;
        try { conditions = ConditionsValues(node, preparation); }
        catch (InvalidOperationException) { yield break; }
        foreach (var (field, outcomes) in conditions)
            foreach (var (_, condition) in outcomes!.AsObject())
            {
                var value = JsonSerializer.Deserialize(condition!, PlanningJsonContext.Default.PlanningValue)!;
                if (value.Kind is "string" or "number" or "null" or "object" or "array" or "template" ||
                    value.Kind == "compute" && PlanningComputations.HasNonBooleanResult(value.Text))
                    yield return new("DECISION_CONDITION_INVALID", location + "/input/decisions/" + field,
                        "Each outcome condition must return a boolean, never an outcome label or undefined. Routing assigns the outcome label and enforces human permission separately. Preserve every declared analysis dependency.");
            }
    }

    internal static PlanningDecisionContract? Contract(PlanningNode node, PlanningPreparation preparation)
    {
        if (node.Type != "switch") return null;
        var matches = preparation.Decisions.Where(d =>
            node.Cases.Any(c => d.AllowedValues.Except(d.NoEffectValues).Contains(c.Value) &&
                PlanningGraphCompiler.Enumerate(c.Steps).Any(n => n.OperationIds.Intersect(d.EffectOperationIds).Any()))).OrderBy(d => d.Group, StringComparer.Ordinal).ToArray();
        if (matches.Length == 0) return null;
        var first = matches[0];
        if (matches.Any(d => d.Version != first.Version || d.SourceOperationId != first.SourceOperationId || d.SourceCapabilityId != first.SourceCapabilityId ||
            d.SourcePointer != first.SourcePointer || d.ContractSource != first.ContractSource || !JsonNode.DeepEquals(d.ResponseSchema, first.ResponseSchema) ||
            !Same(d.AllowedValues, first.AllowedValues) || !Same(d.NoEffectValues, first.NoEffectValues) || !Same(d.PermissionOperationIds, first.PermissionOperationIds)))
            throw new AmbiguousDecisionException(node.Key);
        return new()
        {
            Version = first.Version,
            Group = string.Join(",", matches.Select(d => d.Group)),
            SourceOperationId = first.SourceOperationId,
            SourceCapabilityId = first.SourceCapabilityId,
            SourcePointer = first.SourcePointer,
            ContractSource = first.ContractSource,
            ResponseSchema = first.ResponseSchema.DeepClone().AsObject(),
            AllowedValues = first.AllowedValues.ToList(),
            NoEffectValues = first.NoEffectValues.ToList(),
            EffectOperationIds = matches.SelectMany(d => d.EffectOperationIds).Distinct(StringComparer.Ordinal).ToList(),
            InputOperationIds = matches.SelectMany(d => d.InputOperationIds).Distinct(StringComparer.Ordinal).ToList(),
            PermissionOperationIds = first.PermissionOperationIds.ToList()
        };

        static bool Same(IEnumerable<string> left, IEnumerable<string> right) => left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal));
    }

    internal sealed class AmbiguousDecisionException(string node) : InvalidOperationException(
        "Decision '" + node + "' combines different locked decision sources or permission outcomes. Preserve separate gates or establish an explicit typed reducer consuming every required permission; one source cannot substitute for another.");

    internal static PlanningValue Resolve(PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation, PlanningGraph graph)
    {
        var contract = Contract(node, preparation) ?? throw new InvalidOperationException("Missing locked confirmation decision.");
        if (contract.ContractSource != PlanningDecisionContract.HumanConfirmation)
        {
            var path = contract.SourcePointer.Split('/').Skip(1).Select(Decode).ToList();
            var channel = contract.ContractSource == "structured_output" ? "structured" : "default";
            if (channel == "structured" && path.FirstOrDefault() == "json") path.RemoveAt(0);
            var binding = PlanningDataflow.Index(workflow, preparation, graph, node.Key).Values.FirstOrDefault(b =>
                b.Value.Kind == "output" && (b.Value.ResultChannel ?? "default") == channel && b.Value.Path.SequenceEqual(path) &&
                PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Any(n => n.Key == b.Value.Source && n.CapabilityId == contract.SourceCapabilityId && n.OperationIds.Contains(contract.SourceOperationId)));
            if (binding is null) throw new InvalidOperationException("The exact declared decision producer is unavailable. Establish its result contract before routing its outcomes.");
            return new() { Kind = "decision_binding", Items = [binding.Value] };
        }
        var bindings = PlanningDataflow.Index(workflow, preparation, graph, node.Key).Values.Where(b =>
            PlanningValueProvenance.Proves(workflow, b.Value, graph, (producer, value) => producer.Type == "human.input"
                && (contract.SourceCapabilityId.Length == 0 || producer.CapabilityId == contract.SourceCapabilityId)
                && producer.OperationIds.Contains(contract.SourceOperationId) && value.Path.SequenceEqual(new[] { "response" }))).ToArray();
        if (bindings.Length == 0) throw new InvalidOperationException("The declared human confirmation is not available at this decision. Preserve its execution scope and route its exact result through an explicit boundary.");
        return new()
        {
            Kind = "confirmation",
            Text = contract.AllowedValues.Except(contract.NoEffectValues).Single(),
            Source = contract.NoEffectValues.Single(),
            Items = [bindings.OrderBy(b => b.Value.Path.Count).ThenBy(b => b.Id, StringComparer.Ordinal).First().Value]
        };
    }

    internal static PlanningDecisionContract[] LocalContracts(PlanningNode node, PlanningPreparation preparation) => node.Type != "decision.evaluate" ? [] :
        preparation.Decisions.Where(d => d.ContractSource == "local_decision" && d.SourceCapabilityId == node.CapabilityId && node.OperationIds.Contains(d.SourceOperationId)).ToArray();

    internal static JsonObject ConditionsSchema(PlanningNode node, PlanningPreparation preparation)
    {
        static JsonObject Object(JsonObject properties) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray())
        };
        return Object(new JsonObject(LocalContracts(node, preparation).Select(d => new KeyValuePair<string, JsonNode?>(Field(d),
            Object(new JsonObject(d.AllowedValues.Except(d.NoEffectValues).Select(value => new KeyValuePair<string, JsonNode?>(value, new JsonObject { ["$ref"] = "#/$defs/value" }))))))));
    }

    internal static void ApplyConditions(PlanningWorkflow workflow, PlanningNode node, JsonObject conditions, PlanningPreparation preparation, PlanningGraph graph)
    {
        var decisions = new PlanningValue { Kind = "object" };
        foreach (var contract in LocalContracts(node, preparation))
        {
            var cases = new PlanningValue { Kind = "array" };
            foreach (var outcome in contract.AllowedValues.Except(contract.NoEffectValues))
            {
                var condition = JsonSerializer.Deserialize(conditions[Field(contract)]![outcome]!, PlanningJsonContext.Default.PlanningValue)!;
                var permissions = contract.PermissionOperationIds.Count > 0 ? contract.PermissionOperationIds : contract.InputOperationIds.Where(id =>
                    preparation.Capabilities.Any(c => c.StepType == "human.input" && c.OperationIds.Contains(id))).ToList();
                {
                    var members = new List<PlanningMember> { new("condition", condition) };
                    foreach (var operation in permissions)
                    {
                        var binding = PlanningDataflow.Index(workflow, preparation, graph, node.Key).Values.FirstOrDefault(b =>
                            PlanningValueProvenance.Proves(workflow, b.Value, graph, (producer, value) => producer.Type == "human.input" &&
                                producer.OperationIds.Contains(operation) && value.Path.SequenceEqual(new[] { "response" })));
                        if (binding is null) throw new InvalidOperationException("A required human permission is unavailable to the declared decision reducer.");
                        members.Add(new("permission" + members.Count, binding.Value));
                    }
                    condition = new() { Kind = "compute", Members = members, Text = BooleanGuard + "return " + string.Join(" && ", members.Select(m => m.Name + " === true")) + ";" };
                }
                cases.Items.Add(new() { Kind = "object", Members = [new("when", condition), new("value", PlanningJsonTransport.Literal(JsonValue.Create(outcome)))] });
            }
            var fields = new List<PlanningMember> { new("allowed_values", PlanningJsonTransport.Literal(new JsonArray(contract.AllowedValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()))), new("cases", cases) };
            if (contract.NoEffectValues.Count == 1) fields.Add(new("default", PlanningJsonTransport.Literal(JsonValue.Create(contract.NoEffectValues[0]))));
            decisions.Members.Add(new(Field(contract), new() { Kind = "object", Members = fields }));
        }
        node.Input = new() { Kind = "object", Members = [new("decisions", decisions)] }; node.OnError.Clear();
    }

    internal static JsonObject ConditionsValues(PlanningNode node, PlanningPreparation preparation)
    {
        var result = new JsonObject();
        var decisions = node.Input.Members.Single(m => m.Name == "decisions").Value;
        foreach (var contract in LocalContracts(node, preparation))
        {
            var fields = new JsonObject(); var declaration = decisions.Members.Single(m => m.Name == Field(contract)).Value;
            var cases = declaration.Members.Single(m => m.Name == "cases").Value.Items;
            foreach (var outcome in contract.AllowedValues.Except(contract.NoEffectValues))
            {
                var item = cases.Single(c => c.Members.Any(m => m.Name == "value" && m.Value.Text == outcome));
                var condition = item.Members.Single(m => m.Name == "when").Value;
                if (condition.Kind == "compute" && condition.Members.Select(m => m.Name).SequenceEqual(condition.Members.Select((_, i) => i == 0 ? "condition" : "permission" + i)) &&
                    (condition.Text == "return " + string.Join(" && ", condition.Members.Select(m => m.Name + " === true")) + ";" || condition.Text == BooleanGuard + "return " + string.Join(" && ", condition.Members.Select(m => m.Name + " === true")) + ";") && condition.Members.Count > 0)
                    condition = condition.Members[0].Value;
                fields[outcome] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(condition, PlanningJsonContext.Default.PlanningValue));
            }
            result[Field(contract)] = fields;
        }
        return result;
    }

    internal static JsonObject? OutputSchema(PlanningNode node, PlanningPreparation preparation)
    {
        var contracts = LocalContracts(node, preparation);
        return contracts.Length == 0 ? null : new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(contracts.Select(c => (JsonNode?)JsonValue.Create(Field(c))).ToArray()),
            ["properties"] = new JsonObject(contracts.Select(c => new KeyValuePair<string, JsonNode?>(Field(c), c.ResponseSchema.DeepClone())))
        };
    }
    private static string Field(PlanningDecisionContract contract)
    {
        var path = contract.SourcePointer.Split('/');
        if (path.Length != 2 || path[0].Length != 0 || path[1].Length == 0) throw new InvalidOperationException("A native decision must declare one exact output field.");
        return Decode(path[1]);
    }
    private static string Decode(string token) => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
