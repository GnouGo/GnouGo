using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ChatFileAttachmentsTests
{
    [Fact]
    public async Task ChatSelectedImageAndDocumentHaveIndependentRunAndSavedVersionGrants()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-attachments-test-").FullName;
        try
        {
            var vault = new KeyVaultRecordStore(Path.Combine(root, "vault.db"));
            var clock = new TestClock();
            var files = new DurableFileStore(Path.Combine(root, "files"), vault, clock);
            var store = new EncryptedWorkflowRunStore(vault, Path.Combine(root, "index.db"), Path.Combine(root, "owners"),
                null, new(2, clock), files);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ZkAAAAASUVORK5CYII=");
            var until = clock.Now.AddHours(12);
            var image = await files.UploadAsync("tenant", new MemoryStream(png), "image/png", "photo.png", until, ct);
            var doc = await files.UploadAsync("tenant", new MemoryStream(Encoding.UTF8.GetBytes("document")), "text/plain", "note.txt", until, ct);
            var baseUrl = new Uri("https://files.example/app");
            string[] selected = [GnOuGo.Files.Server.Web.FilesLinks.ResolveUploadId(baseUrl + "/api/files/" + image.Id, baseUrl),
                GnOuGo.Files.Server.Web.FilesLinks.ResolveUploadId("/api/files/" + doc.Id, baseUrl)];
            Assert.Throws<ArgumentException>(() => GnOuGo.Files.Server.Web.FilesLinks.ResolveUploadId("https://other.example/api/files/" + image.Id, baseUrl));
            var inputs = new JsonObject();
            await ChatFileAttachments.BindAsync(files, "tenant", selected, inputs, ct);
            Assert.Equal(2, inputs["attachments"]!.AsArray().Count);
            var reference = Assert.Single(inputs["images"]!.AsArray())!.AsObject();
            Assert.Equal(5, reference.Count);
            Assert.Equal(image.Digest, reference["sha256"]!.GetValue<string>());
            await Assert.ThrowsAsync<FileNotFoundException>(() => ChatFileAttachments.BindAsync(files, "elsewhere", selected, new(), ct));
            WorkflowRun Run(string id) => new() { TenantId = "tenant", RunId = id, Limits = new() { TenantId = "tenant", RunId = id } };
            await store.CreateWithUploadsAsync(Run("chat"), selected, null, ct);
            var plan = new FileOwner(FileOwnerKind.WorkflowPlanning, "plan");
            await files.TransferReferencesAsync("tenant", new(FileOwnerKind.Execution, "chat"), plan, inputs, ct);
            var yaml = "version: 1\nworkflows:\n  use:\n    steps:\n      - { id: keep, type: set, input: { value: '" + image.Digest + "' } }\n";
            var version = FileOwner.SavedVersion("agent", yaml);
            await files.TransferReferencesAsync("tenant", plan, version, reference, ct);
            clock.Now = until;
            await files.ReleaseAsync("tenant", new(FileOwnerKind.Execution, "chat"), ct);
            await files.ReleaseAsync("tenant", plan, ct);
            var fresh = Run("fresh");
            fresh.WorkflowYaml = yaml;
            await store.CreateWithUploadsAsync(fresh, [], "agent", ct);
            await files.ReleaseAsync("tenant", version, ct);
            Assert.Equal(png, await ((IWorkflowImageStore)store).GetAsync("tenant", "fresh", image.Digest, ct));
            Assert.False(await files.CopyAsync("tenant", doc.Id, new(FileOwnerKind.Execution, "fresh"), Stream.Null, ct));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
