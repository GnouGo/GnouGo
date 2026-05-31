using System.Globalization;
using GnOuGo.Files.Server.Data;
using GnOuGo.Files.Server.Data.CompiledModels;
using GnOuGo.Files.Server.Options;
using GnOuGo.Files.Server.Services;
using GnOuGo.Files.Server.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GnOuGo.Files.Server;

public static class FilesServerHostingExtensions
{
    public static IServiceCollection AddGnOuGoFilesServer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IOptions<FilesServerOptions>>(_ => Microsoft.Extensions.Options.Options.Create(ReadFilesOptions(configuration)));
        services.AddSingleton<FilesStoragePaths>();
        services.AddDbContext<FilesDbContext>((serviceProvider, options) =>
        {
            var paths = serviceProvider.GetRequiredService<FilesStoragePaths>();
            options.UseSqlite($"Data Source={paths.DatabasePath};Pooling=False")
                .UseModel(FilesDbContextModel.Instance);
        });
        services.AddScoped<FilesMetadataRepository>();
        services.AddScoped<FileStorageService>();
        services.AddHostedService<FilePurgeWorker>();

        return services;
    }

    public static async Task InitializeGnOuGoFilesServerAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        await FilesDatabaseBootstrap.InitializeAsync(services);
    }

    public static IEndpointRouteBuilder MapGnOuGoFilesServer(this IEndpointRouteBuilder endpoints, bool includeHealthEndpoint = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapFilesApi(includeHealthEndpoint);
    }

    private static FilesServerOptions ReadFilesOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(FilesServerOptions.SectionName);
        return new FilesServerOptions
        {
            DefaultTtlHours = double.TryParse(section[nameof(FilesServerOptions.DefaultTtlHours)], NumberStyles.Float, CultureInfo.InvariantCulture, out var defaultTtlHours)
                ? defaultTtlHours
                : 12,
            PurgeIntervalSeconds = int.TryParse(section[nameof(FilesServerOptions.PurgeIntervalSeconds)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var purgeIntervalSeconds)
                ? purgeIntervalSeconds
                : 60,
            StorageRootPath = section[nameof(FilesServerOptions.StorageRootPath)],
            DatabasePath = section[nameof(FilesServerOptions.DatabasePath)],
            StreamBufferSizeBytes = int.TryParse(section[nameof(FilesServerOptions.StreamBufferSizeBytes)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var streamBufferSizeBytes)
                ? streamBufferSizeBytes
                : 1024 * 128
        };
    }
}
