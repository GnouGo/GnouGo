using GnOuGo.Agent.Server.Telemetry;

namespace GnOuGo.Agent.Server.Tests;

public sealed class EmbeddedCollectorLogCategoryFilterTests
{
    [Theory]
    [InlineData("OtlpTenantCollector")]
    [InlineData("OtlpTenantCollector.Services.TelemetryBatchWriter")]
    [InlineData("OtlpTenantCollector.Services.EfTelemetryStore")]
    [InlineData("OtlpTenantCollector.Web.OtlpHttpReceiverMarker")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.EntityFrameworkCore.Infrastructure")]
    [InlineData("Microsoft.EntityFrameworkCore.Query")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware")]
    [InlineData("Grpc.AspNetCore.Server")]
    [InlineData("Grpc.AspNetCore.Server.ServerCallHandler")]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("System.Net.Http.HttpClient.OtlpTraceExporter")]
    public void ShouldCapture_ReturnsFalse_ForSuppressedCollectorCategories(string category)
    {
        Assert.False(EmbeddedCollectorLogCategoryFilter.ShouldCapture(category));
    }

    [Theory]
    [InlineData("GnOuGo.Agent.Server.SmartFlow.SmartFlowService")]
    [InlineData("GnOuGo.Agent.Server.Telemetry.TraceDebugService")]
    [InlineData("GnOuGo.AI.Core.LLMClient")]
    [InlineData("GnOuGo.Flow.Core.Runtime.WorkflowRuntime")]
    [InlineData("GnOuGo.KeyVault.Core.Services.KeyVaultService")]
    [InlineData("Microsoft.AspNetCore.Mvc")]
    [InlineData("MyCustomLogger")]
    public void ShouldCapture_ReturnsTrue_ForApplicationCategories(string category)
    {
        Assert.True(EmbeddedCollectorLogCategoryFilter.ShouldCapture(category));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldCapture_ReturnsTrue_ForNullOrWhitespace(string? category)
    {
        Assert.True(EmbeddedCollectorLogCategoryFilter.ShouldCapture(category));
    }
}

