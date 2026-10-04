using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;
internal static class PlanningValues
{
    internal static PlanningValue? ReadGuard(PlanningValue value)
    {
        // Presence is safe even when the producer did not run.
        if (value.Kind == "present") return null;
        if (value.Kind == "output") return new() { Kind = "present", Source = value.Source };
        if (value.Kind == "predicate" && value.Text is "and" or "or" && value.Items.Count == 2)
        {
            var left = ReadGuard(value.Items[0]); var right = ReadGuard(value.Items[1]);
            if (right is null) return left;
            // Only require the right operand when short-circuit evaluation reaches it.
            var test = value.Text == "and" ? Predicate("not", value.Items[0]) : value.Items[0];
            return And(left, Predicate("or", test, right));
        }
        return value.Members.Select(m => ReadGuard(m.Value)).Concat(value.Items.Select(ReadGuard)).Aggregate((PlanningValue?)null, And);
    }

    internal static PlanningValue? And(PlanningValue? left, PlanningValue? right) => left is null ? right :
        right is null || Same(left, right) ? left : Predicate("and", left, right);
    internal static bool Same(PlanningValue left, PlanningValue right) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(left, PlanningJsonContext.Default.PlanningValue), JsonSerializer.SerializeToNode(right, PlanningJsonContext.Default.PlanningValue));
    internal static bool ContainsGuard(PlanningValue? guard, PlanningValue required) => guard is not null &&
        (Same(guard, required) || guard.Kind == "predicate" && guard.Text == "and" && guard.Items.Any(v => ContainsGuard(v, required)));

    internal const string Omitted = "omitted";
    internal static PlanningValue Predicate(string operation, params PlanningValue[] operands)
        => new() { Kind = "predicate", Text = operation, Items = operands.ToList() };

    internal static string ArithmeticOperator(string? operation) => operation switch
    {
        "add" => "+", "subtract" or "negate" => "-", "multiply" => "*", "divide" => "/", "remainder" => "%",
        _ => throw new InvalidOperationException("Unknown typed arithmetic operator.")
    };

    internal static string PredicateOperator(string? operation) => operation switch
    {
        "not" => "!", "and" => "&&", "or" => "||", "equal" => "===", "not_equal" => "!==",
        "less" => "<", "less_equal" => "<=", "greater" => ">", "greater_equal" => ">=",
        _ => throw new InvalidOperationException("Unknown typed predicate.")
    };

    internal static JsonObject ComputationContract(PlanningValue value, Func<PlanningValue, JsonObject?> resolve)
    {
        if (value.Source is not null || value.ResultChannel is not null || value.Path.Count != 0 || value.Members.Count != 0 ||
            value.Number is not null || value.Boolean is not null)
            throw new InvalidOperationException("A typed computation contains unsupported fields.");
        if (value.Kind == "json")
        {
            if (value.Text is not null || value.Items.Count != 1 || resolve(value.Items[0]) is null)
                throw new InvalidOperationException("JSON encoding requires exactly one established value.");
            return new() { ["type"] = "string" };
        }
        if (value.Kind == "arithmetic")
        {
            _ = ArithmeticOperator(value.Text);
            if (value.Items.Count != (value.Text == "negate" ? 1 : 2)) throw new InvalidOperationException("Arithmetic arity is invalid.");
            if (value.Items.Any(operand => resolve(operand) is not { } schema || !PlanningContractCompatibility.Fits(schema, new() { ["type"] = "number" })))
                throw new InvalidOperationException("Arithmetic requires established, nonnullable numeric operands.");
            return new() { ["type"] = "number" };
        }
        _ = PredicateOperator(value.Text);
        if (value.Items.Count != (value.Text == "not" ? 1 : 2)) throw new InvalidOperationException("Predicate arity is invalid.");
        foreach (var operand in value.Items)
        {
            var schema = resolve(operand) ?? throw new InvalidOperationException("The predicate operand has no established contract.");
            if (value.Text is "and" or "or" or "not" && schema["type"]?.ToString() != "boolean")
                throw new InvalidOperationException("Logical predicates require boolean operands.");
            if (value.Text is "less" or "less_equal" or "greater" or "greater_equal" && schema["type"]?.ToString() is not ("number" or "integer"))
                throw new InvalidOperationException("Ordering predicates require numeric operands.");
        }
        return new() { ["type"] = "boolean" };
    }
    internal static string LiteralLocation(PlanningValue value, string root, string pointer)
    {
        foreach (var token in pointer.Split('/').Skip(1))
        {
            var name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.Kind == "object")
            {
                var index = value.Members.FindIndex(m => m.Name == name);
                if (index < 0) break;
                root += "/members/" + index + "/value"; value = value.Members[index].Value;
            }
            else if (value.Kind == "array" && int.TryParse(name, out var index) && index >= 0 && index < value.Items.Count)
            { root += "/items/" + index; value = value.Items[index]; }
            else break;
        }
        return root;
    }
    internal static bool Established(JsonObject schema) => Established(schema, schema, 0);
    private static bool Established(JsonObject schema, JsonObject root, int depth)
    {
        if (PlanningContractShapes.IsOpaque(schema)) return true;
        if (depth > 32) return false;
        if (schema.ContainsKey("const") || schema["enum"] is JsonArray { Count: > 0 }) return true;
        if (schema["$ref"] is JsonValue reference)
        {
            var pointer = reference.ToString();
            return pointer.StartsWith("#/", StringComparison.Ordinal) && PlanningFieldPaths.ReadOptional(root, pointer[1..]) is JsonObject target && Established(target, root, depth + 1);
        }
        if ((schema["anyOf"] ?? schema["oneOf"]) is JsonArray alternatives)
            return alternatives.Count > 0 && alternatives.All(s => s is JsonObject variant && Established(variant, root, depth + 1));
        if (schema["allOf"] is JsonArray parts && parts.OfType<JsonObject>().Any(p => Established(p, root, depth + 1))) return true;
        var types = schema["type"] is JsonArray union ? union.Select(t => t!.ToString()).Where(t => t != "null").ToArray() : [schema["type"]?.ToString() ?? ""];
        if (types.Length != 1) return false;
        return types[0] switch
        {
            "object" => (schema["properties"] as JsonObject ?? new()).All(p => p.Value is JsonObject field && Established(field, root, depth + 1)) &&
                (schema["properties"] is JsonObject { Count: > 0 } || schema["additionalProperties"]?.ToString() == "false" || schema["additionalProperties"] is JsonObject extra && Established(extra, root, depth + 1)),
            "array" => (schema["prefixItems"] is not JsonArray prefix || prefix.All(p => p is JsonObject item && Established(item, root, depth + 1))) &&
                (schema["items"]?.ToString() == "false" || schema["items"] is JsonObject items && Established(items, root, depth + 1)),
            "string" or "number" or "integer" or "boolean" or "null" => true,
            _ => false
        };
    }
}
