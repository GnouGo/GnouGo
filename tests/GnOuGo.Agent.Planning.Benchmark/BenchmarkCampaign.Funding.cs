using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

internal sealed partial class BenchmarkCampaign
{
    private const string FundingCollection = "planning-evaluation-configuration";
    private const string FundingKey = "independent-funding-scope";
    private static string GrantKey(string reference) => "independent-funding-authorization:" + PlanningGraphCompiler.Fingerprint(reference);

    // The caller holds both the campaign lease and the authorization-reference lease.
    // This allocates new spending authority; it never clears or reprices an old ledger.
    internal async Task<JsonObject> AuthorizeFreshAllocationAsync(string previousId, decimal amount, string reference,
        JsonObject configuration, string hostHash, string metadataHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (amount <= 0 || previousId == Id) throw new ArgumentException("A fresh allocation needs a positive amount and a distinct previous campaign.");
        var previous = new BenchmarkCampaign(records, previousId);
        if (await previous.LoadAsync(FundingCollection, "configuration", ct) is null)
            throw new PlanningConflictException("The retained previous campaign is missing.");
        var material = new JsonObject { ["version"] = 1, ["kind"] = "independent_new_spend", ["campaign"] = Id,
            ["previous_campaign"] = previousId, ["authorization_reference"] = reference, ["allowance_eur"] = amount,
            ["configuration_hash"] = PlanningGraphCompiler.Fingerprint(configuration.ToJsonString()),
            ["host_configuration_hash"] = hostHash, ["declared_metadata_hash"] = metadataHash };
        var binding = await records.GetAsync(FundingCollection, "benchmark", GrantKey(reference), Author, ct);
        if (binding is not null && !JsonNode.DeepEquals(JsonNode.Parse(binding.Value), material))
            throw new PlanningConflictException("This funding authorization is already bound to another allocation.");
        var scope = await LoadAsync(FundingCollection, FundingKey, ct);
        if (scope is not null && !JsonNode.DeepEquals(scope["material"], material))
            throw new PlanningConflictException("The captured fresh funding allocation changed.");
        if (scope is null)
        {
            foreach (var collection in new[] { "planning-evaluation-requests", "planning-evaluation-receipts", BenchmarkHttpJournal.Collection,
                "planning-evaluation-runs", "planning-evaluation-session-policy", "planning-evaluation-budgets" })
            {
                var present = records is IKeyVaultRecordReader reader
                    ? (await reader.QueryMetadataAsync(collection, "benchmark", Id + ":", Author, count: 1, ct: ct)).Records.Count > 0
                    : (await records.ListAsync(collection, "benchmark", Author, ct)).Any(r => r.Key.StartsWith(Id + ":", StringComparison.Ordinal));
                if (present || collection == "planning-evaluation-budgets" && await records.GetAsync(collection, "benchmark", Id, Author, ct) is not null)
                    throw new PlanningConflictException("A funded allocation must start with a new empty campaign; existing consumption cannot be reset.");
            }
            if (await LoadAsync(FundingCollection, "spending-authorization", ct) is not null)
                throw new PlanningConflictException("This campaign already has a spending authorization.");
            scope = new() { ["material"] = material.DeepClone(), ["status"] = "pending", ["created_at_utc"] = DateTimeOffset.UtcNow,
                ["initial_new_consumption_eur"] = 0m, ["previous_ceiling_eur"] = await previous.CostCeilingAsync(ct),
                ["previous_accounting"] = await BenchmarkHttpJournal.AccountingAsync(previous, ct: ct),
                ["historical_liabilities_unreconciled"] = true, ["globally_available_funding"] = null };
            // Intent before the globally unique grant prevents partial initialization from authorizing inference.
            await SaveAsync(FundingCollection, FundingKey, scope, ct);
        }
        if (binding is null) await records.UpsertAsync(FundingCollection, "benchmark", GrantKey(reference), material.ToJsonString(), Author, ct);
        var spending = await LoadAsync(FundingCollection, "spending-authorization", ct);
        if (spending is null)
            await SaveAsync(FundingCollection, "spending-authorization", new() { ["ceiling_eur"] = amount,
                ["history"] = new JsonArray(new JsonObject { ["previous_ceiling_eur"] = 0m, ["previous_code_default_eur"] = 50m,
                    ["ceiling_eur"] = amount, ["authorization"] = reference, ["authorized_at"] = scope["created_at_utc"]!.DeepClone(),
                    ["kind"] = "independent_new_spend", ["initial_new_consumption_eur"] = 0m }) }, ct);
        else _ = AuthorizedAllocationCeiling(spending, amount, reference);
        var registered = await RegisterConfigurationAsync(configuration, hostHash, metadataHash,
            material["configuration_hash"]!.ToString(), reference + ":configuration", true, ct, captureCurrentSettings: true);
        scope["configuration_id"] = registered["configuration_id"]!.DeepClone(); scope["status"] = "active";
        await SaveAsync(FundingCollection, FundingKey, scope, ct);
        return (await FreshAllocationAsync(ct))!;
    }

