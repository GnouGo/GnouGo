using Xunit;

namespace GnOuGo.Observability.Core.Tests;

public sealed class OpenTelemetrySettingsTests
{
    [Fact]
    public void Defaults_AreSafeForLocalDevelopment()
    {
        var settings = new OpenTelemetrySettings();

        Assert.False(settings.Enabled);
        Assert.Equal("GnOuGo", settings.ServiceName);
        Assert.Equal("1.0.0", settings.ServiceVersion);
        Assert.Equal("http://127.0.0.1:4317", settings.OtlpEndpoint);
        Assert.Equal("Grpc", settings.Protocol);
        Assert.True(settings.IncludeLogs);
        Assert.True(settings.IncludeMetrics);
        Assert.True(settings.IncludeHttpClientInstrumentation);
        Assert.False(settings.IncludeAspNetCoreTraces);
    }
}



