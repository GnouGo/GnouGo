using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkEvidenceDigestTests
{
    [Fact]
    public void DigestPreservesTheLegacyUtf8JsonRecipeAcrossEscapingAndChunkBoundaries()
    {
        using (var empty = new BenchmarkCampaign.EvidenceDigest())
            Assert.Equal(PlanningGraphCompiler.Fingerprint(""), empty.Finish());
        var values = new[] { "", "quote\" slash\\ controls\r\n\t\b\f <> & '+ é 日本語 🚀", "\ud800x\udc00",
            new string('x', 8191) + "🚀\n" + new string('é', 16385), string.Concat(Enumerable.Range(0, 128).Select(i => (char)i)) };
        foreach (var value in values)
        {
            KeyVaultRecordValue[] rows = [new("collection", "benchmark", "A", value, default, default),
                new("collection", "benchmark", "a", "retained\nvalue", default, default)];
            var legacy = string.Join('\n', rows.Select(r => new JsonArray(r.Collection, r.Key, r.Value).ToJsonString()));
            using var digest = new BenchmarkCampaign.EvidenceDigest();
            foreach (var row in rows) digest.Append(row);
            Assert.Equal(PlanningGraphCompiler.Fingerprint(legacy), digest.Finish());
            Assert.Throws<InvalidOperationException>(() => digest.Append(rows[0]));
            Assert.Throws<InvalidOperationException>(() => digest.Finish());
        }
    }

    [Fact]
    public void DigestProcessesCanonicalEvidenceBeyondTheSingleStringLengthLimit()
    {
        const int count = 32769;
        var row = new KeyVaultRecordValue("c", "benchmark", "k", new string('x', 65536), default, default);
        var rowLength = new JsonArray(row.Collection, row.Key, row.Value).ToJsonString().Length;
        Assert.True((long)rowLength * count + count - 1 > int.MaxValue);
        using var digest = new BenchmarkCampaign.EvidenceDigest(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++) digest.Append(row);
        // Independent SHA-256 reference: ASCII ["c","k",65536 x characters],
        // repeated 32769 times with one LF between records and no final LF.
        Assert.Equal("671c9197c42560dc037cdf751ea355db949401d9448df03504e6d5c49d28ceda", digest.Finish());
    }

    [Fact]
    public void CancellationStopsInspectionHashingWithoutProducingACompletedDigest()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var digest = new BenchmarkCampaign.EvidenceDigest(cancelled.Token);
        Assert.Throws<OperationCanceledException>(() => digest.Append(new("c", "benchmark", "k", "value", default, default)));
        Assert.Throws<OperationCanceledException>(() => digest.Finish());
    }
}
