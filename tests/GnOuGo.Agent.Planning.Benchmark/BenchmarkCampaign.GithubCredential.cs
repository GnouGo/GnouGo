using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Models;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

internal sealed partial class BenchmarkCampaign
{
    internal const string GithubSecret = "LLM--McpServers--Github";
    internal const string CredentialRotationScope = "github_mcp_api_key_rotation";

    private static bool ValidGithubRotation(JsonObject proof, JsonObject configuration, string host, string metadata, JsonObject parent)
        => JsonNode.DeepEquals(configuration, parent["configuration"]) &&
            proof["previous_host_configuration_hash"]?.ToString() == parent["host_configuration_hash"]?.ToString() &&
            proof["previous_declared_metadata_hash"]?.ToString() == parent["declared_metadata_hash"]?.ToString() &&
            metadata == parent["declared_metadata_hash"]?.ToString() && proof["host_configuration_hash"]?.ToString() == host &&
            host != parent["host_configuration_hash"]?.ToString() && proof["server"]?.ToString() == "Github" &&
            proof["field"]?.ToString() == "ApiKey" && proof["vault_key"]?.ToString() == GithubSecret &&
            Guid.TryParseExact(proof["secret_id"]?.ToString(), "D", out var id) && id != Guid.Empty &&
            proof["previous_version"]?.GetValue<int>() is > 0 and var prior && proof["current_version"]?.GetValue<int>() > prior;
}

internal sealed partial class KeyVaultBenchmarkModel
{
    internal async Task<JsonObject> GithubCredentialProofAsync(string root, string parentId, int from, int to, CancellationToken ct)
    {
        if (from < 1 || to <= from) throw new PlanningConflictException("Supply the earlier and current GitHub MCP credential versions.");
        var path = WorkspaceDatabasePath(root, null, KeyVaultDatabasePathResolver.DefaultRelativePath);
        await using var db = new KeyVaultDbContext(new DbContextOptionsBuilder<KeyVaultDbContext>()
            .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()).Options);
        var vault = new KeyVaultService(db);
        const string reader = "benchmark-credential-rotation";
        var current = await vault.GetSecretAsync(BenchmarkCampaign.GithubSecret, null, reader, ct);
        if (current?.Version != to) throw new PlanningConflictException("The requested current GitHub MCP version is missing or superseded.");
        var previous = await vault.GetSecretVersionAsync(BenchmarkCampaign.GithubSecret, null, reader, from, ct);
        if (previous is null) throw new PlanningConflictException("The requested historical GitHub MCP version is missing.");
        return await ProveGithubCredentialAsync(parentId, previous, current, ct);
    }

    internal async Task<JsonObject> ProveGithubCredentialAsync(string parentId, SecretValueDto previous, SecretValueDto current, CancellationToken ct)
    {
        var parent = await _campaign.LoadAsync("planning-evaluation-configuration", "configuration:" + parentId, ct)
            ?? throw new PlanningConflictException("The previous registered configuration is missing.");
        if (previous.Key != BenchmarkCampaign.GithubSecret || current.Key != previous.Key || previous.Id != current.Id ||
            previous.Id == Guid.Empty || previous.TenantId is not null || current.TenantId is not null ||
            previous.Version < 1 || current.Version <= previous.Version)
            throw new PlanningConflictException("The credential versions do not identify the same GitHub configuration record.");
        static JsonObject Document(string value)
        {
            try { if (JsonNode.Parse(value) is JsonObject document) return document; }
            catch (JsonException) { }
            throw new PlanningConflictException("The selected GitHub configuration version is invalid.");
        }
        static string? Field(JsonObject document, string name, string alias)
            => (document[name] ?? document[alias]) is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        var before = Document(previous.Value);
        var after = Document(current.Value);
        var oldKey = Field(before, "apiKey", "api_key");
        var newKey = Field(after, "apiKey", "api_key");
        if (!string.Equals(Field(before, "authType", "auth_type"), "api_key", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Field(after, "authType", "auth_type"), "api_key", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey) || oldKey == newKey ||
            !_options.McpServers.TryGetValue("Github", out var github) || github.ApiKey != newKey)
            throw new PlanningConflictException("The versions must establish a change to the effective GitHub API key.");

        var metadata = new RoutingLLMClient(_options, []).ResolveDeclaredMetadata(Provider, Model);
        var metadataHash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(metadata));
        if (metadata is null || metadataHash != parent["declared_metadata_hash"]?.ToString() ||
            !JsonNode.DeepEquals(CampaignConfiguration(), parent["configuration"]))
            throw new PlanningConflictException("The campaign configuration or declared model metadata also changed.");
        var restored = JsonSerializer.Deserialize<LLMOptions>(JsonSerializer.Serialize(_options))!;
        var host = ConfigurationFingerprint;
        if (HostFingerprint(restored) != host) throw new PlanningConflictException("The effective configuration cannot be copied losslessly for verification.");
        restored.McpServers["Github"].ApiKey = oldKey;
        var reconstructed = HostFingerprint(restored);
        if (reconstructed != parent["host_configuration_hash"]?.ToString())
        {
            string[] fields = ["name", "transport", "url", "command", "args", "description", "environmentVariables", "authType", "discoveryTimeoutSeconds", "callTimeoutSeconds"];
            var changed = fields.Where(f => !JsonNode.DeepEquals(before[f], after[f])).ToArray();
            throw new PlanningConflictException("Restoring only the credential did not reproduce the funded host. Changed named GitHub settings: " +
                (changed.Length == 0 ? "none detected; other effective differences cannot be localized from the retained fingerprint." : string.Join(", ", changed) + "."));
        }
        return new() { ["previous_configuration_id"] = parentId, ["previous_host_configuration_hash"] = reconstructed,
            ["previous_declared_metadata_hash"] = metadataHash, ["host_configuration_hash"] = host, ["server"] = "Github", ["field"] = "ApiKey",
            ["vault_key"] = BenchmarkCampaign.GithubSecret, ["secret_id"] = current.Id.ToString("D"),
            ["previous_version"] = previous.Version, ["current_version"] = current.Version };
    }
}
