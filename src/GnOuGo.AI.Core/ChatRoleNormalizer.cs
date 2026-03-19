namespace GnOuGo.AI.Core;

/// <summary>
/// Utility for normalizing chat message roles across different AI providers.
/// </summary>
public static class ChatRoleNormalizer
{
    /// <summary>
    /// Normalizes a role string to one of the canonical values: user, assistant, system, developer.
    /// Falls back to "user" for unknown or empty roles.
    /// </summary>
    public static string Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "user";

        role = role.Trim().ToLowerInvariant();
        return role switch
        {
            "user" or "assistant" or "system" or "developer" => role,
            _ => "user"
        };
    }
}

