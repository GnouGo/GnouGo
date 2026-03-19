using Microsoft.EntityFrameworkCore;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Models;
using GnOuGo.KeyVault.Core.Services;

var builder = WebApplication.CreateSlimBuilder(args);

var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "gnougo-keyvault.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<KeyVaultDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<KeyVaultService>();

var app = builder.Build();

// ── Bootstrap DB ─────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
    await db.Database.EnsureCreatedAsync();
    var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
    await svc.EnsureDefaultKeyPairAsync();
}

// ── Static files (SPA) ──────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API endpoints ────────────────────────────────────────────────────

// Tenants
app.MapGet("/api/tenants", async (KeyVaultService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListTenantsAsync(ct)));

app.MapPost("/api/tenants", async (CreateTenantRequest req, KeyVaultService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateTenantAsync(req.Name, req.Author, ct)));

app.MapDelete("/api/tenants/{tenantId:guid}", async (Guid tenantId, string author, KeyVaultService svc, CancellationToken ct) =>
    await svc.DeleteTenantAsync(tenantId, author, ct) ? Results.Ok() : Results.NotFound());

// Secrets
app.MapGet("/api/secrets", async (Guid? tenantId, KeyVaultService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListSecretsAsync(tenantId, ct)));

app.MapPut("/api/secrets/{key}", async (string key, SetSecretRequest req, KeyVaultService svc, CancellationToken ct) =>
    Results.Ok(await svc.SetSecretAsync(key, req.Value, req.TenantId, req.Author, ct)));

app.MapGet("/api/secrets/{key}/value", async (string key, Guid? tenantId, string author, KeyVaultService svc, CancellationToken ct) =>
{
    var result = await svc.GetSecretAsync(key, tenantId, author, ct);
    return result is not null ? Results.Ok(result) : Results.NotFound();
});

app.MapDelete("/api/secrets/{key}", async (string key, Guid? tenantId, string author, KeyVaultService svc, CancellationToken ct) =>
    await svc.DeleteSecretAsync(key, tenantId, author, ct) ? Results.Ok() : Results.NotFound());

app.MapGet("/api/secrets/{key}/versions", async (string key, Guid? tenantId, KeyVaultService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetSecretVersionsAsync(key, tenantId, ct)));

// Audit
app.MapGet("/api/audit", async (Guid? tenantId, string? key, int? skip, int? take, KeyVaultService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetAuditLogAsync(tenantId, key, skip ?? 0, take ?? 50, ct)));

// SPA fallback
app.MapFallbackToFile("index.html");

app.Run();

