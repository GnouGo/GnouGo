namespace GnOuGo.Agent.Server.Configuration;

public sealed class BundledMcpSettings
{
    public const string SectionName = "BundledMcp";

    public Dictionary<string, BundledMcpServerSettings> Servers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BundledMcpServerSettings
{
    public bool Listable { get; set; }

    public Dictionary<string, BundledMcpEditableFieldSettings> EditableFields { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BundledMcpEditableFieldSettings
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public bool Sensitive { get; set; } = true;
    public string? SecretKey { get; set; }
    public string? Target { get; set; }

    public string ResolveSecretKey(string serverName, string fieldName)
        => string.IsNullOrWhiteSpace(SecretKey)
            ? BuildDefaultSecretKey(serverName, fieldName)
            : SecretKey;

    public static string BuildDefaultSecretKey(string serverName, string fieldName)
        => $"LLM--McpServerOverrides--{serverName}--{fieldName}";
}
