using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class ResultAvailabilityTests
{
    [Theory]
    [InlineData("data.steps.a != null && (!data.inputs.selected || data.steps.b != null)", true)]
    [InlineData("data.steps.a != null && (data.inputs.selected || data.steps.b != null)", true)]
    [InlineData("data.steps.a != null && data.steps.missing != null", false)]
    [InlineData("data.steps.a != null && data.inputs.selected", false)]
    [InlineData("data.steps.a != null || true", false)]
    [InlineData("data.steps.a != null && false", false)]
    [InlineData("data.steps.missing.flag || data.steps.a != null", false)]
    [InlineData("data.steps.b.flag || data.steps.a != null", true)]
    [InlineData("unproven() || data.steps.a != null", false)]
    [InlineData("data.steps.a != null ?? data.steps.b != null", false)]
    public void GuardProofUsesOnlyKnownPresenceFacts(string expression, bool expected)
    {
        Assert.Equal(expected, WorkflowResultAvailability.GuardHolds(expression, new HashSet<string>(["a", "b"], StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData("data.steps.a != null", true)]
    [InlineData("data.steps.a !== null", false)]
    [InlineData("data.steps.a != null || true", false)]
    [InlineData("data.inputs.selected || data.steps.a != null", false)]
    public void EvaluationGuardsNeverWeakenTheSeparateProofOfPresence(string expression, bool expected)
        => Assert.Equal(expected, WorkflowResultAvailability.ProvesPresence(expression, "a"));
}