    internal async Task<JsonObject?> FreshAllocationAsync(CancellationToken ct)
    {
        var scope = await LoadAsync(FundingCollection, FundingKey, ct);
        if (scope is null) return null;
        var material = scope["material"] as JsonObject ?? throw AdmissionFailure("funding_scope_material");
        var reference = material["authorization_reference"]?.ToString() ?? throw AdmissionFailure("funding_scope_authorization");
        var binding = await records.GetAsync(FundingCollection, "benchmark", GrantKey(reference), Author, ct);
        if (scope["status"]?.ToString() != "active" || material["kind"]?.ToString() != "independent_new_spend" || material["campaign"]?.ToString() != Id ||
            binding is null || !JsonNode.DeepEquals(JsonNode.Parse(binding.Value), material) ||
            material["allowance_eur"]?.GetValue<decimal>() is not > 0)
            throw AdmissionFailure("funding_scope_incomplete_or_changed");
        var spending = await LoadAsync(FundingCollection, "spending-authorization", ct)
            ?? throw AdmissionFailure("funding_scope_spending_authorization");
        var ceiling = AuthorizedAllocationCeiling(spending, material["allowance_eur"]!.GetValue<decimal>(), reference);
        var identity = scope["configuration_id"]?.ToString() ?? throw AdmissionFailure("funding_scope_configuration");
        var captured = await LoadAsync(FundingCollection, "configuration:" + identity, ct);
        if (captured?["configuration"] is not JsonObject configuration ||
            PlanningGraphCompiler.Fingerprint(configuration.ToJsonString()) != material["configuration_hash"]?.ToString() ||
            captured["declared_metadata_hash"]?.ToString() != material["declared_metadata_hash"]?.ToString() ||
            !(await CheckConfigurationSelectionAsync(identity, configuration, material["host_configuration_hash"]!.ToString(), ct)).Accepted)
            throw AdmissionFailure("funding_scope_configuration_changed");
        if (ConfigurationId is not null && ConfigurationId != identity)
            await VerifyFundedConfigurationChainAsync(ConfigurationId, identity, ct);
        return new() { ["kind"] = "independent_new_spend", ["campaign"] = Id, ["allowance_eur"] = material["allowance_eur"]!.DeepClone(),
            ["current_authorized_ceiling_eur"] = ceiling,
            ["authorization_reference"] = reference, ["configuration_id"] = identity,
            ["previous_campaign"] = material["previous_campaign"]!.DeepClone(), ["previous_ceiling_eur"] = scope["previous_ceiling_eur"]!.DeepClone(),
            ["historical_liabilities_unreconciled"] = true, ["initial_new_consumption_eur"] = 0m, ["globally_available_funding"] = null };
    }

    // The original grant stays immutable. Later authority comes only from the existing
    // leased supplement ledger, with a continuous chain and one entry per authorization.
    private static decimal AuthorizedAllocationCeiling(JsonObject spending, decimal initialAllowance, string originalReference)
    {
        if (spending["history"] is not JsonArray { Count: > 0 } history)
            throw AdmissionFailure("funding_scope_authorization_history");
        var references = new HashSet<string>(StringComparer.Ordinal);
        var ceiling = 0m;
        for (var index = 0; index < history.Count; index++)
        {
            var entry = history[index] as JsonObject;
            var reference = entry?["authorization"]?.ToString();
            var next = entry?["ceiling_eur"]?.GetValue<decimal>();
            if (string.IsNullOrWhiteSpace(reference) || !references.Add(reference) ||
                entry?["previous_ceiling_eur"]?.GetValue<decimal>() != ceiling || next is null || next <= ceiling ||
                string.IsNullOrWhiteSpace(entry?["authorized_at"]?.ToString()) ||
                (index == 0 ? next != initialAllowance || reference != originalReference || entry?["kind"]?.ToString() != "independent_new_spend"
                    : entry?["accounting_before"] is not JsonObject || entry["kind"] is not null))
                throw AdmissionFailure("funding_scope_authorization_history");
            ceiling = next.Value;
        }
        if (spending["ceiling_eur"]?.GetValue<decimal>() != ceiling)
            throw AdmissionFailure("funding_scope_authorization_ceiling");
        return ceiling;
    }
}
