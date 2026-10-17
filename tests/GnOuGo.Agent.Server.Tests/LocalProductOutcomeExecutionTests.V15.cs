namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("repeated")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    [InlineData("redirected")]
    public Task V15CompositionPreservesExactLocalWorkbookAndRecovery(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15");

    [Fact]
    public Task V15ResourceBaselineUsesTheIdenticalV14BusinessComposition()
        => ExecuteTypedComposition("complete", "compact-bindings-v14");
}
