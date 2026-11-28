using System.Net;
using System.Security.Claims;
using System.Text.Json;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningDefaultPolicyTests
{
    [Theory]
    [InlineData(null, true, true, true, "diagnostic", "diagnostic")]
    [InlineData(null, true, true, true, "strict", "strict")]
    [InlineData("strict", true, true, true, "diagnostic", "strict")]
    [InlineData(null, true, false, true, "diagnostic", "strict")]
    [InlineData(null, false, true, true, "diagnostic", "strict")]
    [InlineData(null, true, true, false, "diagnostic", "strict")]
    public async Task OmittedModeUsesOnlyAuthorizedHostDefaults(string? requested, bool local, bool authenticated, bool enabled, string hostDefault, string expected)
    {
        using var services = Services(local, authenticated, enabled, hostDefault);
        var selection = await services.GetRequiredService<LocalOperatorAccess>().ResolveAsync(requested);
        Assert.Equal(expected, selection.Mode);
        Assert.Equal(expected == "diagnostic", selection.Grant is not null);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task ExplicitUnauthorizedDiagnosticSelectionIsNeverDowngraded(bool local, bool authenticated, bool enabled)
    {
        using var services = Services(local, authenticated, enabled, "diagnostic");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => services.GetRequiredService<LocalOperatorAccess>().ResolveAsync("diagnostic"));
    }

    [Fact]
    public void OmittedWireValuesRemainUnspecifiedAndExplicitStrictIsPreserved()
    {
        Assert.Null(JsonSerializer.Deserialize("""{"name":"plan","prompt":"Return 42"}""", ChatJsonContext.Default.PlanningStartDto)!.ThresholdMode);
        Assert.Null(JsonSerializer.Deserialize("""{"prompt":"Return 42"}""", ChatJsonContext.Default.ChatStreamRequestDto)!.ThresholdMode);
        Assert.Equal("strict", JsonSerializer.Deserialize("""{"name":"plan","prompt":"Return 42","thresholdMode":"strict"}""", ChatJsonContext.Default.PlanningStartDto)!.ThresholdMode);
        Assert.Throws<InvalidOperationException>(() => new TypedWorkflowPlanningSettings { DefaultThresholdMode = "unknown" }.Validate());
    }

    private static ServiceProvider Services(bool local, bool authenticated, bool enabled, string hostDefault)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "http"; context.Request.Host = new("localhost");
        context.Connection.RemoteIpAddress = local ? IPAddress.Loopback : IPAddress.Parse("192.0.2.1");
        if (authenticated) context.User = new(new ClaimsIdentity([new Claim(LocalOperatorAccess.Claim, "true")], "test"));
        return new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context })
            .AddSingleton<IOptions<TypedWorkflowPlanningSettings>>(Options.Create(new TypedWorkflowPlanningSettings { DiagnosticEnabled = enabled, DefaultThresholdMode = hostDefault }))
            .AddScoped<LocalOperatorAccess>().BuildServiceProvider();
    }
}
