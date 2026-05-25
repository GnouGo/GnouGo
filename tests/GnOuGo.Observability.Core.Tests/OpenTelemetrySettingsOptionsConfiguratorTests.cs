using Microsoft.Extensions.Configuration;
using Xunit;

namespace GnOuGo.Observability.Core.Tests;

public sealed class OpenTelemetrySettingsOptionsConfiguratorTests
{
    [Fact]
    public void Configure_AppliesConfiguredValuesWithoutConfigurationBinder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:ServiceName"] = "GnOuGo.Cmd.Mcp",
                ["OpenTelemetry:ServiceVersion"] = "1.2.3",
                ["OpenTelemetry:OtlpEndpoint"] = "http://localhost:4318",
                ["OpenTelemetry:Protocol"] = "HttpProtobuf",
                ["OpenTelemetry:TenantId"] = "tenant-alpha",
                ["OpenTelemetry:IncludeLogs"] = "false",
                ["OpenTelemetry:IncludeMetrics"] = "true",
                ["OpenTelemetry:IncludeHttpClientInstrumentation"] = "false",
                ["OpenTelemetry:IncludeAspNetCoreTraces"] = "true",
                ["OpenTelemetry:ActivitySources:0"] = "GnOuGo.Cmd.Mcp",
                ["OpenTelemetry:ActivitySources:1"] = "GnOuGo.Cmd.Mcp.Tools",
                ["OpenTelemetry:Meters:0"] = "GnOuGo.Cmd.Mcp"
            })
            .Build();

        var settings = new OpenTelemetrySettings();
        new OpenTelemetrySettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.True(settings.Enabled);
        Assert.Equal("GnOuGo.Cmd.Mcp", settings.ServiceName);
        Assert.Equal("1.2.3", settings.ServiceVersion);
        Assert.Equal("http://localhost:4318", settings.OtlpEndpoint);
        Assert.Equal("HttpProtobuf", settings.Protocol);
        Assert.Equal("tenant-alpha", settings.TenantId);
        Assert.False(settings.IncludeLogs);
        Assert.True(settings.IncludeMetrics);
        Assert.False(settings.IncludeHttpClientInstrumentation);
        Assert.True(settings.IncludeAspNetCoreTraces);
        Assert.Equal(["GnOuGo.Cmd.Mcp", "GnOuGo.Cmd.Mcp.Tools"], settings.ActivitySources);
        Assert.Equal(["GnOuGo.Cmd.Mcp"], settings.Meters);
    }

    [Fact]
    public void Configure_PreservesDefaultsWhenValuesAreMissingOrInvalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:Enabled"] = "not-a-bool",
                ["OpenTelemetry:IncludeLogs"] = "not-a-bool",
                ["OpenTelemetry:IncludeMetrics"] = "not-a-bool",
                ["OpenTelemetry:IncludeHttpClientInstrumentation"] = "not-a-bool",
                ["OpenTelemetry:IncludeAspNetCoreTraces"] = "not-a-bool"
            })
            .Build();

        var settings = new OpenTelemetrySettings();
        var defaultActivitySources = settings.ActivitySources.ToArray();
        var defaultMeters = settings.Meters.ToArray();

        new OpenTelemetrySettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.False(settings.Enabled);
        Assert.True(settings.IncludeLogs);
        Assert.True(settings.IncludeMetrics);
        Assert.True(settings.IncludeHttpClientInstrumentation);
        Assert.False(settings.IncludeAspNetCoreTraces);
        Assert.Equal(defaultActivitySources, settings.ActivitySources);
        Assert.Equal(defaultMeters, settings.Meters);
    }
}

