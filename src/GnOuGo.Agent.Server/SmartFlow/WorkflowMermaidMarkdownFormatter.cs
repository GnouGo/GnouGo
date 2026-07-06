using System.Text;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Mermaid;

namespace GnOuGo.Agent.Server.SmartFlow;

internal static class WorkflowMermaidMarkdownFormatter
{
    public static string AppendDiagrams(
        string markdown,
        string? workflowYaml,
        ILogger logger,
        WorkflowMermaidMarkdownOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(workflowYaml))
            return markdown;

        try
        {
            options ??= new WorkflowMermaidMarkdownOptions();
            var result = MermaidWorkflowRenderer.Render(workflowYaml, new MermaidRenderOptions
            {
                SubWorkflowMode = options.IncludeLocalWorkflowDetails
                    ? MermaidSubWorkflowMode.ReferencedLocalOnly
                    : MermaidSubWorkflowMode.None
            });

            var builder = new StringBuilder(markdown.TrimEnd());
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
            builder.AppendLine("## Workflow diagrams");
            builder.AppendLine();

            AppendDiagram(builder, $"Main workflow: `{result.Main.WorkflowName}`", result.Main.Content);

            if (options.IncludeLocalWorkflowDetails)
            {
                foreach (var diagram in result.SubWorkflows)
                    AppendDiagram(builder, $"Local workflow: `{diagram.WorkflowName}`", diagram.Content);
            }

            if (result.MissingLocalWorkflowReferences.Count > 0)
            {
                builder.AppendLine("Some local workflow references could not be rendered because they are missing from the generated YAML: "
                    + string.Join(", ", result.MissingLocalWorkflowReferences.Select(static name => $"`{name}`")) + ".");
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not render Mermaid diagrams for generated workflow YAML.");
            return markdown;
        }
    }

    public static SmartFlowEvent EnhanceGeneratedWorkflowEvent(
        SmartFlowEvent evt,
        ILogger logger,
        WorkflowMermaidMarkdownOptions? options = null)
    {
        if (!IsGeneratedWorkflowMessage(evt.Text))
            return evt;

        if (!TryExtractYamlFence(evt.Text!, out var yaml))
            return evt;

        var enhanced = AppendDiagrams(evt.Text!, yaml, logger, options);
        return evt with { Type = "thinking:response", Text = enhanced };
    }

    private static bool IsGeneratedWorkflowMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("Generated workflow", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Proposed improved workflow", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendDiagram(StringBuilder builder, string title, string mermaid)
    {
        builder.Append("### ").AppendLine(title);
        builder.AppendLine();
        builder.AppendLine("```mermaid");
        builder.AppendLine(mermaid.TrimEnd());
        builder.AppendLine("```");
        builder.AppendLine();
    }

    private static bool TryExtractYamlFence(string markdown, out string yaml)
    {
        yaml = "";
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var fence = lines[i].Trim();
            if (!fence.StartsWith("```", StringComparison.Ordinal))
                continue;

            var language = fence[3..].Trim();
            if (!string.Equals(language, "yaml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(language, "yml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = i + 1;
            for (var j = start; j < lines.Length; j++)
            {
                if (!lines[j].Trim().StartsWith("```", StringComparison.Ordinal))
                    continue;

                yaml = string.Join('\n', lines[start..j]).Trim();
                return !string.IsNullOrWhiteSpace(yaml);
            }

            return false;
        }

        return false;
    }
}

public sealed class WorkflowMermaidMarkdownOptions
{
    public const string SectionName = "WorkflowMermaid";

    public bool IncludeLocalWorkflowDetails { get; set; }
}
