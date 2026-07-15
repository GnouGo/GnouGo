using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanDryRunValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AllowsOrdinaryBoundedRetryIterationCount()
    {
        var document = WorkflowParser.Parse("""
        version: 1
        entrypoint: main
        workflows:
          main:
            steps:
              - id: retry_loop
                type: loop.sequential
                input:
                  times: 5
                steps:
                  - id: attempt
                    type: set
                    input:
                      ok: true
        """);

        await WorkflowPlanDryRunValidator.ValidateAsync(
            document,
            mcpClientFactory: null,
            logger: null,
            CancellationToken.None);
    }
}
