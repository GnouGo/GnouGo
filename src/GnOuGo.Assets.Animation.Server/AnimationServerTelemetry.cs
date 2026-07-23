using System.Diagnostics;

namespace GnOuGo.Assets.Animation.Server;

internal static class AnimationServerTelemetry
{
    public const string ActivitySourceName = "GnOuGo.Assets.Animation.Server";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static void ApplyTenant(Activity? activity, HttpContext context)
    {
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(tenantId))
            activity?.SetTag("tenant.id", tenantId.Trim());
    }
}
