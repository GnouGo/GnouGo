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
    internal const string GitSecret = "LLM--McpServerOverrides--GnOuGo.Git.Mcp--Git--Token";
    internal const string CredentialRotationScope = "github_mcp_api_key_rotation";
    internal const string PairedCredentialRotationScope = "github_api_key_and_git_token_rotation";

    private static bool ValidGithubRotation(JsonObject proof, JsonObject configuration, string host, string metadata, JsonObject parent, bool paired)
        => proof.Count == (paired ? 11 : 10) && proof["previous_configuration_id"]?.ToString() is { Length: 64 } &&
            JsonNode.DeepEquals(configuration, parent["configuration"]) &&
            proof["previous_host_configuration_hash"]?.ToString() == parent["host_configuration_hash"]?.ToString() &&
            proof["previous_declared_metadata_hash"]?.ToString() == parent["declared_metadata_hash"]?.ToString() &&
            metadata == parent["declared_metadata_hash"]?.ToString() && proof["host_configuration_hash"]?.ToString() == host &&
            host != parent["host_configuration_hash"]?.ToString() && proof["server"]?.ToString() == "Github" &&
            proof["field"]?.ToString() == "ApiKey" && proof["vault_key"]?.ToString() == GithubSecret &&
            Guid.TryParseExact(proof["secret_id"]?.ToString(), "D", out var id) && id != Guid.Empty &&
            ValidVersions(proof) && (!paired || proof["git_token_rotation"] is JsonObject { Count: 6 } git &&
                git["server"]?.ToString() == "GnOuGo.Git.Mcp" && git["field"]?.ToString() == "Git__Token" &&
                git["vault_key"]?.ToString() == GitSecret && Guid.TryParseExact(git["secret_id"]?.ToString(), "D", out var gitId) &&
                gitId != Guid.Empty && gitId != id && ValidVersions(git));

    private static bool ValidVersions(JsonObject record) =>
        record["previous_version"] is JsonValue previous && previous.TryGetValue<int>(out var from) && from > 0 &&
        record["current_version"] is JsonValue current && current.TryGetValue<int>(out var to) && to > from;
}

internal sealed partial class KeyVaultBenchmarkModel
{
    internal async Task<JsonObject> GithubCredentialProofAsync(string root, string parentId, int from, int to, CancellationToken ct,
        int? gitFrom = null, int? gitTo = null)
    {
        if (gitFrom.HasValue != gitTo.HasValue)
            throw new PlanningConflictException("Supply both bundled Git token versions.");
        var path = WorkspaceDatabasePath(root, null, KeyVaultDatabasePathResolver.DefaultRelativePath);
        await using var db = new KeyVaultDbContext(new DbContextOptionsBuilder<KeyVaultDbContext>()
            .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()).Options);
        var vault = new KeyVaultService(db);
        const string reader = "benchmark-credential-rotation";
        async Task<(SecretValueDto Previous, SecretValueDto Current)> Read(string key, int prior, int latest)
        {
            if (prior < 1 || latest <= prior) throw new PlanningConflictException("Supply the earlier and current record versions.");
            var current = await vault.GetSecretAsync(key, null, reader, ct);
            if (current?.Version != latest) throw new PlanningConflictException("The requested current record version is missing or superseded.");
            var previous = await vault.GetSecretVersionAsync(key, null, reader, prior, ct);
            if (previous?.Version != prior) throw new PlanningConflictException("The requested historical record version is missing.");
            return (previous, current);
        }
        var github = await Read(BenchmarkCampaign.GithubSecret, from, to);
        var git = gitFrom is int previousGit && gitTo is int currentGit ? await Read(BenchmarkCampaign.GitSecret, previousGit, currentGit)
            : ((SecretValueDto Previous, SecretValueDto Current)?)null;
        return await ProveGithubCredentialAsync(parentId, github.Previous, github.Current, ct, git);
    }

    private static bool SameRecord(string key, SecretValueDto previous, SecretValueDto current) =>
        previous.Key == key && current.Key == key && previous.Id == current.Id && current.Id != Guid.Empty &&
        previous.TenantId is null && current.TenantId is null && previous.Version > 0 && current.Version > previous.Version;

    internal async Task<JsonObject> ProveGithubCredentialAsync(string parentId, SecretValueDto previous, SecretValueDto current, CancellationToken ct,
        (SecretValueDto Previous, SecretValueDto Current)? git = null)
    {
        var parent = await _campaign.LoadAsync("planning-evaluation-configuration", "configuration:" + parentId, ct)
            ?? throw new PlanningConflictException("The previous registered configuration is missing.");
        if (!SameRecord(BenchmarkCampaign.GithubSecret, previous, current))
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
        JsonObject? gitProof = null;
        if (git is { } token)
        {
            if (!SameRecord(BenchmarkCampaign.GitSecret, token.Previous, token.Current) || token.Current.Id == current.Id ||
                string.IsNullOrWhiteSpace(token.Previous.Value) || string.IsNullOrWhiteSpace(token.Current.Value) ||
                token.Previous.Value == token.Current.Value || !restored.McpServers.TryGetValue("GnOuGo.Git.Mcp", out var server) ||
                server.EnvironmentVariables is not { } environment || !environment.TryGetValue("Git__Token", out var loaded) || loaded != token.Current.Value)
                throw new PlanningConflictException("The selected versions must establish a change to the effective bundled Git token.");
            environment["Git__Token"] = token.Previous.Value;
            gitProof = new() { ["server"] = "GnOuGo.Git.Mcp", ["field"] = "Git__Token", ["vault_key"] = BenchmarkCampaign.GitSecret,
                ["secret_id"] = token.Current.Id.ToString("D"), ["previous_version"] = token.Previous.Version, ["current_version"] = token.Current.Version };
        }
        var reconstructed = HostFingerprint(restored);
        if (reconstructed != parent["host_configuration_hash"]?.ToString())
        {
            string[] fields = ["name", "transport", "url", "command", "args", "description", "environmentVariables", "authType", "discoveryTimeoutSeconds", "callTimeoutSeconds"];
            var changed = fields.Where(f => !JsonNode.DeepEquals(before[f], after[f])).ToArray();
            throw new PlanningConflictException("Restoring the selected credentials did not reproduce the funded host. Changed named GitHub settings: " +
                (changed.Length == 0 ? "none detected; other effective differences cannot be localized from the retained fingerprint." : string.Join(", ", changed) + "."));
        }
        var proof = new JsonObject { ["previous_configuration_id"] = parentId, ["previous_host_configuration_hash"] = reconstructed,
            ["previous_declared_metadata_hash"] = metadataHash, ["host_configuration_hash"] = host, ["server"] = "Github", ["field"] = "ApiKey",
            ["vault_key"] = BenchmarkCampaign.GithubSecret, ["secret_id"] = current.Id.ToString("D"),
            ["previous_version"] = previous.Version, ["current_version"] = current.Version };
        if (gitProof is not null) proof["git_token_rotation"] = gitProof;
        return proof;
    }
}
