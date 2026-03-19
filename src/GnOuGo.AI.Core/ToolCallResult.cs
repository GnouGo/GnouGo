using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// Represents a tool call parsed from an LLM response.
/// </summary>
public sealed class ToolCallResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonNode? Arguments { get; set; }
}

