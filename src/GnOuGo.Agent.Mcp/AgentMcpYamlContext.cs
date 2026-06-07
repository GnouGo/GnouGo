using System.Text;

namespace GnOuGo.Agent.Mcp;

internal static class AgentMcpYamlContext
{
    public static string Serialize(AgentSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendScalar(builder, "id", snapshot.Id);
        AppendScalar(builder, "name", snapshot.Name);
        AppendBlock(builder, "workflow", snapshot.Workflow);
        AppendScalar(builder, "originalPrompt", snapshot.OriginalPrompt);

        AppendScalar(builder, "createdAt", snapshot.CreatedAt);
        AppendScalar(builder, "updatedAt", snapshot.UpdatedAt);
        return builder.ToString();
    }

    private static void AppendScalar(StringBuilder builder, string name, string value)
        => builder.Append(name).Append(": ").AppendLine(Quote(value));

    private static void AppendBlock(StringBuilder builder, string name, string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\n'))
        {
            AppendScalar(builder, name, value);
            return;
        }

        builder.Append(name).AppendLine(": |-");
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            builder.Append("  ").AppendLine(line);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
