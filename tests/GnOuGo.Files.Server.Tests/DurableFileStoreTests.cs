using System.Security.Cryptography;
using System.Text;
using GnOuGo.Files.Core;
using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Files.Server.Tests;

public sealed class DurableFileStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gnougo-files-test-").FullName;
    private readonly Clock _clock = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private DurableFileStore Open() => new(_root, new KeyVaultRecordStore(Path.Combine(_root, "keys.db")), _clock);

    [Fact]
    public async Task HoldsOutliveUploadAndAreReleasedIndependentlyAfterRestart()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 150_000) + "private-file");
        var expiry = _clock.Now.AddHours(1);
        var file = await Open().UploadAsync("tenant", new MemoryStream(bytes), "image/png", "private.png", expiry, Ct);
        var run = new FileOwner(FileOwnerKind.Execution, "same");
        var plan = new FileOwner(FileOwnerKind.Planning, "same");
        var version = FileOwner.SavedVersion("agent", "approved workflow");
        await Open().ClaimAsync("tenant", file.Id, null, run, Ct);
        await Open().ClaimAsync("tenant", file.Id, run, plan, Ct);
        await Open().ClaimAsync("tenant", file.Id, plan, version, Ct);
        await Open().ClaimAsync("tenant", file.Id, plan, version, Ct);
        var preview = await Open().InspectRetentionAsync("tenant", run, expiry, Ct);
        Assert.Equal(1, preview.Objects);
        Assert.True(preview.SharedBytes > bytes.Length);
        Assert.Equal(0, preview.ExclusiveBytes);
        _clock.Now = expiry;
        await Open().SweepAsync(Ct);
        Assert.False(await Open().CopyAsync("tenant", file.Id, null, Stream.Null, Ct));
        Assert.False(await Open().CopyAsync("other", file.Id, run, Stream.Null, Ct));
        Assert.Null(await Open().ReadImageAsync("tenant", new(FileOwnerKind.Execution, "other"), file.Digest, Ct));
        await Open().ReleaseAsync("tenant", run, Ct);
        Assert.Null(await Open().ReadImageAsync("tenant", run, file.Digest, Ct));
        await Open().ReleaseAsync("tenant", plan, Ct);
        Assert.Equal(bytes, await Open().ReadImageAsync("tenant", version, file.Digest, Ct));
        await Open().ReleaseAsync("tenant", version, Ct);
        await Open().SweepAsync(Ct);
        Assert.Empty(Directory.GetFiles(_root, "*.enc", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TamperingAndMissingManifestBlockReadsAndWrongVaultCannotCollect()
    {
        var owner = new FileOwner(FileOwnerKind.Execution, "one");
        var bytes = Encoding.UTF8.GetBytes("confidential-image-pixels");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var file = await Open().PutImageAsync("tenant", owner, digest, bytes, Ct);
        Assert.Equal(file.Id, (await Open().PutImageAsync("tenant", owner, digest, bytes, Ct)).Id);
        var path = Assert.Single(Directory.GetFiles(_root, "*.enc", SearchOption.AllDirectories));
        var encrypted = await File.ReadAllBytesAsync(path, Ct);
        Assert.DoesNotContain("confidential", Encoding.UTF8.GetString(encrypted));
        var otherVault = new DurableFileStore(_root, new KeyVaultRecordStore(Path.Combine(_root, "other.db")));
        await otherVault.SweepAsync(Ct);
        Assert.True(File.Exists(path));
        encrypted[0] ^= 1;
        await File.WriteAllBytesAsync(path, encrypted, Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Open().ReadImageAsync("tenant", owner, digest, Ct));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
