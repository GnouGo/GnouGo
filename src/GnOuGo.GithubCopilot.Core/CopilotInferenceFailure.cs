using System.Text.Json;
using System.Text.Json.Serialization;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Safe physical transport facts returned by the negotiated local inference journal.</summary>
public sealed record CopilotInferenceFailure(int? Status, string Classification, string Phase, int Attempts,
    int? RequestedDelayMilliseconds, DateTimeOffset? NotBeforeUtc, string PhysicalRequestHash, string? ProviderRequestHash)
{
    public const string Header = "X-GnOuGo-Inference-Failure";

    [JsonIgnore]
    public string Summary => FormattableString.Invariant(
        $"Provider {Classification}, HTTP {Status}, phase {Phase}, attempts {Attempts}, requested delay {RequestedDelayMilliseconds} ms, retry from {NotBeforeUtc:O}, physical request SHA-256 {PhysicalRequestHash}.");

    internal static CopilotInferenceFailure? Read(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(Header, out var values) || values.FirstOrDefault() is not { Length: > 0 and <= 1024 } json)
            return null;
        try
        {
            var failure = JsonSerializer.Deserialize(json, CopilotCoreJsonContext.Default.CopilotInferenceFailure);
            return failure is { Status: null or >= 100 and <= 599, Attempts: >= 1 and <= 3, RequestedDelayMilliseconds: null or >= 0,
                Phase: "provider_rejection" or "response_headers" or "response_body" or "response_protocol",
                Classification: "RateLimited" or "ServiceUnavailable" or "Timeout" or "Transport" or "Authentication" or "Authorization"
                    or "QuotaOrBilling" or "InvalidRequest" or "ModelUnavailable" or "Unknown" }
                && Hash(failure.PhysicalRequestHash) && (failure.ProviderRequestHash is null || Hash(failure.ProviderRequestHash)) ? failure : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
