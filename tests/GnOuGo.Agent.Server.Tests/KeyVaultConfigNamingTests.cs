using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class KeyVaultConfigNamingTests
{
    [Fact]
    public void ResolveExistingSecretKey_PrefersOneCanonicalKeyAndIgnoresLegacyAliasForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var canonical = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var legacy = Assert.Single(KeyVaultConfigNaming.GetCandidateKeys(kind, "Sample").Skip(1));
            var secrets = new[] { Summary(legacy), Summary(canonical) };

            Assert.Equal(canonical, KeyVaultConfigNaming.ResolveExistingSecretKey(secrets, kind, "sample"));
            Assert.Equal(canonical, Assert.Single(KeyVaultConfigNaming.SelectPreferredSecrets(secrets, kind)).Key);
        }
    }

    [Fact]
    public void ResolveExistingSecretKey_RejectsSamePriorityCaseAliasesForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var upper = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var lower = KeyVaultConfigNaming.BuildSecretKey(kind, "sample");
            var secrets = new[] { Summary(upper), Summary(lower) };

            var exception = Assert.Throws<InvalidDataException>(() =>
                KeyVaultConfigNaming.ResolveExistingSecretKey(secrets, kind, "sample"));

            Assert.Equal(
                "KeyVault contains ambiguous configuration keys for the same logical setting.",
                exception.Message);
            Assert.DoesNotContain("Sample", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResolveExistingSecretKey_RejectsDuplicateLegacyAliasesEvenWithCanonicalForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var canonical = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var legacy = Assert.Single(KeyVaultConfigNaming.GetCandidateKeys(kind, "Sample").Skip(1));
            var legacyCaseAlias = Assert.Single(KeyVaultConfigNaming.GetCandidateKeys(kind, "sample").Skip(1));
            var secrets = new[] { Summary(canonical), Summary(legacy), Summary(legacyCaseAlias) };

            var exception = Assert.Throws<InvalidDataException>(() =>
                KeyVaultConfigNaming.ResolveExistingSecretKey(secrets, kind, "sample"));

            Assert.Equal(
                "KeyVault contains ambiguous configuration keys for the same logical setting.",
                exception.Message);
        }
    }

    [Fact]
    public void ResolveWriteSecretKey_ReusesSingleCanonicalCaseVariantForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var existing = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");

            Assert.Equal(
                existing,
                KeyVaultConfigNaming.ResolveWriteSecretKey([Summary(existing)], kind, "sample"));
        }
    }

    [Fact]
    public void ResolveWriteSecretKey_UsesExactCanonicalVariantToRepairExistingAmbiguity()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var exact = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var alias = KeyVaultConfigNaming.BuildSecretKey(kind, "sample");
            var secrets = new[] { Summary(alias), Summary(exact) };

            Assert.Equal(
                exact,
                KeyVaultConfigNaming.ResolveWriteSecretKey(secrets, kind, "Sample"));
            Assert.Equal(
                new[] { alias, exact }.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                KeyVaultConfigNaming.FindEquivalentSecrets(secrets, kind, "SAMPLE")
                    .Select(secret => secret.Key)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    [Fact]
    public async Task SaveConfigSecretAsync_ConsolidatesCanonicalCaseAndLegacyAliasesForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var exact = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var caseAlias = KeyVaultConfigNaming.BuildSecretKey(kind, "sample");
            var legacyAlias = Assert.Single(KeyVaultConfigNaming.GetCandidateKeys(kind, "Sample").Skip(1));
            var store = new FakeKeyVaultRuntimeConfigStore()
                .AddSecret(exact, "previous")
                .AddSecret(caseAlias, "stale-case")
                .AddSecret(legacyAlias, "stale-legacy");
            var service = SmartFlowTestFactory.CreateProvidersService(
                new RecordingLlmClient(),
                keyVaultStore: store);

            var savedKey = await service.SaveConfigSecretAsync(
                kind,
                "Sample",
                "replacement",
                TestContext.Current.CancellationToken);

            Assert.Equal(exact, savedKey);
            Assert.Equal([exact], store.SecretKeys);
            Assert.Equal(
                "replacement",
                await store.GetSecretValueAsync(exact, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SaveConfigSecretAsync_ReusesSoleCanonicalCaseVariantForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var existing = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var store = new FakeKeyVaultRuntimeConfigStore().AddSecret(existing, "previous");
            var service = SmartFlowTestFactory.CreateProvidersService(
                new RecordingLlmClient(),
                keyVaultStore: store);

            var savedKey = await service.SaveConfigSecretAsync(
                kind,
                "sample",
                "replacement",
                TestContext.Current.CancellationToken);

            Assert.Equal(existing, savedKey);
            Assert.Equal([existing], store.SecretKeys);
            Assert.Equal(
                "replacement",
                await store.GetSecretValueAsync(existing, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DeleteConfigSecretsAsync_RemovesEveryEquivalentAliasForEveryKind()
    {
        foreach (var kind in Enum.GetValues<KeyVaultConfigSecretKind>())
        {
            var canonical = KeyVaultConfigNaming.BuildSecretKey(kind, "Sample");
            var caseAlias = KeyVaultConfigNaming.BuildSecretKey(kind, "sample");
            var legacyAlias = Assert.Single(KeyVaultConfigNaming.GetCandidateKeys(kind, "Sample").Skip(1));
            var store = new FakeKeyVaultRuntimeConfigStore()
                .AddSecret(canonical, "canonical")
                .AddSecret(caseAlias, "case-alias")
                .AddSecret(legacyAlias, "legacy-alias");
            var service = SmartFlowTestFactory.CreateProvidersService(
                new RecordingLlmClient(),
                keyVaultStore: store);

            var deleted = await service.DeleteConfigSecretsAsync(
                kind,
                "SAMPLE",
                TestContext.Current.CancellationToken);

            Assert.True(deleted);
            Assert.Empty(store.SecretKeys);
        }
    }

    private static KeyVaultSecretSummary Summary(string key)
        => new(key, "2026-09-02T00:00:00.0000000+00:00", 1);
}
