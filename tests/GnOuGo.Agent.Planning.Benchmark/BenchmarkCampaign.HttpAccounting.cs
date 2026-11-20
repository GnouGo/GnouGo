using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;

internal sealed partial class BenchmarkCampaign
{
    // Scoped to this open campaign. Each admission enumerates the authoritative membership and revisions again.
    private readonly ConcurrentDictionary<string, (KeyVaultRecordMetadata Revision, HttpContribution Contribution)> _httpContributions = new(StringComparer.Ordinal);

    internal sealed record HttpContribution(int Attempts, int Pending, long InputCeiling, long OutputCeiling,
        decimal CostCeiling, long InputTokens, long OutputTokens, decimal? MeasuredCost)
    {
        internal static HttpContribution From(JsonObject row)
        {
            var attempts = row["transport"]!["Attempts"]!.AsArray();
            var usage = row["usage"];
            var pending = attempts.Count(a => PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(
                a?["Status"]?.GetValue<int>(), usage is not null, PlanningBenchmarkMeasurements.RetainsAllHttpFailures(row)));
            return new(attempts.Count, pending, row["input_ceiling"]!.GetValue<long>(), row["output_ceiling"]!.GetValue<long>(),
                row["cost_ceiling_eur"]!.GetValue<decimal>(), usage is null ? 0 : usage["input_tokens"]!.GetValue<long>(),
                usage is null ? 0 : usage["output_tokens"]!.GetValue<long>(), usage is null ? null : usage["benchmark_cost_eur"]!.GetValue<decimal>());
        }
    }

    internal async Task<Dictionary<string, HttpContribution>> HttpContributionsAsync(CancellationToken ct)
    {
        const string collection = BenchmarkHttpJournal.Collection;
        var prefix = Id + ":";
        if (records is not IKeyVaultRecordReader reader)
            return (await OwnedRecordsAsync(collection, ct)).ToDictionary(row => row.Key[prefix.Length..],
                row => HttpContribution.From(JsonNode.Parse(row.Value)!.AsObject()), StringComparer.Ordinal);

        var contributions = new Dictionary<string, HttpContribution>(StringComparer.Ordinal);
        foreach (var revision in await OwnedMetadataAsync(reader, collection, prefix, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (_httpContributions.TryGetValue(revision.Key, out var cached) && cached.Revision == revision)
            {
                contributions.Add(revision.Key[prefix.Length..], cached.Contribution);
                continue;
            }
            var row = await ReadOwnedRecordAsync(collection, revision.Key, ct);
            if (row.UpdatedAt != revision.UpdatedAt || row.CreatedAt != revision.CreatedAt)
                throw AdmissionFailure("campaign_record_changed_during_inspection");
            if (revision.Version is { } version && (row.Value.Length != revision.Characters ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Value))), version, StringComparison.OrdinalIgnoreCase)))
                throw AdmissionFailure("campaign_record_content_version_mismatch");
            var contribution = HttpContribution.From(JsonNode.Parse(row.Value)!.AsObject());
            if (revision.Version is not null) _httpContributions[revision.Key] = (revision, contribution);
            contributions.Add(revision.Key[prefix.Length..], contribution);
        }
        return contributions;
    }
}
