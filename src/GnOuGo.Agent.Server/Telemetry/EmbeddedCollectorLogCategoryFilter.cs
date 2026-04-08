namespace GnOuGo.Agent.Server.Telemetry;

internal static class EmbeddedCollectorLogCategoryFilter
{
    private static readonly string[] SuppressedCategoryPrefixes =
    [
        "OtlpTenantCollector",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.Routing.EndpointMiddleware",
        "Grpc.AspNetCore.Server"
    ];

    public static bool ShouldCapture(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return true;

        foreach (var prefix in SuppressedCategoryPrefixes)
        {
            if (categoryName.StartsWith(prefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

