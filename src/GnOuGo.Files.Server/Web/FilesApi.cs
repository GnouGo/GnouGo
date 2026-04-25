using GnOuGo.Files.Server.Options;
using GnOuGo.Files.Server.Services;
using Microsoft.Extensions.Options;
using System.Net.Mime;

namespace GnOuGo.Files.Server.Web;

public static class FilesApi
{
    public static IEndpointRouteBuilder MapFilesApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", async context =>
        {
            await WriteJsonAsync(context, new HealthStatusResponse("ok", DateTimeOffset.UtcNow), FilesJsonContext.Default.HealthStatusResponse);
        });

        endpoints.MapGet("/api/files/config", async context =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<FilesServerOptions>>().Value;
            var paths = context.RequestServices.GetRequiredService<FilesStoragePaths>();
            await WriteJsonAsync(
                context,
                new FilesConfigResponse(paths.StorageRootPath, paths.DatabasePath, options.DefaultTtlHours, options.PurgeIntervalSeconds),
                FilesJsonContext.Default.FilesConfigResponse);
        });

        endpoints.MapPost("/api/files", async context =>
        {
            var storage = context.RequestServices.GetRequiredService<FileStorageService>();

            try
            {
                var response = await storage.UploadAsync(context, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status201Created;
                await WriteJsonAsync(context, response, FilesJsonContext.Default.FileUploadResponse);
            }
            catch (ArgumentException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, new ApiErrorResponse("invalid_request", ex.Message), FilesJsonContext.Default.ApiErrorResponse);
            }
        });

        endpoints.MapGet("/api/files", async context =>
        {
            var storage = context.RequestServices.GetRequiredService<FileStorageService>();
            var response = await storage.ListAvailableAsync(context.Request, context.RequestAborted);
            await WriteJsonAsync(context, response, FilesJsonContext.Default.FileListResponse);
        });

        endpoints.MapGet("/api/files/{id}", async context =>
        {
            var id = context.Request.RouteValues["id"] as string;
            if (string.IsNullOrWhiteSpace(id))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, new ApiErrorResponse("invalid_request", "A file id is required."), FilesJsonContext.Default.ApiErrorResponse);
                return;
            }

            var storage = context.RequestServices.GetRequiredService<FileStorageService>();
            var record = await storage.GetAvailableFileAsync(id, context.RequestAborted);
            if (record is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(context, new ApiErrorResponse("not_found", "The file does not exist or its TTL has expired."), FilesJsonContext.Default.ApiErrorResponse);
                return;
            }

            context.Response.ContentType = string.IsNullOrWhiteSpace(record.ContentType)
                ? MediaTypeNames.Application.Octet
                : record.ContentType;
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{EscapeHeaderValue(record.OriginalFileName)}\"";
            context.Response.ContentLength = record.SizeBytes;

            await using var stream = new FileStream(
                record.StoredPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });

        return endpoints;
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await System.Text.Json.JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, context.RequestAborted);
    }

    private static string EscapeHeaderValue(string value)
    {
        return value.Replace("\\", "_", StringComparison.Ordinal).Replace("\"", "_", StringComparison.Ordinal);
    }
}


