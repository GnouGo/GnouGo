using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Capabilities;

// Comparison identity is distinct from the journal's exact serialized payload hash.
// Arrays and all contract values remain significant; this is not schema equivalence.
internal static class CapabilityContractIdentity
{
    internal static string Version(PlanningCapability capability) => "json-v1:" +
        PlanningGraphCompiler.Fingerprint(Canonical(Document(capability)).ToJsonString());

    internal static string LegacyVersion(PlanningCapability capability) =>
        PlanningGraphCompiler.Fingerprint(Document(capability).ToJsonString());

    private static JsonObject Document(PlanningCapability capability)
    {
        var document = JsonSerializer.SerializeToNode(capability, PlanningJsonContext.Default.PlanningCapability)!.AsObject();
        document["version"] = "";
        return document;
    }

    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value is null ? null : Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(value => value is null ? null : Canonical(value)).ToArray()),
        _ => node.DeepClone()
    };
}
