using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class TypedDecisionGroundingTests
{
    [Fact]
    public void ConfirmationNeverUsesAnAnalysisProducer_RegardlessOfUpstreamCandidateCount()
    {
        var t = typeof(WorkflowPlanExecutor);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        Func<string, Type> T = n => t.GetNestedType(n, System.Reflection.BindingFlags.NonPublic)!;
        Func<Type, Array> empty = x => Array.CreateInstance(x, 0);
        Func<string, string, string, string[], object> op = (id, kind, decision, inputs) =>
        {
            var x = Activator.CreateInstance(T("CapabilityInventoryOperation"), new object?[] { id, "Sanitized operation", true, kind, kind == "human_interaction" ? "none" : "write", decision, "requested_effect", "", id == "effect", "" })!;
            x.GetType().GetProperty("InputOperationIds")!.SetValue(x, inputs); return x;
        };
        Func<string, string, string, object> entry = (id, resolution, method) => Activator.CreateInstance(T("CapabilityCatalogEntry"), new object?[] { id, resolution, "neutral", "tool", method, "Sanitized contract", empty(T("CapabilityRequestBinding")), "", empty(T("CapabilitySchemaField")), empty(T("CapabilitySchemaField")), null, null })!;
        Func<object, string, string, object> match = (operation, status, id) => Activator.CreateInstance(T("CapabilityOperationMatch"), new object?[] { operation, status, "", new string[] { id }, new string[0], null, null, null, null, null, null, null })!;
        var dictionaryType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), T("CapabilityCatalogEntry"));
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(dictionaryType)!;
        dict.Add("read", entry("read", "mcp", "read_value"));
        dict.Add("analysis", entry("analysis", "native", "llm.call"));
        dict.Add("human", entry("human", "native", "human.input"));
        dict.Add("write", entry("write", "mcp", "write_value"));
        foreach (var inputs in new[] { new[] { "read", "analysis", "confirmation" }, new[] { "analysis", "confirmation" }, new[] { "confirmation" } })
        {
            var effectOp = op("effect", "external_effect", "confirmation", inputs);
            var effect = match(effectOp, "conditional", "write");
            effect.GetType().GetProperty("ConditionalActivationMode")!.SetValue(effect, "all_on_value");
            var all = new[] { match(op("read", "external_effect", "", new string[0]), "matched", "read"), match(op("analysis", "external_effect", "", new[] { "read" }), "matched", "analysis"), match(op("confirmation", "human_interaction", "", new[] { "analysis" }), "matched", "human"), effect };
            var arr = Array.CreateInstance(T("CapabilityOperationMatch"), all.Length);
            for (var i = 0; i < all.Length; i++) arr.SetValue(all[i], i);
            var eval = Activator.CreateInstance(T("CapabilityMatchingEvaluation"), new object?[] { arr, empty(T("CapabilityConstraintMatch")), empty(T("CapabilityMatchingIssue")), true })!;
            eval.GetType().GetProperty("ExactDecisionSources")!.SetValue(eval, true);
            var args = new object?[] { eval, effect, dict, null, null, null, null, null, null, null };
            var ok = t.GetMethod("TryGroundConditionalDecision", flags)!.Invoke(null, args);
            Assert.Equal(true, ok); Assert.Equal("human_confirmation", args[6]); Assert.Equal("confirmation", args[8]); Assert.Equal("/response", args[3]); Assert.Equal("", args[9]);
            var inventoryOperations = Array.CreateInstance(T("CapabilityInventoryOperation"), all.Length);
            for (var i = 0; i < all.Length; i++) inventoryOperations.SetValue(all[i].GetType().GetProperty("Operation")!.GetValue(all[i]), i);
            var inventory = Activator.CreateInstance(T("CapabilityInventory"), new object?[] { true, inventoryOperations,
    empty(T("CapabilityInventoryConstraint")), empty(T("CapabilityInventoryIncompleteReason")), "required", "explicit consent" })!;
            var contextType = T("TypedContractJsonContext");
            var context = (System.Text.Json.Serialization.JsonSerializerContext)contextType.GetProperty("Default")!.GetValue(null)!;
            var info = context.GetTypeInfo(inventory.GetType())!;
            var serialized = System.Text.Json.JsonSerializer.Serialize(inventory, info);
            Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize(serialized, info));
            var catalogEntries = Array.CreateInstance(T("CapabilityCatalogEntry"), dict.Count);
            var ci = 0; foreach (var entryValue in dict.Values) catalogEntries.SetValue(entryValue, ci++);
            var catalog = Activator.CreateInstance(T("CapabilityCatalog"), new object?[] { catalogEntries, "" })!;
            var schema = (JsonObject)t.GetMethod("BuildTypedCapabilityMatchingSchema", flags)!.Invoke(null, new[] { inventory, catalog })!;
            Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
            var readProperties = schema["properties"]!["operation_matches"]!["properties"]!["read"]!["anyOf"]![0]!["properties"]!;
            Assert.Equal(1, readProperties["catalog_ids"]!["maxItems"]!.GetValue<int>());
            Assert.Equal(0, readProperties["candidate_catalog_ids"]!["maxItems"]!.GetValue<int>());
            Assert.DoesNotContain("conditional", readProperties["status"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
            Assert.Equal("", Assert.Single(readProperties["decision_operation_id"]!["enum"]!.AsArray())!.GetValue<string>());

        }

    }
}

