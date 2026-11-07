using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Shares identical contract presentations inside one issued prompt without changing contract authority.</summary>
public static class PlanningPromptPresentation
{
    /// <summary>
    /// Replaces a repeated evidence schema with a pointer to its byte-identical schema
    /// already presented for the same operation identity, version and original hash.
    /// Original coordinates, ancestor constraints, reference closures and diagnostics remain.
    /// </summary>
    public static string ShareDuplicateContractSchemas(string prompt)
    {
        var offset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        if (offset < 0) return prompt;
        JsonObject? context;
        try { context = JsonNode.Parse(prompt[(offset + 1)..]) as JsonObject; }
        catch (JsonException) { return prompt; }
        if (context?["operations"] is not JsonArray operations || context["evidence"] is not JsonObject evidence) return prompt;
        var schemas = new Dictionary<(string Id, string Version, string Hash, string Pointer, string Schema), string>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is not JsonObject operation || !Identity(operation, out var id, out var version, out var hash)) continue;
            Add(operation["inputSchema"] as JsonObject, "/input", $"/operations/{i}/inputSchema");
            foreach (var name in new[] { "selectedInputs", "selectedOutputs" })
                if (operation[name] is JsonArray selected)
                    for (var j = 0; j < selected.Count; j++)
                        if (Text(selected[j]?["pointer"]) is { Length: > 0 } pointer)
                            Add(selected[j]?["schema"] as JsonObject, pointer, $"/operations/{i}/{name}/{j}/schema");

            void Add(JsonObject? schema, string original, string presented)
            {
                if (schema is null) return;
                foreach (var entry in PlanningSchemaReferences.Walk(schema, original, 0))
                    schemas.TryAdd((id, version, hash, entry.Path, entry.Schema.ToJsonString()), presented + entry.Path[original.Length..]);
            }
        }
        var shared = false;
        foreach (var row in evidence.Select(p => p.Value).OfType<JsonObject>())
            if (Identity(row, out var id, out var version, out var hash) && Text(row["pointer"]) is { Length: > 0 } pointer &&
                row["schema"] is JsonObject schema && !row.ContainsKey("schemaPresentedAt") &&
                schemas.TryGetValue((id, version, hash, pointer, schema.ToJsonString()), out var presented))
            {
                row.Remove("schema"); row["schemaPresentedAt"] = presented; shared = true;
            }
        if (!shared) return prompt;
        context["schemaPresentation"] = "schemaPresentedAt is a JSON pointer within this same context to the byte-identical schema for the retained operation id/version/hash and original pointer. Evidence retains its original ancestor constraints and reference closure. This presentation link grants no binding, contract or execution authority.";
        return prompt[..(offset + 1)] + PlanningJsonTransport.Prompt(context);
    }

    private static bool Identity(JsonObject value, out string id, out string version, out string hash)
    {
        id = Text(value["id"]); version = Text(value["version"]); hash = Text(value["hash"]);
        return id.Length > 0 && version.Length > 0 && hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    }

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}
