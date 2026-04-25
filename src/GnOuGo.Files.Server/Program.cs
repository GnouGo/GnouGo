using GnOuGo.Files.Server.Data;
using GnOuGo.Files.Server.Data.CompiledModels;
using GnOuGo.Files.Server.Options;
using GnOuGo.Files.Server.Services;
using GnOuGo.Files.Server.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});

builder.Services.AddSingleton<IOptions<FilesServerOptions>>(_ => Options.Create(ReadFilesOptions(builder.Configuration)));
builder.Services.AddSingleton<FilesStoragePaths>();
builder.Services.AddDbContext<FilesDbContext>((serviceProvider, options) =>
{
    var paths = serviceProvider.GetRequiredService<FilesStoragePaths>();
    options.UseSqlite($"Data Source={paths.DatabasePath};Pooling=False")
        .UseModel(FilesDbContextModel.Instance);
});
builder.Services.AddSingleton<FilesMetadataRepository>();
builder.Services.AddScoped<FileStorageService>();
builder.Services.AddHostedService<FilePurgeWorker>();
builder.Services.AddRouting();

var app = builder.Build();

await FilesDatabaseBootstrap.InitializeAsync(app.Services);

app.MapFilesApi();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();

static FilesServerOptions ReadFilesOptions(IConfiguration configuration)
{
    var section = configuration.GetSection(FilesServerOptions.SectionName);
    return new FilesServerOptions
    {
        DefaultTtlHours = double.TryParse(section[nameof(FilesServerOptions.DefaultTtlHours)], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var defaultTtlHours)
            ? defaultTtlHours
            : 12,
        PurgeIntervalSeconds = int.TryParse(section[nameof(FilesServerOptions.PurgeIntervalSeconds)], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var purgeIntervalSeconds)
            ? purgeIntervalSeconds
            : 60,
        StorageRootPath = section[nameof(FilesServerOptions.StorageRootPath)],
        DatabasePath = section[nameof(FilesServerOptions.DatabasePath)],
        StreamBufferSizeBytes = int.TryParse(section[nameof(FilesServerOptions.StreamBufferSizeBytes)], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var streamBufferSizeBytes)
            ? streamBufferSizeBytes
            : 1024 * 128
    };
}




