namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("redirected")]
    public Task NormalHostBudgetSupportsExactWorkbookAndCommittedRecovery(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", viaHost: true);
}
