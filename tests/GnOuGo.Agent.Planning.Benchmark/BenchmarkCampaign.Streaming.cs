using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

internal sealed partial class BenchmarkCampaign
{
    internal static JsonObject TransportProjection(JsonObject configuration)
    {
        var policy = configuration["request_policy"] as JsonObject;
        return new()
        {
            ["streaming_mode"] = policy?["StreamingMode"]?.DeepClone(),
            ["streaming_support"] = policy?["StreamingSupport"]?.DeepClone(),
            ["exact_model_override"] = configuration["model_transport_overrides"]?[configuration["model"]!.ToString()]?.DeepClone()
        };
    }

    internal static bool OnlyStreamingTransportChanged(JsonObject current, JsonObject previous)
    {
        if (current["request_policy"] is not JsonObject || previous["request_policy"] is not JsonObject priorPolicy ||
            current["model"]?.ToString() != previous["model"]?.ToString()) return false;
        var restored = current.DeepClone().AsObject();
        foreach (var name in new[] { "StreamingMode", "StreamingSupport" })
            if (priorPolicy.ContainsKey(name)) restored["request_policy"]![name] = priorPolicy[name]?.DeepClone();
            else restored["request_policy"]!.AsObject().Remove(name);
        var id = current["model"]!.ToString();
        var overrides = (restored["model_transport_overrides"] as JsonObject) ?? new JsonObject();
        var prior = previous["model_transport_overrides"] as JsonObject;
        if (prior?.ContainsKey(id) == true) overrides[id] = prior[id]?.DeepClone();
        else overrides.Remove(id);
        if (prior is null && overrides.Count == 0) restored.Remove("model_transport_overrides");
        else restored["model_transport_overrides"] = overrides.DeepClone();
        return JsonNode.DeepEquals(restored, previous) && !JsonNode.DeepEquals(TransportProjection(current), TransportProjection(previous));
    }

    private async Task VerifyFundedConfigurationChainAsync(string selectedId, string fundedId, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 8 && selectedId != fundedId; depth++)
        {
            if (!seen.Add(selectedId)) throw AdmissionFailure("funding_scope_selected_configuration");
            var child = await LoadAsync(FundingCollection, "configuration:" + selectedId, ct);
            var kind = child?["amendment_scope"]?.ToString();
            var proof = child?[kind switch { "streaming_transport_only" => "streaming_transport_proof",
                "sdk_transport_only" => "sdk_transport_proof", _ => "reasoning_capability_proof" }] as JsonObject;
            var parentId = proof?["previous_configuration_id"]?.ToString();
            var parent = parentId is { Length: 64 } ? await LoadAsync(FundingCollection, "configuration:" + parentId, ct) : null;
            if (child?["configuration"] is not JsonObject configuration || parent?["configuration"] is not JsonObject previous ||
                parent["host_configuration_hash"]?.ToString() != proof?["previous_host_configuration_hash"]?.ToString() ||
                parent["declared_metadata_hash"]?.ToString() != proof?["previous_declared_metadata_hash"]?.ToString() ||
                !(await CheckConfigurationSelectionAsync(selectedId, configuration, child["host_configuration_hash"]!.ToString(), ct)).Accepted ||
                !(await CheckConfigurationSelectionAsync(parentId!, previous, parent["host_configuration_hash"]!.ToString(), ct)).Accepted)
                throw AdmissionFailure("funding_scope_selected_configuration");
            if (kind == "reasoning_capabilities_only")
            {
                if (!JsonNode.DeepEquals(configuration, previous) || proof?["before"] is not JsonArray { Count: > 0 } || proof["after"] is not JsonArray { Count: > 0 })
                    throw AdmissionFailure("funding_scope_selected_configuration");
            }
            else if (kind == "streaming_transport_only")
            {
                if (child["declared_metadata_hash"]?.ToString() != parent["declared_metadata_hash"]?.ToString() ||
                    proof?["host_configuration_hash"]?.ToString() != child["host_configuration_hash"]?.ToString() ||
                    !OnlyStreamingTransportChanged(configuration, previous) ||
                    !JsonNode.DeepEquals(proof?["before"], TransportProjection(previous)) ||
                    !JsonNode.DeepEquals(proof?["after"], TransportProjection(configuration)))
                    throw AdmissionFailure("funding_scope_selected_configuration");
            }
            else if (kind == "sdk_transport_only")
            {
                if (proof is null || !ValidSdkTransportProof(proof, configuration, child["host_configuration_hash"]!.ToString(),
                    child["declared_metadata_hash"]!.ToString(), parent))
                    throw AdmissionFailure("funding_scope_selected_configuration");
            }
            else throw AdmissionFailure("funding_scope_selected_configuration");
            selectedId = parentId!;
        }
        if (selectedId != fundedId) throw AdmissionFailure("funding_scope_selected_configuration");
    }
}
