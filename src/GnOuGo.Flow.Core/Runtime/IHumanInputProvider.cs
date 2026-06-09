using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Shared human-input DSL contract.
/// </summary>
public static class HumanInputContract
{
    public const string ModeText = "text";
    public const string ModeChoice = "choice";
    public const string ModeForm = "form";
    public const string ModeConfirm = "confirm";

    public static readonly string[] KnownModesForDsl =
    [
        ModeText,
        ModeChoice,
        ModeForm,
        ModeConfirm,
    ];

    public static readonly string[] KnownFieldTypesForDsl =
    [
        "string",
        "text",
        "textarea",
        "markdown",
        "json",
        "yaml",
        "number",
        "integer",
        "boolean",
        "select",
        "radio",
        "multiselect",
        "checkbox",
        "password",
        "secret",
        "url",
        "email",
        "date",
        "file",
        "directory",
    ];

    public static readonly ISet<string> KnownModes = new HashSet<string>(KnownModesForDsl, StringComparer.OrdinalIgnoreCase);

    public static readonly ISet<string> KnownFieldTypes = new HashSet<string>(KnownFieldTypesForDsl, StringComparer.OrdinalIgnoreCase);

    public static bool RequiresOptions(string fieldType) =>
        fieldType.Equals("select", StringComparison.OrdinalIgnoreCase)
        || fieldType.Equals("radio", StringComparison.OrdinalIgnoreCase)
        || fieldType.Equals("multiselect", StringComparison.OrdinalIgnoreCase)
        || fieldType.Equals("checkbox", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Describes a single field expected from the human.
/// </summary>
public sealed class HumanInputFieldDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; } = true;
    public string? Description { get; set; }
    public List<string>? Options { get; set; }
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

    /// <summary>Interaction mode: text, choice, form, or confirm.</summary>
    public string Mode { get; set; } = HumanInputContract.ModeText;

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
