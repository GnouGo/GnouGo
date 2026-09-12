using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Atomically evaluates a finite set of provider-neutral runtime decisions.
/// Conditions have already been resolved by the workflow expression engine.
/// </summary>
public sealed class DecisionEvaluateExecutor : IStepExecutor
{
    public string StepType => "decision.evaluate";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The decision contract is malformed or exceeds the switch-case execution limit."),
        new(ErrorCodes.DecisionEvaluationUnresolved, false, "A decision has overlapping matching cases or no matching case and no default.")
    };

    public string DslSnippet => """
        ### decision.evaluate — Evaluate finite runtime decisions atomically
        ```yaml
        - id: compute_decisions
          type: decision.evaluate
          input:
            decisions:
              decision_1:
                allowed_values: [VALUE_A, VALUE_B, NO_EFFECT]
                cases:
                  - when: "${data.steps.first.is_valid}"
                    value: VALUE_A
                  - when: "${data.steps.second.needs_attention}"
                    value: VALUE_B
                default: NO_EFFECT
        ```
        Each `when` expression must resolve to a boolean. Exactly one case may match.
        With no match, `default` is selected when declared. All values must be non-empty,
        unique strings. Decision and case counts use the switch-case execution limit.
        Output: `{ "decision_1": "VALUE_A" }`. Evaluation is atomic.
        """;

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw Invalid("decision.evaluate input must be an object");
        RequireOnlyFields(input, "decision.evaluate input", "decisions");

        var decisions = input["decisions"] as JsonObject
            ?? throw Invalid("decision.evaluate requires a 'decisions' object");
        if (decisions.Count == 0)
            throw Invalid("decision.evaluate requires at least one decision");
        if (decisions.Count > ctx.Limits.MaxSwitchCases)
        {
            throw Invalid(
                $"Decision count ({decisions.Count}) exceeds limit ({ctx.Limits.MaxSwitchCases})");
        }

        var selected = new List<KeyValuePair<string, string>>(decisions.Count);
        foreach (var (field, node) in decisions)
        {
            if (!IsSafeFieldName(field))
                throw Invalid($"Decision field '{field}' must be a safe identifier");

            var contract = node as JsonObject
                ?? throw Invalid($"Decision '{field}' must be an object");
            RequireOnlyFields(contract, $"Decision '{field}'", "allowed_values", "cases", "default");

            var allowedValues = ReadUniqueStrings(contract["allowed_values"], $"Decision '{field}' allowed_values");
            var allowed = allowedValues.ToHashSet(StringComparer.Ordinal);
            var cases = contract["cases"] as JsonArray
                ?? throw Invalid($"Decision '{field}' requires a 'cases' array");
            if (cases.Count == 0)
                throw Invalid($"Decision '{field}' requires at least one case");
            if (cases.Count > ctx.Limits.MaxSwitchCases)
            {
                throw Invalid(
                    $"Decision '{field}' case count ({cases.Count}) exceeds limit ({ctx.Limits.MaxSwitchCases})");
            }

            var caseValues = new HashSet<string>(StringComparer.Ordinal);
            string? matchedValue = null;
            foreach (var caseNode in cases)
            {
                var decisionCase = caseNode as JsonObject
                    ?? throw Invalid($"Decision '{field}' cases must be objects");
                RequireOnlyFields(decisionCase, $"Decision '{field}' case", "when", "value");
                if (!decisionCase.ContainsKey("when"))
                    throw Invalid($"Decision '{field}' case requires 'when'");
                if (decisionCase["when"] is not JsonValue whenValue
                    || !whenValue.TryGetValue(out bool matches))
                {
                    throw Invalid($"Decision '{field}' case 'when' must resolve to a boolean");
                }

                var value = ReadNonEmptyString(decisionCase["value"], $"Decision '{field}' case value");
                if (!allowed.Contains(value))
                    throw Invalid($"Decision '{field}' case value must be declared in allowed_values");
                if (!caseValues.Add(value))
                    throw Invalid($"Decision '{field}' case values must be unique");

                if (!matches)
                    continue;
                if (matchedValue is not null)
                {
                    throw new WorkflowRuntimeException(
                        ErrorCodes.DecisionEvaluationUnresolved,
                        $"Decision '{field}' has more than one matching case");
                }
                matchedValue = value;
            }

            if (contract.TryGetPropertyValue("default", out var defaultNode))
            {
                var defaultValue = ReadNonEmptyString(defaultNode, $"Decision '{field}' default");
                if (!allowed.Contains(defaultValue))
                    throw Invalid($"Decision '{field}' default must be declared in allowed_values");
                matchedValue ??= defaultValue;
            }

            if (matchedValue is null)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.DecisionEvaluationUnresolved,
                    $"Decision '{field}' has no matching case and no default");
            }
            selected.Add(new KeyValuePair<string, string>(field, matchedValue));
        }

        var result = new JsonObject();
        foreach (var (field, value) in selected)
            result[field] = value;
        return Task.FromResult<JsonNode?>(result);
    }

    private static IReadOnlyList<string> ReadUniqueStrings(JsonNode? node, string label)
    {
        if (node is not JsonArray values || values.Count == 0)
            throw Invalid($"{label} must be a non-empty array");

        var result = new List<string>(values.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeValue in values)
        {
            var value = ReadNonEmptyString(nodeValue, label);
            if (!unique.Add(value))
                throw Invalid($"{label} must contain unique strings");
            result.Add(value);
        }
        return result;
    }

    private static string ReadNonEmptyString(JsonNode? node, string label)
    {
        if (node is not JsonValue value
            || !value.TryGetValue(out string? text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw Invalid($"{label} must be a non-empty string");
        }
        return text;
    }

    private static void RequireOnlyFields(JsonObject value, string label, params string[] allowedFields)
    {
        var allowed = allowedFields.ToHashSet(StringComparer.Ordinal);
        var unknown = value.Select(static item => item.Key).FirstOrDefault(field => !allowed.Contains(field));
        if (unknown is not null)
            throw Invalid($"{label} contains unknown field '{unknown}'");
    }

    private static bool IsSafeFieldName(string value)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;
        return value.All(static c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static WorkflowRuntimeException Invalid(string message) =>
        new(ErrorCodes.InputValidation, message);
}
