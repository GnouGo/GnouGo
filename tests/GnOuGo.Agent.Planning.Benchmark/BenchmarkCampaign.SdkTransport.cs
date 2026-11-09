using System.Text.Json.Nodes;

internal sealed partial class BenchmarkCampaign
{
    internal const string SdkServer = "GnOuGo.GithubCopilot.Mcp";
    internal const string SdkTransportKey = "Code__Copilot__InferenceTransport";
    internal const string SdkStructuredTransport = "gnougo-structured-tools-v1";

    private static bool ValidSdkTransportProof(JsonObject proof, JsonObject configuration, string hostHash, string metadataHash, JsonObject parent)
        => JsonNode.DeepEquals(configuration, parent["configuration"]) &&
            proof["previous_host_configuration_hash"]?.ToString() == parent["host_configuration_hash"]?.ToString() &&
            proof["previous_declared_metadata_hash"]?.ToString() == parent["declared_metadata_hash"]?.ToString() &&
            metadataHash == parent["declared_metadata_hash"]?.ToString() && proof["host_configuration_hash"]?.ToString() == hostHash &&
            hostHash != parent["host_configuration_hash"]?.ToString() &&
            proof["server"]?.ToString() == SdkServer && proof["environment_key"]?.ToString() == SdkTransportKey &&
            proof["before"]?.ToString() is "absent" or "native" && proof["after"]?.ToString() == SdkStructuredTransport;
}
