using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;

namespace GnOuGo.Files.Core;

public enum FileOwnerKind { Execution, Planning, WorkflowPlanning, SavedVersion, Deliverable }
public sealed record FileOwner(FileOwnerKind Kind, string Id)
{
    public static FileOwner SavedVersion(string name, string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(FileOwnerKind.SavedVersion, name + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))));
    }

    internal string Prefix
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Id);
            if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
            return Kind + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Id))) + "/";
        }
    }
}

public sealed record StoredFile(string Id, string Digest, long Size, string ContentType, string Name);
internal sealed record FileManifest(string Tenant, string Id, byte[] Key, string Digest, long Size,
    string ContentType, string Name, DateTimeOffset? UploadUntil, List<FileOwner> Owners, bool Writing = false)
{
    public StoredFile Info => new(Id, Digest, Size, ContentType, Name);
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(FileManifest))]
internal partial class FileJson : JsonSerializerContext;

/// <summary>File contents live in Files. KeyVault protects keys, identities, metadata and grants.</summary>
public sealed class DurableFileStore(string root, IKeyVaultRecordStore records, TimeProvider? time = null)
{
    private const string Manifests = "files-manifests-v2", Grants = "files-owner-grants-v2", Author = "GnOuGo.Files";
    public const long UploadLimit = 64L * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetFullPath(root), "objects-v2");
    private readonly TimeProvider _clock = time ?? TimeProvider.System;

    public static string ResolveRoot(string? configured, string baseDirectory) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(GnOuGoWorkspace.ResolveDefaultWorkingDirectorySafe(contentRootPath: baseDirectory), GnOuGoWorkspace.WorkspaceDataSubfolder, "Files")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

    private static bool ValidId(string id) => id.Length is 32 or 64 && id.All(char.IsAsciiHexDigitLower);
    private static void Validate(string tenant, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (!ValidId(id)) throw new ArgumentException("Invalid file ID.", nameof(id));
    }

    private async Task<FileStream> LockAsync(string id, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, id[..2] + ".lock");
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(20, ct); }
        }
    }

    private string PathFor(string id, string extension) => Path.Combine(_directory, id + extension);
    private async Task<FileManifest?> FindAsync(string tenant, string id, CancellationToken ct)
    {
        var record = await records.GetAsync(Manifests, tenant, id, Author, ct);
        if (record is null) return null;
        var file = JsonSerializer.Deserialize(record.Value, FileJson.Default.FileManifest);
        if (file is null || file.Id != id || file.Tenant != tenant || file.Key.Length != 32 ||
            file.Size is < 0 or > UploadLimit || (!file.Writing && file.Digest.Length != 64))
            throw new InvalidDataException("Invalid file manifest.");
        return file;
    }

    private Task SaveAsync(FileManifest file, CancellationToken ct) =>
        records.UpsertAsync(Manifests, file.Tenant, file.Id, JsonSerializer.Serialize(file, FileJson.Default.FileManifest), Author, ct);

    public Task<StoredFile> UploadAsync(string tenant, Stream input, string contentType, string name,
        DateTimeOffset until, CancellationToken ct) =>
        WriteAsync(tenant, Guid.NewGuid().ToString("N"), input, contentType, name, until, null, null, ct);

    public Task<StoredFile> PutImageAsync(string tenant, FileOwner owner, string digest, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > 4 * 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest)
            throw new ArgumentException("Invalid image length or digest.", nameof(bytes));
        var identity = new System.Text.Json.Nodes.JsonArray(tenant, owner.Prefix, digest).ToJsonString();
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return WriteAsync(tenant, id, new MemoryStream(bytes, false), "application/octet-stream", "", null, owner, digest, ct);
    }

    private async Task<StoredFile> WriteAsync(string tenant, string id, Stream input, string mime, string name,
        DateTimeOffset? until, FileOwner? owner, string? expectedDigest, CancellationToken ct)
    {
        Validate(tenant, id);
        await using var lease = await LockAsync(id, ct);
        var prior = await FindAsync(tenant, id, ct);
        if (prior is { Writing: false })
        {
            if (owner is null || prior.Digest != expectedDigest || !prior.Owners.Contains(owner))
                throw new InvalidDataException("File identity conflict.");
            return prior.Info;
        }
        if (prior is not null) await CollectLockedAsync(tenant, id, prior, ct);
        else if (File.Exists(PathFor(id, ".enc")) || File.Exists(PathFor(id, ".part")))
            throw new InvalidDataException("The encrypted file has no readable manifest.");
        // Persist a protected write intent before staging any ciphertext. Collection takes
        // the same lease and needs a matching manifest before it can unlink bytes.
        await File.WriteAllTextAsync(PathFor(id, ".owner"), tenant, ct);
        var key = RandomNumberGenerator.GetBytes(32);
        var pending = new FileManifest(tenant, id, key, "", 0, mime, name, null, [], Writing: true);
        await SaveAsync(pending, ct);
        (long size, string digest) content;
        await using (var stream = new FileStream(PathFor(id, ".part"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            content = await EncryptedFileContent.EncryptAsync(input, stream, key, UploadLimit, ct);
            stream.Flush(true);
        }
        File.Move(PathFor(id, ".part"), PathFor(id, ".enc"), true);
        var file = new FileManifest(tenant, id, key, content.digest, content.size, mime, name, until, []);
        if (owner is not null) await AddGrantAsync(file, owner, ct);
        else await SaveAsync(file, ct);
        return file.Info;
    }

    private async Task AddGrantAsync(FileManifest file, FileOwner owner, CancellationToken ct)
    {
        // This lookup index alone grants no access. Publish it before the authoritative manifest.
        var link = owner.Prefix + file.Digest + "/" + file.Id;
        await records.UpsertAsync(Grants, file.Tenant, link, file.Id, Author, ct);
        if (!file.Owners.Contains(owner)) file.Owners.Add(owner);
        await SaveAsync(file, ct);
    }

    // Only trusted host code constructs owner identities. Null source means the temporary upload.
    public async Task<StoredFile> ClaimAsync(string tenant, string id, FileOwner? source, FileOwner destination, CancellationToken ct)
    {
        Validate(tenant, id);
        await using var lease = await LockAsync(id, ct);
        var file = await FindAsync(tenant, id, ct);
        if (file is null || !Authorized(file, source)) throw new FileNotFoundException("File grant unavailable.");
        await AddGrantAsync(file, destination, ct);
        return file.Info;
    }

    public async Task<StoredFile?> InspectUploadAsync(string tenant, string id, CancellationToken ct)
    {
        Validate(tenant, id);
        var file = await FindAsync(tenant, id, ct);
        return file is not null && Authorized(file, null) ? file.Info : null;
    }

    private bool Authorized(FileManifest file, FileOwner? owner) => !file.Writing && (owner is null
        ? file.UploadUntil > _clock.GetUtcNow() : file.Owners.Contains(owner));

    public async Task<bool> CopyAsync(string tenant, string id, FileOwner? owner, Stream output, CancellationToken ct)
    {
        Validate(tenant, id);
        FileManifest? file;
        FileStream content;
        await using (var lease = await LockAsync(id, ct))
        {
            file = await FindAsync(tenant, id, ct);
            if (file is null || !Authorized(file, owner)) return false;
            content = new FileStream(PathFor(id, ".enc"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        await using (content) await EncryptedFileContent.DecryptAsync(content, output, file, ct);
        return true;
    }

    public async Task<byte[]?> ReadImageAsync(string tenant, FileOwner owner, string digest, CancellationToken ct)
    {
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigitLower)) throw new ArgumentException("Invalid image digest.");
        await foreach (var link in records.EnumerateKeysAsync(Grants, tenant, owner.Prefix + digest + "/", Author, ct))
        {
            var id = link[(link.LastIndexOf('/') + 1)..];
            // A document may also be held by this owner. Check size before buffering it.
            if ((await FindAsync(tenant, id, ct))?.Size is > 4 * 1024 * 1024) continue;
            using var output = new MemoryStream();
            if (await CopyAsync(tenant, id, owner, output, ct)) return output.ToArray();
        }
        return null;
    }

    public async Task TransferReferencesAsync(string tenant, FileOwner source, FileOwner destination,
        System.Text.Json.Nodes.JsonNode? bindings, CancellationToken ct)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        void Visit(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    foreach (var pair in obj) Visit(pair.Value);
                    break;
                case System.Text.Json.Nodes.JsonArray array:
                    foreach (var value in array) Visit(value);
                    break;
                case System.Text.Json.Nodes.JsonValue scalar when scalar.TryGetValue<string>(out var text) && ValidId(text):
                    references.Add(text);
                    break;
            }
        }
        Visit(bindings);
        if (references.Count == 0) return;
        await foreach (var link in records.EnumerateKeysAsync(Grants, tenant, source.Prefix, Author, ct))
        {
            var parts = link[source.Prefix.Length..].Split('/');
            if (parts.Length != 2) throw new InvalidDataException("Invalid file grant.");
            if (references.Contains(parts[0]) || references.Contains(parts[1]))
                await ClaimAsync(tenant, parts[1], source, destination, ct);
        }
    }

    public async Task ReleaseAsync(string tenant, FileOwner owner, CancellationToken ct)
    {
        await foreach (var link in records.EnumerateKeysAsync(Grants, tenant, owner.Prefix, Author, ct))
        {
            var id = link[(link.LastIndexOf('/') + 1)..];
            Validate(tenant, id);
            await using var lease = await LockAsync(id, ct);
            if (await FindAsync(tenant, id, ct) is { } file && file.Owners.Remove(owner))
            {
                await SaveAsync(file, ct);
                await CollectLockedAsync(tenant, id, file, ct);
            }
            await records.DeleteAsync(Grants, tenant, link, Author, ct);
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.owner"))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!ValidId(id)) continue;
            await using var lease = await LockAsync(id, ct);
            if (!File.Exists(path)) continue;
            var tenant = await File.ReadAllTextAsync(path, ct);
            // A torn locator can only precede initial manifest publication.
            if (string.IsNullOrWhiteSpace(tenant)) continue;
            await CollectLockedAsync(tenant, id, await FindAsync(tenant, id, ct), ct);
        }
    }

    private async Task CollectLockedAsync(string tenant, string id, FileManifest? file, CancellationToken ct)
    {
        if (file is null)
        {
            // A different or incomplete vault cannot authorize destruction in this directory.
            if (!File.Exists(PathFor(id, ".enc")) && !File.Exists(PathFor(id, ".part"))) File.Delete(PathFor(id, ".owner"));
            return;
        }
        if (!file.Writing && (file.Owners.Count > 0 || Authorized(file, null))) return;
        File.Delete(PathFor(id, ".part"));
        File.Delete(PathFor(id, ".enc"));
        await records.DeleteAsync(Manifests, tenant, id, Author, ct);
        File.Delete(PathFor(id, ".owner"));
    }
}
