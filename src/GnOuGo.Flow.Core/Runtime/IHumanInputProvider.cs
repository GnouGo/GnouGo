using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Describes a single field expected from the human.
/// </summary>
public sealed class HumanInputFieldDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string"; // string, number, boolean, text, select
    public bool Required { get; set; } = true;
    public string? Description { get; set; }
    public List<string>? Options { get; set; } // for select
    public string? Default { get; set; }
}

/// <summary>
/// Request sent to the human for input.
/// </summary>
public sealed class HumanInputRequest
{
    /// <summary>Unique run identifier (scoped to the workflow execution).</summary>
    public string RunId { get; set; } = "";

    /// <summary>Step that is waiting for input.</summary>
    public string StepId { get; set; } = "";

    /// <summary>Human-readable prompt / question.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Optional structured context shown to the user (e.g. plan JSON).</summary>
    public JsonNode? Context { get; set; }

    /// <summary>Optional pre-defined choices (quick-reply buttons).</summary>
    public List<string>? Choices { get; set; }

    /// <summary>Optional structured fields for richer forms.</summary>
    public List<HumanInputFieldDef>? Fields { get; set; }

    /// <summary>Timeout in milliseconds (0 = no timeout).</summary>
    public int TimeoutMs { get; set; } = 300_000; // 5 min default
}

/// <summary>
/// Abstraction for obtaining human input during workflow execution.
/// Implementations: ServerHumanInputProvider (HTTP-based), ConsoleHumanInputProvider (stdin).
/// </summary>
public interface IHumanInputProvider
{
    /// <summary>
    /// Sends a prompt to the user and waits for a response.
    /// Returns the user response as a JsonNode (object with field values, or a simple string).
    /// </summary>
    Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct);
}

