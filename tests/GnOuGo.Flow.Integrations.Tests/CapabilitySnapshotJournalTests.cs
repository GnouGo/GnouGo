using Xunit;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class CapabilitySnapshotJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gnougo-catalog-snapshots-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private KeyVaultRecordStore Records() => new(Path.Combine(_directory, "vault.db"));

    [Fact]
    public async Task ImmutableEncryptedSnapshotSurvivesReopenAndContainsNoPlaintextBusinessMetadata()
    {
        var snapshot = Snapshot();
        var reference = await new PlanningCapabilitySnapshotJournal(Records()).PublishAsync("tenant", "session", "action", snapshot, Ct);
        var restored = await new PlanningCapabilitySnapshotJournal(Records()).ReadAsync(reference, "tenant", "session", "action", "source", Ct);
        Assert.Equal(Serialize(snapshot), Serialize(restored));
        var again = await new PlanningCapabilitySnapshotJournal(Records()).PublishAsync("tenant", "session", "action", snapshot, Ct);
        Assert.Equal(reference, again);
        foreach (var file in Directory.EnumerateFiles(_directory))
            Assert.DoesNotContain("PRIVATE_CATALOG_BUSINESS", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("session")]
    [InlineData("action")]
    [InlineData("source")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("configuration")]
    public async Task ExactOwnershipAndHashAreRequiredOnEveryRead(string substitution)
    {
        var journal = new PlanningCapabilitySnapshotJournal(Records());
        var reference = await journal.PublishAsync("tenant", "session", "action", Snapshot(), Ct);
        if (substitution == "key") reference = reference with { Key = "another-record" };
        if (substitution == "version") reference = reference with { Version = new string('a', 64) };
        if (substitution == "configuration") reference = reference with { ConfigurationFingerprint = "another-configuration" };
        await Assert.ThrowsAsync<PlanningConflictException>(() => journal.ReadAsync(reference,
            substitution == "tenant" ? "other" : "tenant", substitution == "session" ? "other" : "session",
            substitution == "action" ? "other" : "action", substitution == "source" ? "other" : "source", Ct));
    }

    [Fact]
    public async Task InterruptedPublicationIsIdempotentAndMissingOrCorruptPayloadNeverRefetchesSource()
    {
        var records = Records(); var interrupted = new InterruptPublication(records);
        var journal = new PlanningCapabilitySnapshotJournal(interrupted);
        await Assert.ThrowsAsync<OperationCanceledException>(() => journal.PublishAsync("tenant", "session", "action", Snapshot(), Ct));
        interrupted.Interrupt = false;
        var reference = await journal.PublishAsync("tenant", "session", "action", Snapshot(), Ct);
        Assert.Equal(1, interrupted.Writes);
        await records.UpsertAsync(PlanningCapabilitySnapshotJournal.Collection, "tenant", reference.Key, "{}", "test", Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => journal.ReadAsync(reference, "tenant", "session", "action", "source", Ct));
        await records.DeleteAsync(PlanningCapabilitySnapshotJournal.Collection, "tenant", reference.Key, "test", Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => journal.ReadAsync(reference, "tenant", "session", "action", "source", Ct));
    }

    private static CapabilitySourceSnapshot Snapshot() => new("source", PlanningGraphCompiler.Fingerprint("configured source"),
        [new() { Id = "operation", Version = "version", Description = "PRIVATE_CATALOG_BUSINESS", InputSchema = new() { ["type"] = "object" } }]);
    private static string Serialize(CapabilitySourceSnapshot value) => JsonSerializer.Serialize(value, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class InterruptPublication(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal bool Interrupt = true;
        internal int Writes;
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct) => inner.GetAsync(c, t, k, a, ct);
        public async Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct)
        {
            Writes++; var result = await inner.UpsertAsync(c, t, k, v, a, ct);
            if (Interrupt) throw new OperationCanceledException("Interrupted after immutable payload publication, before checkpoint reference.");
            return result;
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct) => throw new InvalidOperationException("Snapshot reads must be targeted.");
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct) => inner.DeleteAsync(c, t, k, a, ct);
    }
}
