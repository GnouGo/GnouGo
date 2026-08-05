using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowExecutionTenantTests
{
    [Fact]
    public void Resolve_UsesConfiguredTenant()
    {
        var tenant = WorkflowExecutionTenant.Resolve(Options.Create(new OpenTelemetrySettings
        {
            TenantId = " tenant-a "
        }));

        Assert.Equal("tenant-a", tenant);
    }

    [Fact]
    public void Resolve_UsesStableDefault_WhenNoTenantIsConfigured()
    {
        Assert.Equal(
            WorkflowExecutionTenant.DefaultTenantId,
            WorkflowExecutionTenant.Resolve(configuredTenantId: null, environmentTenantId: null));
    }
}
