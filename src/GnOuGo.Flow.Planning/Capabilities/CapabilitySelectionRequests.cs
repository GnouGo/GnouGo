using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Capabilities;

/// <summary>Sizes the complete selector request, including its scoped response contract.</summary>
internal static class CapabilitySelectionRequests
{
    internal sealed record Page(string Prompt, JsonObject Schema, IReadOnlySet<string> CatalogIds);

    internal static IReadOnlyList<Page> Build(CapabilityInventory inventory, PhysicalCapabilityCatalog catalog,
        bool repair, IReadOnlySet<string> operationIds, IReadOnlySet<string> constraintIds, int inputCeiling)
    {
        var context = Context(inventory, operationIds, constraintIds).ToJsonString();
        var pages = new List<Page>();
        for (var offset = 0; offset < catalog.Entries.Count;)
        {
            Page? accepted = null;
            var acceptedCount = 0;
            var low = 1;
            var high = catalog.Entries.Count - offset;
            while (low <= high)
            {
                var count = low + (high - low) / 2;
                var entries = catalog.Entries.Skip(offset).Take(count).ToArray();
                var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                var schema = Schema(operationIds, constraintIds, ids);
                var prompt = $$"""
                    Select plausible physical capability candidates for the validated operations and exact denials.
                    {{(repair ? "Reconsider the inventory items omitted by the initial pass." : "Initial selection.")}}
                    Include complementary prerequisites and cleanup capabilities supported by declared artifact contracts.
                    Empty selections mean this page has no candidate. Selection grants no execution authority;
                    exact contracts, evidence, ownership and policies are validated after selection.
                    <inventory>{{context}}</inventory>
                    <catalog>
                    {{string.Join('\n', entries.Select(e => e.Id + " " + e.Card))}}
                    </catalog>
                    """;
                if (PlanningJsonTransport.EstimateInputTokens(prompt, schema) <= inputCeiling)
                {
                    accepted = new(prompt, schema, ids); acceptedCount = count; low = count + 1;
                }
                else high = count - 1;
            }
            if (accepted is null)
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT",
                    $"Capability selection cannot fit the scoped inventory and catalog entry '{catalog.Entries[offset].Id}' within {inputCeiling} input tokens. Narrow the intent or capability constraints. No selection request was dispatched.");
            pages.Add(accepted);
            if (pages.Count > PhysicalCapabilityMaxPages)
                throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT",
                    $"Capability selection needs more than {PhysicalCapabilityMaxPages} bounded pages. Narrow the intent or capability constraints. No selection request was dispatched.");
            offset += acceptedCount;
        }
        return pages;
    }

    private static JsonObject Context(CapabilityInventory inventory, IReadOnlySet<string> operationIds, IReadOnlySet<string> constraintIds)
    {
        // Inventory evidence was already validated. Share the exact declared obligations here;
        // selection cannot edit their classifications, anchors, policies or confirmation rules.
        var obligations = new JsonObject();
        var obligationIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var operations = new JsonObject();
        foreach (var operation in inventory.Operations.Where(o => operationIds.Contains(o.Id)).OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var coverage = new JsonArray();
            foreach (var text in operation.CoverageRequirements.Concat(operation.CoverageRequirementEvidence.Select(e => e.Excerpt)).Distinct(StringComparer.Ordinal))
            {
                if (!obligationIds.TryGetValue(text, out var id))
                { id = "r" + obligationIds.Count; obligationIds.Add(text, id); obligations[id] = text; }
                coverage.Add((JsonNode?)JsonValue.Create(id));
            }
            operations[operation.Id] = new JsonObject
            {
                ["description"] = operation.Description, ["effect"] = operation.ExternalEffectKind,
                ["inputs"] = Strings(operation.InputOperationIds), ["obligations"] = coverage
            };
        }
        var constraints = new JsonObject();
        foreach (var constraint in inventory.Constraints.Where(c => constraintIds.Contains(c.Id)).OrderBy(c => c.Id, StringComparer.Ordinal))
            constraints[constraint.Id] = constraint.Description;
        var dependencies = new JsonObject();
        var referenced = inventory.Operations.Where(o => operationIds.Contains(o.Id)).SelectMany(o => o.InputOperationIds).ToHashSet(StringComparer.Ordinal);
        foreach (var dependency in inventory.Operations.Where(o => referenced.Contains(o.Id) && !operationIds.Contains(o.Id)).OrderBy(o => o.Id, StringComparer.Ordinal))
            dependencies[dependency.Id] = dependency.Description;
        return new() { ["operations"] = operations, ["exact_denials"] = constraints, ["obligations"] = obligations, ["dependencies"] = dependencies };
    }

    private static JsonObject Schema(IReadOnlySet<string> operations, IReadOnlySet<string> constraints, IReadOnlySet<string> catalogIds)
    {
        JsonObject Candidates(string identity, IReadOnlySet<string> ids)
        {
            var shape = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = ids.Count,
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        [identity] = ids.Count == 0 ? new JsonObject { ["type"] = "string" }
                            : new JsonObject { ["type"] = "string", ["enum"] = Strings(ids.Order(StringComparer.Ordinal)) },
                        ["catalog_ids"] = new JsonObject { ["$ref"] = "#/$defs/candidates" }
                    },
                    ["required"] = new JsonArray(identity, "catalog_ids")
                }
            };
            return shape;
        }
        return new()
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["operation_candidates"] = Candidates("operation_id", operations),
                ["constraint_candidates"] = Candidates("constraint_id", constraints)
            },
            ["required"] = new JsonArray("operation_candidates", "constraint_candidates"),
            ["$defs"] = new JsonObject
            {
                ["candidates"] = new JsonObject
                {
                    ["type"] = "array", ["maxItems"] = PhysicalCapabilityMaxCandidatesPerInventoryItem,
                    ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(catalogIds.Order(StringComparer.Ordinal)) }
                }
            }
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
