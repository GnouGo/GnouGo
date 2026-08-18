using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Shared human-input DSL contract.
/// </summary>
public static class HumanInputContract
{
    public const int DefaultTimeoutMs = 10 * 60 * 60 * 1000;

    public const string ModeText = "text";
    public const string ModeChoice = "choice";
    public const string ModeForm = "form";
    public const string ModeConfirm = "confirm";
    public const string ActionProperty = "_action";
    public const string ActionSubmit = "submit";
    public const string ActionAbandon = "abandon";

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

    /// <summary>Returns whether a structured response explicitly abandons the request.</summary>
    public static bool IsAbandoned(JsonNode? response) =>
        response is JsonObject obj
        && obj[ActionProperty] is JsonValue value
        && value.TryGetValue<string>(out var action)
        && action.Equals(ActionAbandon, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the stable JSON payload transported by telemetry and HTTP hosts.
    /// Keeping this mapping in Flow.Core prevents host-specific drift.
    /// </summary>
    public static JsonObject BuildRequestPayload(HumanInputRequest request)
    {
        var payload = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["mode"] = request.Mode,
            ["run_id"] = request.RunId,
            ["step_id"] = request.StepId,
            ["timeout_ms"] = request.TimeoutMs,
            ["allow_abandon"] = request.AllowAbandon
        };

        if (request.Context != null)
            payload["context"] = request.Context.DeepClone();
        if (request.Choices != null)
            payload["choices"] = new JsonArray(request.Choices.Select(static choice => (JsonNode?)JsonValue.Create(choice)).ToArray());
        if (request.Fields != null)
        {
            payload["fields"] = new JsonArray(request.Fields.Select(static field =>
            {
                var fieldPayload = new JsonObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type,
                    ["required"] = field.Required,
                    ["allow_custom_answer"] = field.AllowCustomAnswer
                };
                if (field.Description != null)
                    fieldPayload["description"] = field.Description;
                if (field.Options != null)
                    fieldPayload["options"] = new JsonArray(field.Options.Select(static option => (JsonNode?)JsonValue.Create(option)).ToArray());
                if (field.OptionDefinitions != null)
                {
                    fieldPayload["option_definitions"] = new JsonArray(field.OptionDefinitions.Select(static option =>
                        (JsonNode)new JsonObject
                        {
                            ["value"] = option.Value,
                            ["description"] = option.Description,
                            ["recommended"] = option.Recommended
                        }).ToArray());
                }
                if (field.Default != null)
                    fieldPayload["default"] = field.Default;
                return (JsonNode)fieldPayload;
            }).ToArray());
        }

        return payload;
    }

    /// <summary>Returns whether a choice label represents confirmation.</summary>
    public static bool IsAffirmativeConfirmationChoice(string? value) =>
        value is not null
        && (value.Equals("approve", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("confirm", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ok", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns whether a choice label represents rejection.</summary>
    public static bool IsNegativeConfirmationChoice(string? value) =>
        value is not null
        && (value.Equals("reject", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("cancel", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a provider response using the stable <c>confirm</c> contract. The
    /// response may be a Boolean, a common confirmation label, or either of two
    /// configured presentation choices (first = true, second = false).
    /// </summary>
    public static bool TryReadConfirmation(
        JsonNode? response,
        IReadOnlyList<string>? choices,
        out bool confirmed)
    {
        var value = response;
        if (response is JsonObject obj)
        {
            value = obj["response"]
                    ?? obj["confirmed"]
                    ?? obj["approved"]
                    ?? obj["decision"];
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out confirmed))
                return true;

            if (jsonValue.TryGetValue<string>(out var text))
            {
                var normalized = text.Trim();
                if (bool.TryParse(normalized, out confirmed))
                    return true;
                if (normalized == "1" || IsAffirmativeConfirmationChoice(normalized))
                {
                    confirmed = true;
                    return true;
                }
                if (normalized == "0" || IsNegativeConfirmationChoice(normalized))
                {
                    confirmed = false;
                    return true;
                }

                if (choices is { Count: 2 })
                {
                    if (normalized.Equals(choices[0], StringComparison.OrdinalIgnoreCase))
                    {
                        confirmed = true;
                        return true;
                    }
                    if (normalized.Equals(choices[1], StringComparison.OrdinalIgnoreCase))
                    {
                        confirmed = false;
                        return true;
                    }
                }
            }

            if (jsonValue.TryGetValue<int>(out var intValue) && intValue is 0 or 1)
            {
                confirmed = intValue == 1;
                return true;
            }
            if (jsonValue.TryGetValue<long>(out var longValue) && longValue is 0L or 1L)
            {
                confirmed = longValue == 1L;
                return true;
            }
        }

        confirmed = false;
        return false;
    }
}

/// <summary>
/// Rich presentation metadata for one finite human-input option. The value is
/// both user-visible and the value returned by legacy and rich hosts.
/// </summary>
public sealed class HumanInputOptionDef
{
    public string Value { get; set; } = "";
    public string? Description { get; set; }
    public bool Recommended { get; set; }
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
    public List<HumanInputOptionDef>? OptionDefinitions { get; set; }
    public bool AllowCustomAnswer { get; set; }
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

    /// <summary>Whether the host should expose an explicit abandon action.</summary>
    public bool AllowAbandon { get; set; }

    /// <summary>Timeout in milliseconds (0 = no timeout).</summary>
    public int TimeoutMs { get; set; } = HumanInputContract.DefaultTimeoutMs;
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
