using GnOuGo.Files.Server.Data;
using GnOuGo.Files.Server.Models;
using GnOuGo.Files.Server.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Files.Server.Tests;

public sealed class FilesMetadataRepositoryTests
{
    [Fact]
    public async Task InsertGetListDelete_RoundTripsMetadataWithUtcTimestamps()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-files-tests", Guid.NewGuid().ToString("N"));
        var options = Microsoft.Extensions.Options.Options.Create(new FilesServerOptions
        {
            StorageRootPath = root,
            DatabasePath = Path.Combine(root, "files.db")
        });
        var paths = new FilesStoragePaths(options);
        var services = new ServiceCollection()
            .AddSingleton<IOptions<FilesServerOptions>>(options)
            .AddSingleton(paths)
            .BuildServiceProvider();

        try
        {
            await FilesDatabaseBootstrap.InitializeAsync(services);
            var repository = new FilesMetadataRepository(paths);
            var createdUtc = DateTimeOffset.UtcNow;
            var expiresUtc = createdUtc.AddMinutes(5);
            var record = new FileRecord
            {
                Id = "test-id",
                TenantId = "tenant-a",
                OriginalFileName = "sample.txt",
                ContentType = "text/plain",
                StoredFileName = "test-id.blob",
                StoredPath = Path.Combine(root, "test-id.blob"),
                SizeBytes = 42,
                CreatedUtc = createdUtc,
                ExpiresUtc = expiresUtc
            };

            await repository.InsertAsync(record, CancellationToken.None);

            var loaded = await repository.GetAsync(record.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(record.Id, loaded.Id);
            Assert.Equal(TimeSpan.Zero, loaded.CreatedUtc.Offset);
            Assert.Equal(TimeSpan.Zero, loaded.ExpiresUtc.Offset);
            Assert.Equal(record.SizeBytes, loaded.SizeBytes);

            var records = await repository.ListAsync(CancellationToken.None);
            Assert.Single(records);

            await repository.DeleteAsync(record.Id, CancellationToken.None);
            Assert.Empty(await repository.ListAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}



