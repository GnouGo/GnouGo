using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Direct argument bindings must fit the destination; transformation parameters retain their source types.</summary>
internal static class PlanningArgumentBindings
{
    internal static void Constrain(JsonObject arguments, JsonObject definitions, string prefix, JsonObject destinations, IEnumerable<PlanningBinding> bindings)
    {
        var index = bindings.ToArray();
        var general = "#/$defs/" + prefix + "value";
        var computed = prefix + "nonBindingValue";
        foreach (var (name, shape) in arguments["properties"]!.AsObject())
        {
            // Artifact arguments have a stronger original-producer-only schema.
            // Preserve it and preserve the separate optional-argument omission case.
            var value = shape?["$ref"]?.ToString() == general ? shape : shape?["anyOf"]?[0];
            if (value?["$ref"]?.ToString() != general || destinations[name] is not JsonObject expected) continue;
            var eligible = index.Where(b => PlanningGraphValidation.TypesFit(b.Schema, expected)).Select(b => b.Id).ToArray();
            if (eligible.Length == index.Length) continue;
            if (!definitions.ContainsKey(computed))
            {
                var alternative = definitions[prefix + "value"]!.DeepClone();
                var variants = alternative["anyOf"]!.AsArray();
                foreach (var binding in variants.OfType<JsonObject>().Where(v => v["properties"]?["kind"]?["enum"]?[0]?.ToString() == "binding").ToArray()) variants.Remove(binding);
                definitions[computed] = alternative;
            }
            // Destinations with the same eligible producers share one enum. Nested
            // compute/template members still reference the complete typed source index.
            var key = prefix + "argument_" + PlanningGraphCompiler.Fingerprint(string.Join("\n", eligible))[..16];
            if (!definitions.ContainsKey(key))
            {
                var variants = new JsonArray(new JsonObject { ["$ref"] = "#/$defs/" + computed });
                if (eligible.Length != 0) variants.Add((JsonNode)PlanningDataflow.BindingSchema(eligible));
                definitions[key] = new JsonObject { ["anyOf"] = variants };
            }
            value!["$ref"] = "#/$defs/" + key;
        }
    }
}
