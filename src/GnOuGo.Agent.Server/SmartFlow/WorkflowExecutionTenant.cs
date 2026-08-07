using GnOuGo.Agent.Server.Configuration;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Resolves the host-owned tenant identity used for workflow execution metadata.
/// Tenant identity is deliberately not accepted from generated workflow inputs.
/// </summary>
internal static class WorkflowExecutionTenant
{
    internal const string DefaultTenantId = "default";

    internal static string Resolve(IOptions<OpenTelemetrySettings>? settings)
        => Resolve(settings?.Value.TenantId, Environment.GetEnvironmentVariable("GNouGo__TenantId"));

    internal static string Resolve(string? configuredTenantId, string? environmentTenantId)
    {
        if (!string.IsNullOrWhiteSpace(configuredTenantId))
            return configuredTenantId.Trim();

        return string.IsNullOrWhiteSpace(environmentTenantId) ? DefaultTenantId : environmentTenantId.Trim();
    }
}
