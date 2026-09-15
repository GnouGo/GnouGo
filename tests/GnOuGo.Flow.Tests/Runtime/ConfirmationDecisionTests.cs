using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class ConfirmationDecisionTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    [InlineData("\"analysis_says_approve\"", false)]
    [InlineData("{\"_action\":\"abandon\"}", false)]
    [InlineData("{}", false)]
    [InlineData("\"timeout\"", false)]
    [InlineData("\"cancelled\"", false)]
    public async Task OnlyConsentPublishes_AndCleanupAlwaysRuns(string response, bool expectedWrite)
    {
        var expression = ConfirmationDecisionExpression.Build("data.steps.confirm.response", "EFFECT", "NO_EFFECT");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: analysis
                    type: set
                    input: {decision: APPROVE}
                  - id: confirm
                    type: human.input
                    input: {mode: confirm, prompt: Publish?, choices: [approve, reject], allow_abandon: true, timeout_ms: 50}
                  - id: route
                    type: switch
                    expr: '${EXPRESSION}'
                    cases:
                      - value: EFFECT
                        steps:
                          - id: published
                            type: emit
                            input: {message: published}
                      - value: NO_EFFECT
                        steps: []
                    default: []
                finally:
                  - id: cleanup
                    type: emit
                    input: {message: cleanup}
            """.Replace("EXPRESSION", expression, StringComparison.Ordinal)));
        var emitted = new List<string>();
        var engine = new WorkflowEngine { HumanInputProvider = new Human(JsonNode.Parse(response)) };
        engine.Registry.Register(new RecordingEmit(emitted));
        await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(expectedWrite, emitted.Contains("published")); Assert.Contains("cleanup", emitted);
    }

    [Theory]
    [InlineData("data.steps.confirm.response === true ? 'ACT' : 'SKIP'", true)]
    [InlineData("data.steps.confirm.response ? 'ACT' : 'SKIP'", false)]
    [InlineData("data.steps.confirm.response === false ? 'ACT' : 'SKIP'", false)]
    [InlineData("data.steps.confirm.response === true ? 'SKIP' : 'ACT'", false)]
    [InlineData("getConsent() === true ? 'ACT' : 'SKIP'", false)]
    public void PermissionMappingRequiresStrictBooleanAndExactOutcomes(string expression, bool valid)
        => Assert.Equal(valid, ConfirmationDecisionExpression.TryRead(expression, "ACT", "SKIP", out _));

    [Fact]
    public void HumanContractsResolveModesAndFormTypesWithoutGuessingLabels()
    {
        var confirm = HumanInputContract.ResolveOutputSchema(HumanInputContract.ConfirmationInput("Proceed?"));
        Assert.Equal("boolean", confirm["properties"]!["response"]!["type"]!.GetValue<string>());
        var form = HumanInputContract.ResolveOutputSchema(JsonNode.Parse("""{"mode":"form","fields":[{"name":"choice","type":"radio","options":["a","b"],"required":true},{"name":"many","type":"multiselect","options":["a"]}]}""")!.AsObject());
        Assert.Equal("string", form["properties"]!["choice"]!["type"]!.GetValue<string>());
        Assert.Equal("array", form["properties"]!["many"]!["type"]!.GetValue<string>());
    }

    private sealed class RecordingEmit(List<string> messages) : IStepExecutor
    {
        public string StepType => "emit";
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            messages.Add(ctx.Engine.GetResolvedInput(ctx)!["message"]!.GetValue<string>());
            return Task.FromResult<JsonNode?>(new JsonObject());
        }
    }

    private sealed class Human(JsonNode? response) : IHumanInputProvider
    {
        public async Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            if (response is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (text == "timeout") await Task.Delay(Timeout.Infinite, ct);
                if (text == "cancelled") throw new OperationCanceledException();
            }
            return response?.DeepClone();
        }
    }
}
