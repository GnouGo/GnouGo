using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

internal static class WorkflowTelemetryInputAttributes
{
    internal const int DefaultInputAttributeLimit = 4 * 1024;

    private static readonly string[] SensitiveKeyFragments =
    [
        "api_key",
        "apikey",
        "authorization",
        "bearer",
        "credential",
        "password",
        "secret",
        "token"
    ];

    public static void Apply(ITelemetrySpan span, JsonNode? inputs)
    {
        if (inputs is not JsonObject inputObject)
        {
            span.SetAttribute("gnougo-flow.workflow.inputs.count", 0);
            span.SetAttribute("gnougo-flow.workflow.inputs", inputs is null ? "{}" : Truncate(inputs.ToJsonString()));
            return;
        }

        span.SetAttribute("gnougo-flow.workflow.inputs.count", inputObject.Count);
        span.SetAttribute("gnougo-flow.workflow.inputs.keys", string.Join(",", inputObject.Select(static kv => kv.Key)));
        var redactedInputs = RedactSensitiveValues(inputObject) ?? new JsonObject();
        span.SetAttribute("gnougo-flow.workflow.inputs", Truncate(redactedInputs.ToJsonString()));
    }

    private static JsonNode? RedactSensitiveValues(JsonNode? value)
    {
        return value switch
        {
            JsonObject obj => RedactSensitiveObject(obj),
            JsonArray array => RedactSensitiveArray(array),
            _ => value?.DeepClone()
        };
    }

    private static JsonObject RedactSensitiveObject(JsonObject source)
    {
        var redacted = new JsonObject();
        foreach (var (key, value) in source)
            redacted[key] = IsSensitiveKey(key) ? "<redacted>" : RedactSensitiveValues(value);
        return redacted;
    }

    private static JsonArray RedactSensitiveArray(JsonArray source)
    {
        var redacted = new JsonArray();
        foreach (var item in source)
            redacted.Add(RedactSensitiveValues(item));
        return redacted;
    }

    private static string Truncate(string value)
        => value.Length <= DefaultInputAttributeLimit
            ? value
            : value[..DefaultInputAttributeLimit] + "...<truncated>";

    private static bool IsSensitiveKey(string key)
        => SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
