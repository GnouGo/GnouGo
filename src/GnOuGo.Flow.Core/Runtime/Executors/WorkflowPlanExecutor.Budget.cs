using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private static LLMUsageBudgetLimits? ParseLLMUsageBudget(JsonObject input)
    {
        if (input["llm_budget"] is null)
            return null;
        if (input["llm_budget"] is not JsonObject budget)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan llm_budget must be an object.");

        var unverifiable = budget["unverifiable"]?.GetValue<string>() ?? "fail";
        if (!string.Equals(unverifiable, "fail", StringComparison.Ordinal))
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan llm_budget.unverifiable must be 'fail'.");

        var limits = new LLMUsageBudgetLimits
        {
            MaxCalls = ReadPositiveInt32(budget, "max_calls"),
            MaxTotalTokens = ReadPositiveInt64(budget, "max_total_tokens"),
            MaxElapsed = ReadPositiveTimeSpan(budget, "max_elapsed_ms"),
            MaxEstimatedCostUsd = ReadPositiveDecimal(budget, "max_estimated_cost_usd")
        };

        try
        {
            limits.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan llm_budget is invalid: {ex.Message}",
                inner: ex);
        }

        return limits;
    }

    private static int? ReadPositiveInt32(JsonObject source, string property)
    {
        var value = ReadPositiveInt64(source, property);
        if (value is null)
            return null;
        if (value > int.MaxValue)
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan llm_budget.{property} must fit in a positive 32-bit integer.");
        return (int)value.Value;
    }

    private static long? ReadPositiveInt64(JsonObject source, string property)
    {
        if (source[property] is null)
            return null;
        if (source[property] is not JsonValue value
            || (!value.TryGetValue<long>(out var parsed)
                && !(value.TryGetValue<int>(out var parsedInt) && (parsed = parsedInt) >= 0))
            || parsed <= 0)
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan llm_budget.{property} must be a positive integer.");
        return parsed;
    }

    private static decimal? ReadPositiveDecimal(JsonObject source, string property)
    {
        if (source[property] is null)
            return null;
        if (source[property] is not JsonValue value)
            throw InvalidPositiveNumber(property);

        decimal parsed;
        try
        {
            if (value.TryGetValue<decimal>(out var decimalValue))
                parsed = decimalValue;
            else if (value.TryGetValue<double>(out var doubleValue))
                parsed = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
            else if (value.TryGetValue<long>(out var longValue))
                parsed = longValue;
            else if (value.TryGetValue<int>(out var intValue))
                parsed = intValue;
            else
                throw InvalidPositiveNumber(property);
        }
        catch (OverflowException ex)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan llm_budget.{property} exceeds the supported numeric range.",
                inner: ex);
        }

        if (parsed <= 0)
            throw InvalidPositiveNumber(property);
        return parsed;
    }

    private static TimeSpan? ReadPositiveTimeSpan(JsonObject source, string property)
    {
        var milliseconds = ReadPositiveInt64(source, property);
        if (milliseconds is null)
            return null;
        try
        {
            return TimeSpan.FromMilliseconds(milliseconds.Value);
        }
        catch (OverflowException ex)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan llm_budget.{property} exceeds the supported elapsed-time range.",
                inner: ex);
        }
    }

    private static WorkflowRuntimeException InvalidPositiveNumber(string property)
        => new(
            ErrorCodes.InputValidation,
            $"workflow.plan llm_budget.{property} must be a positive number.");

    private static void AttachLLMUsageBudget(StepExecutionContext ctx, JsonObject input)
    {
        var limits = ParseLLMUsageBudget(input);
        if (limits is null)
            return;

        ctx.LLMUsageBudget = ctx.LLMUsageBudget is null
            ? new LLMUsageBudgetScope(limits)
            : ctx.LLMUsageBudget.CreateChild(limits);

        ctx.SetTelemetryAttribute("gnougo-flow.llm_budget.max_calls", limits.MaxCalls);
        ctx.SetTelemetryAttribute("gnougo-flow.llm_budget.max_total_tokens", limits.MaxTotalTokens);
        ctx.SetTelemetryAttribute("gnougo-flow.llm_budget.max_elapsed_ms", limits.MaxElapsed?.TotalMilliseconds);
        ctx.SetTelemetryAttribute("gnougo-flow.llm_budget.max_estimated_cost_usd", limits.MaxEstimatedCostUsd);
        ctx.SetTelemetryAttribute("gnougo-flow.llm_budget.unverifiable", "fail");
    }
}
