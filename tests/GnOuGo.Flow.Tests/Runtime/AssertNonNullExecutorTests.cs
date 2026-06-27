using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class AssertNonNullExecutorTests
{
    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    [Fact]
    public async Task AssertNonNull_PassesThroughNonNullValues()
    {
        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: require_identity
        type: assert.non_null
        input:
          owner: AxaFrance
          repo: oidc-client
    outputs:
      owner: "${data.steps.require_identity.owner}"
      repo: "${data.steps.require_identity.repo}"
""");

        var result = await new WorkflowEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("AxaFrance", result.Outputs!["owner"]!.GetValue<string>());
        Assert.Equal("oidc-client", result.Outputs!["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task AssertNonNull_FailsWhenInputContainsNull()
    {
        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: require_identity
        type: assert.non_null
        input:
          owner: null
          repo: oidc-client
""");

        var result = await new WorkflowEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("$.owner is null", result.Error.Message);
    }
}
