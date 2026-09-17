using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal sealed class RetainedDiagnosticRecords(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
{
    internal const string LocalCase = "schema5-governing-applicability-diagnostics-1:local";
    internal int WritesAttempted { get; private set; }
    internal static void RequireCase(string id)
    {
        if (id != LocalCase) throw new InvalidOperationException("Only the retained governing-applicability LOCAL is authorized for read-only adjudication.");
    }
    public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
        => inner.GetAsync(collection, tenantId, key, author, ct);
    public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default)
        => inner.ListAsync(collection, tenantId, author, ct);
    public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
    { WritesAttempted++; throw new InvalidOperationException("Retained adjudication cannot write encrypted records."); }
    public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
    { WritesAttempted++; throw new InvalidOperationException("Retained adjudication cannot delete encrypted records."); }
}
