﻿using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace OtlpTestCli;

public static class OtelLogSender
{
    public static async Task SendAsync(string endpoint, string tenantId, string protocol = "http")
    {
        Console.WriteLine($"[DEBUG LOGS] Protocol: {protocol}, Endpoint: {endpoint}");
        
        // Déterminer le protocole OTLP et l'URL finale
        var otlpProtocol = protocol.ToLowerInvariant() == "grpc" 
            ? OpenTelemetry.Exporter.OtlpExportProtocol.Grpc 
            : OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        
        var finalEndpoint = protocol.ToLowerInvariant() == "grpc" 
            ? endpoint 
            : $"{endpoint}/v1/logs";
        
        Console.WriteLine($"[DEBUG LOGS] Final Endpoint: {finalEndpoint}");
        
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService("test-logger", serviceVersion: "1.0.0")
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["deployment.environment"] = "development",
                        ["host.name"] = Environment.MachineName,
                        ["tenant.id"] = tenantId
                    }));

                options.AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(finalEndpoint);
                    otlpOptions.Protocol = otlpProtocol;
                    otlpOptions.Headers = $"X-Tenant-Id={tenantId}";
                });

                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });
        });

        var logger = loggerFactory.CreateLogger("TestLogger");

        // Log 1: TRACE
        logger.LogTrace("Application starting - initializing configuration. Module: {Module}, ConfigFile: {ConfigFile}", 
            "startup", "appsettings.json");

        // Log 2: DEBUG
        logger.LogDebug("Database connection pool initialized with {PoolSize} connections. DbHost: {DbHost}", 
            10, "localhost:5432");

        // Log 3-5: INFO
        using (logger.BeginScope(new Dictionary<string, object> 
        { 
            ["user.id"] = "user-12345",
            ["user.email"] = "john.doe@example.com"
        }))
        {
            logger.LogInformation("User authentication successful. AuthMethod: {AuthMethod}, HttpMethod: {HttpMethod}, Url: {Url}",
                "oauth2", "POST", "/api/auth/login");

            logger.LogInformation("HTTP {HttpMethod} {Url} - {StatusCode} OK - {ResponseTime}ms. UserId: {UserId}",
                "GET", "/api/users/12345", 200, 45, "user-12345");
        }

        // Log 6-7: WARN
        logger.LogWarning("Cache miss for key '{CacheKey}' - falling back to {Fallback}. CacheProvider: {Provider}",
            "user:profile:12345", "database", "redis");

        logger.LogWarning("Database query took {Duration}ms - exceeding threshold of {Threshold}ms. QueryType: {QueryType}, Table: {Table}",
            2500, 1000, "SELECT", "users");

        // Log 8-9: ERROR
        logger.LogError("Failed to send email notification - SMTP server timeout. SmtpHost: {Host}, Port: {Port}, Recipient: {Recipient}, Error: {ErrorType}",
            "smtp.example.com", 587, "user@example.com", "SmtpException");

        logger.LogError("Request validation failed - invalid email format. Field: {Field}, Value: {Value}, HttpMethod: {Method}, Url: {Url}",
            "email", "not-an-email", "POST", "/api/users");

        // Log 10: CRITICAL (équivalent FATAL)
        logger.LogCritical("Database connection pool exhausted - unable to acquire connection after {Timeout}ms. PoolSize: {Size}, Active: {Active}, Idle: {Idle}",
            30000, 10, 10, 0);

        // Log 11: INFO - Success
        logger.LogInformation("Batch job completed successfully - processed {RecordCount} records in {Duration}ms. JobName: {JobName}, JobId: {JobId}, Status: {Status}",
            1234, 5200, "UserSyncJob", "job-789", "success");

        // Forcer l'export
        loggerFactory.Dispose();
        await Task.Delay(500);
    }
}

