using GnOuGo.Files.Server.Data;
using GnOuGo.Files.Server.Data.CompiledModels;
using GnOuGo.Files.Server.Models;
using GnOuGo.Files.Server.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Files.Server.Tests;

public sealed class FilesMetadataRepositoryTests
{
    [Fact]
    public async Task InsertGetListDelete_RoundTripsMetadataWithUtcTimestamps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "gnougo-files-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(root, "files.db");
        Directory.CreateDirectory(root);
        var options = Microsoft.Extensions.Options.Options.Create(new FilesServerOptions
        {
            StorageRootPath = root,
            DatabasePath = dbPath
        });
        var paths = new FilesStoragePaths(options);
        var services = new ServiceCollection()
            .AddSingleton<IOptions<FilesServerOptions>>(options)
            .AddSingleton(paths)
            .AddDbContext<FilesDbContext>(opt => opt.UseSqlite($"Data Source={dbPath};Pooling=False").UseModel(FilesDbContextModel.Instance))
            .AddScoped<FilesMetadataRepository>()
            .BuildServiceProvider();

        try
        {
            await FilesDatabaseBootstrap.InitializeAsync(services, cancellationToken);
            using var scope = services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<FilesMetadataRepository>();
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

            await repository.InsertAsync(record, cancellationToken);

            // Initialization must be idempotent for an existing compatible database.
            await FilesDatabaseBootstrap.InitializeAsync(services, cancellationToken);
            await AssertExpectedIndexesAsync(dbPath, cancellationToken);

            var loaded = await repository.GetAsync(record.Id, record.TenantId, cancellationToken);
            Assert.NotNull(loaded);
            Assert.Equal(record.Id, loaded.Id);
            Assert.Equal(record.SizeBytes, loaded.SizeBytes);

            var records = await repository.ListAsync(record.TenantId, cancellationToken);
            Assert.Single(records);

            Assert.Null(await repository.GetAsync(record.Id, "tenant-b", cancellationToken));
            Assert.Empty(await repository.ListAsync("tenant-b", cancellationToken));

            await repository.DeleteAsync(record.Id, cancellationToken);
            Assert.Empty(await repository.ListAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertExpectedIndexesAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);

        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_index_list('files') WHERE origin = 'c';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                indexNames.Add(reader.GetString(0));
        }

        Assert.Contains("IX_files_expires_utc", indexNames);
        Assert.Contains("IX_files_tenant_id_expires_utc", indexNames);

        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "SELECT name FROM pragma_index_info('IX_files_tenant_id_expires_utc') ORDER BY seqno;";
        var columns = new List<string>();
        await using var columnsReader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
        while (await columnsReader.ReadAsync(cancellationToken))
            columns.Add(columnsReader.GetString(0));

        Assert.Equal(["tenant_id", "expires_utc"], columns);
    }
}
