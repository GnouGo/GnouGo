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
        AppendBlock(builder, "originalPrompt", snapshot.OriginalPrompt);

        AppendScalar(builder, "createdAt", snapshot.CreatedAt);
        AppendScalar(builder, "updatedAt", snapshot.UpdatedAt);
        return builder.ToString();
    }

    private static void AppendScalar(StringBuilder builder, string name, string value)
        => builder.Append(name).Append(": ").AppendLine(Quote(value));

    private static void AppendBlock(StringBuilder builder, string name, string value)
    {
        if (!value.Contains('\n') || value.Contains('\r'))
        {
            AppendScalar(builder, name, value);
            return;
        }

        var keepFinalBreaks = value.EndsWith('\n');
        builder.Append(name).AppendLine(keepFinalBreaks ? ": |+" : ": |-");
        var content = keepFinalBreaks ? value[..^1] : value;
        foreach (var line in content.Split('\n'))
            builder.Append("  ").AppendLine(line);
    }

    private static string Quote(string value)
        => System.Text.Json.JsonSerializer.Serialize(value, AgentMcpJsonContext.Default.String);
}
