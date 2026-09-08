using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class StructuredFallbackTests
{
    [Theory]
    [InlineData("{\"json\":{\"count\":2}}", true)]
    [InlineData("{\"json\":{\"count\":\"wrong\"}}", false)]
    [InlineData("{\"json\":{}}", false)]
    [InlineData("{\"response\":{\"count\":2}}", false)]
    [InlineData("null", false)]
    public async Task ContinuationValidatesResolvedStructuredResultsBeforeDownstreamExecution(string fallback, bool valid)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: source
                    type: llm.call
                    input:
                      model: fake
                      prompt: Read the observation.
                      structured_output:
                        schema_inline:
                          type: object
                          properties:
                            count: {type: integer}
                          required: [count]
                          additionalProperties: false
                        strict: true
                    on_error:
                      cases:
                        - action: continue
                          set_output: ${data.inputs.fallback}
                  - id: after
                    type: set
                    input: {executed: true}
                finally:
                  - id: cleanup
                    type: set
                    input: {cleaned: true}
            """));
        var result = await new WorkflowEngine { LLMClient = new UnavailableClient() }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!],
            new JsonObject { ["fallback"] = JsonNode.Parse(fallback) }, TestContext.Current.CancellationToken);
        Assert.Equal(valid, result.Success);
        Assert.Equal(valid, result.StepResults.Any(s => s.StepId == "after"));
        Assert.Contains(result.StepResults, s => s.StepId == "cleanup");
        if (!valid)
        {
            Assert.Equal("STRUCTURED_FALLBACK_INVALID", result.Error?.Code);
            Assert.Null(result.StepResults[0].Output);
        }
    }

    private sealed class UnavailableClient : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => throw new WorkflowRuntimeException("UNAVAILABLE", "Injected model failure");
    }
}
