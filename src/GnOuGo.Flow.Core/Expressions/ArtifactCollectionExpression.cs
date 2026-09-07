using System.Text.Json.Nodes;
using Acornima.Ast;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>Lossless collection of declared JSON-array artifacts from completed loop iterations.</summary>
public static class ArtifactCollectionExpression
{
    public const string FunctionName = "collect_json_arrays";

    public static string Build(string loopId, IReadOnlyList<string> path)
    {
        if (!SafeIdentifier(loopId) || path.Count == 0 || path.Any(string.IsNullOrEmpty)) throw new ArgumentException("An exact loop identifier and non-empty result path are required.");
        var names = new JsonArray(path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        return FunctionName + "(data.steps." + loopId + ".results," + names.ToJsonString() + ")";
    }

    public static bool TryRead(string expression, out string loopId, out IReadOnlyList<string> path)
    {
        loopId = ""; path = [];
        try
        {
            if (new Acornima.Parser().ParseExpression(expression) is not CallExpression { Callee: Identifier { Name: FunctionName }, Arguments.Count: 2 } call ||
                call.Arguments[0] is not MemberExpression { Computed: false, Property: Identifier { Name: "results" }, Object: MemberExpression
                { Computed: false, Property: Identifier loop, Object: MemberExpression { Computed: false, Object: Identifier { Name: "data" }, Property: Identifier { Name: "steps" } } } } ||
                call.Arguments[1] is not ArrayExpression values || values.Elements.Count == 0 || values.Elements.Any(e => e is not StringLiteral { Value.Length: > 0 })) return false;
            loopId = loop.Name; path = values.Elements.Cast<StringLiteral>().Select(v => v.Value).ToArray(); return true;
        }
        catch (Acornima.ParseErrorException) { return false; }
    }

    internal static JsonNode? Evaluate(JsonNode?[] args)
    {
        if (args.Length != 2 || args[0] is not JsonArray rows || args[1] is not JsonArray fields || fields.Count == 0 || fields.Any(f => f is not JsonValue v || !v.TryGetValue<string>(out var key) || string.IsNullOrEmpty(key)))
            throw new InvalidOperationException("Artifact collection requires an array of completed results and an exact field path.");
        var result = new JsonArray();
        foreach (var row in rows)
        {
            JsonNode? value = row;
            foreach (var field in fields)
            {
                if (value is not JsonObject obj || !obj.TryGetPropertyValue(field!.GetValue<string>(), out value))
                    throw new InvalidOperationException("A required original artifact is missing from a collection result.");
            }
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var json) || JsonNode.Parse(json) is not JsonArray items)
                throw new InvalidOperationException("The declared artifact encoding requires a JSON-array string.");
            foreach (var item in items) result.Add(item?.DeepClone());
        }
        return JsonValue.Create(result.ToJsonString());
    }

    private static bool SafeIdentifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');
}
