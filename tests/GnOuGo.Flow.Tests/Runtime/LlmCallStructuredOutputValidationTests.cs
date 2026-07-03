using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Moq;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class LlmCallStructuredOutputValidationTests
{
    private static async Task<RunResult> RunMainAsync(string structuredOutputYaml, ILLMClient llm)
    {
        var document = WorkflowParser.Parse($$"""
        version: 1
        workflows:
          main:
            steps:
              - id: ask
                type: llm.call
                input:
                  model: test-model
                  prompt: Return structured JSON.
                  structured_output:
        {{Indent(structuredOutputYaml, 12)}}
        """);
        var compiled = new WorkflowCompiler().Compile(document);
        return await new WorkflowEngine { LLMClient = llm }
            .ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);
    }

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join('\n', value.Trim().Split('\n').Select(line => prefix + line.TrimEnd('\r')));
    }

    [Theory]
    [InlineData("""
    schema_inline: { type: object }
    schema_ref: { type: object }
    """, "mutually exclusive")]
    [InlineData("""
    strict: true
    """, "exactly one")]
    [InlineData("""
    schema_inline: { type: object }
    unknown_option: true
    """, "unknown field")]
    [InlineData("""
    schema_inline: { type: object }
    strict: yes
    """, "expected boolean")]
    [InlineData("""
    schema_ref: named-schema
    """, "schema must be a JSON Schema object")]
    public async Task InvalidEnvelope_FailsBeforeProviderCall(string structuredOutput, string expectedMessage)
    {
        var llm = new Mock<ILLMClient>();

        var result = await RunMainAsync(structuredOutput, llm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Contains(expectedMessage, result.Error.Message, StringComparison.OrdinalIgnoreCase);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("""
    schema_inline:
      type: imaginary
    """, "unknown JSON type")]
    [InlineData("""
    schema_inline:
      type: object
      properties: [bad]
    """, "properties: expected object")]
    [InlineData("""
    schema_inline:
      type: string
      pattern: "["
    """, "invalid regular expression")]
    [InlineData("""
    schema_inline:
      type: array
      minItems: -1
      items: { type: string }
    """, "expected non-negative integer")]
    [InlineData("""
    schema_inline:
      type: object
      properties:
        value: { $ref: "#/missing" }
    """, "unresolved or unsupported reference")]
    public async Task InvalidJsonSchema_FailsBeforeProviderCall(string structuredOutput, string expectedMessage)
    {
        var llm = new Mock<ILLMClient>();

        var result = await RunMainAsync(structuredOutput, llm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Contains(expectedMessage, result.Error.Message, StringComparison.OrdinalIgnoreCase);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("""
    schema_inline:
      type: string
    strict: true
    """, "root must declare type: object")]
    [InlineData("""
    schema_inline:
      type: object
      properties:
        name: { type: string }
      required: []
      additionalProperties: false
    strict: true
    """, "must include property 'name'")]
    [InlineData("""
    schema_inline:
      type: object
      properties: {}
      required: []
      additionalProperties: true
    strict: true
    """, "strict object schemas require false")]
    [InlineData("""
    schema_inline:
      type: object
      properties:
        value:
          allOf:
            - { type: string }
      required: [value]
      additionalProperties: false
    strict: true
    """, "keyword is not supported")]
    public async Task StrictProfileViolations_FailBeforeProviderCall(string structuredOutput, string expectedMessage)
    {
        var llm = new Mock<ILLMClient>();

        var result = await RunMainAsync(structuredOutput, llm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Contains(expectedMessage, result.Error.Message, StringComparison.OrdinalIgnoreCase);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReturnedJson_IsValidatedAgainstSchema()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Json = new JsonObject
                {
                    ["status"] = "wrong",
                    ["count"] = "many",
                    ["extra"] = true
                }
            });

        var result = await RunMainAsync("""
        schema_inline:
          type: object
          properties:
            status: { type: string, enum: [ok] }
            count: { type: integer, minimum: 1 }
          required: [status, count]
          additionalProperties: false
        """, llm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Contains("does not conform", result.Error.Message);
        Assert.Contains("$.status", result.Error.Message);
        Assert.Contains("$.count: expected integer", result.Error.Message);
        Assert.Contains("$.extra: property is not allowed", result.Error.Message);
        Assert.Single(llm.Invocations);
    }

    [Fact]
    public async Task ReturnedTextJson_IsValidatedThroughLocalDefinitionReference()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "{\"payload\":{\"score\":\"high\"}}" });

        var result = await RunMainAsync("""
        schema_inline:
          type: object
          properties:
            payload: { $ref: "#/$defs/payload" }
          required: [payload]
          additionalProperties: false
          $defs:
            payload:
              type: object
              properties:
                score: { type: number }
              required: [score]
              additionalProperties: false
        """, llm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Contains("$.payload.score: expected number", result.Error.Message);
    }

    [Fact]
    public async Task ValidStrictSchemaAndResponse_AreAcceptedAndNormalizedBeforeProviderCall()
    {
        LLMRequest? captured = null;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new LLMResponse { Text = "{\"name\":\"Ada\",\"age\":37}" });

        var result = await RunMainAsync("""
        schema_inline:
          type: object
          properties:
            name: { type: string, minLength: "2" }
            age: { type: integer, minimum: "0" }
          required: [name, age]
          additionalProperties: false
        strict: true
        """, llm.Object);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(captured?.StructuredOutputSchema);
        Assert.True(captured!.StructuredOutputStrict);
        Assert.Equal(2, captured.StructuredOutputSchema!["properties"]!["name"]!["minLength"]!.GetValue<int>());
        Assert.Equal(0, captured.StructuredOutputSchema["properties"]!["age"]!["minimum"]!.GetValue<int>());
        Assert.Equal("Ada", result.StepResults[0].Output!["json"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DynamicSchemaRef_MustResolveToSchemaObjectAndIsThenValidated()
    {
        LLMRequest? captured = null;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new LLMResponse { Json = new JsonObject { ["answer"] = "yes" } });

        var document = WorkflowParser.Parse("""
        version: 1
        workflows:
          main:
            steps:
              - id: schema
                type: set
                input:
                  value:
                    type: object
                    properties:
                      answer: { type: string }
                    required: [answer]
                    additionalProperties: false
              - id: ask
                type: llm.call
                input:
                  model: test-model
                  prompt: Answer.
                  structured_output:
                    schema_ref: "${data.steps.schema.value}"
        """);

        var validationErrors = new WorkflowValidator().Validate(document);
        Assert.DoesNotContain(validationErrors, error => error.Code == ErrorCodes.LlmSchema);

        var compiled = new WorkflowCompiler().Compile(document);
        var result = await new WorkflowEngine { LLMClient = llm.Object }
            .ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("object", captured!.StructuredOutputSchema!["type"]!.GetValue<string>());
    }

    [Fact]
    public void WorkflowValidator_RecursivelyReportsInvalidNestedStructuredOutput()
    {
        var document = WorkflowParser.Parse("""
        version: 1
        skill:
          description: Test
          tags: [test]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: group
                type: sequence
                steps:
                  - id: ask
                    type: llm.call
                    input:
                      model: test-model
                      prompt: Answer.
                      structured_output:
                        schema_inline:
                          type: object
                          properties:
                            answer: { type: string }
                          required: []
                          additionalProperties: false
                        strict: true
        """);

        var errors = new WorkflowValidator().Validate(document);

        var error = Assert.Single(errors, candidate => candidate.Code == ErrorCodes.LlmSchema);
        Assert.Equal("ask", error.StepId);
        Assert.Contains("must include property 'answer'", error.Message);
    }
}
