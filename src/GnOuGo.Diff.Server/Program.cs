using Microsoft.EntityFrameworkCore;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;

var builder = WebApplication.CreateSlimBuilder(args);

// Configuration
var dbPath = builder.Configuration["Database:Path"] ?? "data/gnougo-diff.db";

// Services
builder.Services.AddDbContext<DiffDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<DiffService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Cr�er le r�pertoire data/ s'il n'existe pas
var dataDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
{
    Directory.CreateDirectory(dataDir);
    app.Logger.LogInformation("Created database directory: {DataDir}", dataDir);
}

// Initialiser la base de donn�es
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DiffDbContext>();
    await context.Database.EnsureCreatedAsync();
    app.Logger.LogInformation("Database initialized at: {DbPath}", dbPath);
}

app.UseCors();

// API Endpoints

// Cr�er une nouvelle r�vision
app.MapPost("/api/revisions", async (CreateRevisionRequest request, DiffService service) =>
{
    var revision = await service.CreateRevisionAsync(request);
    return Results.Ok(revision);
});

// R�cup�rer toutes les r�visions d'une entit�
app.MapGet("/api/revisions/{entityType}/{entityId}", async (string entityType, string entityId, DiffService service) =>
{
    var revisions = await service.GetRevisionsAsync(entityType, entityId);
    return Results.Ok(revisions);
});

// R�cup�rer une r�vision � un timestamp sp�cifique
app.MapGet("/api/revisions/{entityType}/{entityId}/at/{timestamp}", async (string entityType, string entityId, DateTimeOffset timestamp, DiffService service) =>
{
    var revision = await service.GetRevisionAtTimestampAsync(entityType, entityId, timestamp);
    return revision != null ? Results.Ok(revision) : Results.NotFound();
});

// Comparer deux r�visions
app.MapGet("/api/revisions/compare/{fromId:guid}/{toId:guid}", async (Guid fromId, Guid toId, DiffService service) =>
{
    var comparison = await service.CompareRevisionsAsync(fromId, toId);
    return comparison != null ? Results.Ok(comparison) : Results.NotFound();
});

// Lister tous les types d'entit�s
app.MapGet("/api/entity-types", async (DiffService service) =>
{
    var types = await service.GetEntityTypesAsync();
    return Results.Ok(types);
});

// Lister toutes les entit�s d'un type
app.MapGet("/api/entities/{entityType}", async (string entityType, DiffService service) =>
{
    var entities = await service.GetLatestRevisionsForTypeAsync(entityType);
    return Results.Ok(entities);
});

// Static files for ClientApp
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

