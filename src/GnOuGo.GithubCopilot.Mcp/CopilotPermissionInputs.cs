using System.Text.Json.Serialization;

namespace GnOuGo.GithubCopilot.Mcp;

/// <summary>
/// Wire-level permission choices for managed Copilot sessions. These types are
/// intentionally separate from Core result enums so tightening MCP input schemas
/// does not change existing result serialization.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CopilotManagedPermissionModeInput>))]
internal enum CopilotManagedPermissionModeInput
{
    [JsonStringEnumMemberName("interactive")]
    Interactive,

    [JsonStringEnumMemberName("auto_approve_allowlist")]
    AutoApproveAllowlist,

    [JsonStringEnumMemberName("deny")]
    Deny,

    [JsonStringEnumMemberName("approve_all")]
    ApproveAll
}

/// <summary>
/// Wire-level permission choices supported by non-interactive one-shot sessions.
/// Interactive mode is deliberately absent because it requires a managed session.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CopilotOneShotPermissionModeInput>))]
internal enum CopilotOneShotPermissionModeInput
{
    [JsonStringEnumMemberName("auto_approve_allowlist")]
    AutoApproveAllowlist,

    [JsonStringEnumMemberName("deny")]
    Deny,

    [JsonStringEnumMemberName("approve_all")]
    ApproveAll
}
