namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Documentation requirements shared by construction and complete workflow validation.</summary>
public static class GeneratedFunctionDocumentation
{
    public sealed record Finding(string Code, string Function, string Message);

    public static IReadOnlyList<Finding> Validate(string? script) => WorkflowPlanSemanticValidator.ValidateFunctionDocumentation(script)
        .Select(error => new Finding(error.Code, error.Field["functions.".Length..], error.Message)).ToArray();
}
