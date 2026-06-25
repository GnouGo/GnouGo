using System.Reflection;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class StepExpressionTypeValidationTests
{
    [Fact]
    public void SemanticValidation_RejectsWorkflowStringAssignedToIntegerField()
    {
        var doc = Parse("""
inputs:
  limit: string
steps:
  - id: answer
    type: llm.call
    input:
      prompt: Answer briefly.
      max_tokens: "${data.inputs.limit}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("input.max_tokens", exception.InnerException.Message);
        Assert.Contains("resolves to string", exception.InnerException.Message);
        Assert.Contains("requires integer", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_AcceptsTypedInputAndBuiltInFunctionResults()
    {
        var doc = Parse("""
inputs:
  limit: integer
  servers:
    type: array
    items: string
steps:
  - id: answer
    type: llm.call
    input:
      prompt: Answer briefly.
      max_tokens: "${data.inputs.limit}"
  - id: parallel
    type: parallel
    input:
      max_concurrency: "${len(data.inputs.servers)}"
    branches:
      - id: branch
        steps:
          - id: value
            type: set
            input: { ok: true }
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsUnknownNamespacedFunction()
    {
        var doc = Parse("""
inputs:
  repo_url: string
steps:
  - id: parse
    type: set
    input:
      owner: "${functions.parseRepoUrl(data.inputs.repo_url).owner}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("EXPRESSION_FUNCTION_UNKNOWN", exception.InnerException!.Message);
        Assert.Contains("functions.parseRepoUrl", exception.InnerException.Message);
        Assert.Contains("function parseRepoUrl", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_AcceptsDeclaredNamespacedFunctions()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Function validation test.
  tags: [test]
  inputs: {}
  outputs: {}
functions: |
  function parseRepoUrl(url) {
    return { owner: "AxaFrance", repo: "oidc-client" };
  }
workflows:
  main:
    functions: |
      function clampNumber(value, min, max) {
        return Math.max(min, Math.min(max, value));
      }
    inputs:
      repo_url: string
      limit: number
    steps:
      - id: parse
        type: set
        input:
          owner: "${functions.parseRepoUrl(data.inputs.repo_url).owner}"
          max: "${functions.clampNumber(data.inputs.limit, 1, 100)}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_PropagatesSetExpressionOutputType()
    {
        var doc = Parse("""
inputs:
  names:
    type: array
    items: string
steps:
  - id: prepare
    type: set
    input:
      items: "${data.inputs.names}"
  - id: fanout
    type: loop.parallel
    input:
      items: "${data.steps.prepare.items}"
    steps:
      - id: value
        type: set
        input:
          name: "${data.item}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_AcceptsNestedSetObjectBuiltFromPreviousStepFields()
    {
        var doc = Parse("""
steps:
  - id: source
    type: set
    input:
      issue:
        title: Example issue
        number: 42
  - id: reshape
    type: set
    input:
      issue_for_question:
        title: "${data.steps.source.issue.title}"
        number: "${data.steps.source.issue.number}"
  - id: consume
    type: set
    input:
      copied_number: "${data.steps.reshape.issue_for_question.number}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_TreatsUnknownExactSetExpressionsAsOpaque()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Unknown expression type validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: loop
        type: loop.sequential
        input:
          items:
            - issue_number: 42
        item_var: issue
        steps:
          - id: context
            type: set
            input:
              issue_number: "${data.issue.issue_number}"
          - id: call_helper
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
              args:
                issue_number: "${data.steps.context.issue_number}"
  helper:
    inputs:
      issue_number:
        type: number
        required: true
    steps:
      - id: ok
        type: set
        input: { ok: true }
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsEmbeddedInterpolationForNumericDestination()
    {
        var doc = Parse("""
inputs:
  limit: integer
steps:
  - id: answer
    type: llm.call
    input:
      prompt: Answer briefly.
      max_tokens: "prefix-${data.inputs.limit}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("resolves to string", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsNonBooleanGuard()
    {
        var doc = Parse("""
inputs:
  enabled: string
steps:
  - id: guarded
    type: set
    if: "${data.inputs.enabled}"
    input: { ok: true }
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("\"field\":\"if\"", exception.InnerException.Message);
        Assert.Contains("requires boolean", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsExpressionIncompatibleWithTypedWorkflowOutput()
    {
        var doc = Parse("""
inputs:
  name: string
steps:
  - id: value
    type: set
    input: { name: "${data.inputs.name}" }
outputs:
  count:
    type: number
    expr: "${data.steps.value.name}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("outputs.count", exception.InnerException.Message);
        Assert.Contains("requires number", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsLocalWorkflowCallArgsAgainstTargetInputs()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Local workflow call validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    inputs:
      limit_text: string
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args:
            limit: "${data.inputs.limit_text}"
            extra: true
  helper:
    inputs:
      limit: integer
      name: string
    steps:
      - id: value
        type: set
        input: { ok: true }
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("WORKFLOW_CALL_ARGS_INVALID", exception.InnerException.Message);
        Assert.Contains("input.args.name", exception.InnerException.Message);
        Assert.Contains("input.args.extra", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_PropagatesLocalWorkflowCallOutputSchema()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Local workflow output validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args:
            name: Alice
      - id: render
        type: template.render
        input:
          template: "Hello ${data.steps.call_helper.outputs.missing}"
  helper:
    inputs:
      name: string
    steps:
      - id: value
        type: set
        input:
          title: "${data.inputs.name}"
    outputs:
      title:
        type: string
        expr: "${data.steps.value.title}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("STEP_OUTPUT_PROPERTY_UNKNOWN", exception.InnerException!.Message);
        Assert.Contains("data.steps.call_helper.outputs.missing", exception.InnerException.Message);
        Assert.Contains("data.steps.call_helper.outputs.title", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_AcceptsLocalWorkflowCallArgsAndTypedOutputs()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Local workflow output validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args:
            name: Alice
      - id: render
        type: template.render
        input:
          template: "Hello ${data.steps.call_helper.outputs.title}"
  helper:
    inputs:
      name: string
    steps:
      - id: value
        type: set
        input:
          title: "${data.inputs.name}"
    outputs:
      title:
        type: string
        expr: "${data.steps.value.title}"
""");

        InvokeSemanticValidation(doc);
    }

    private static WorkflowDocument Parse(string workflowBody) => WorkflowParser.Parse($$"""
version: 1
skill:
  description: Expression type validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
{{Indent(workflowBody, 4)}}
""");

    private static void InvokeSemanticValidation(WorkflowDocument document)
    {
        var validatorType = typeof(WorkflowEngine).Assembly.GetType(
            "GnOuGo.Flow.Core.Runtime.WorkflowPlanSemanticValidator",
            throwOnError: true)!;
        var validate = validatorType.GetMethod(
            "Validate",
            BindingFlags.Public | BindingFlags.Static)!;
        validate.Invoke(null, new object?[] { document, null });
    }

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join('\n', value.Trim().Split('\n').Select(line => prefix + line));
    }
}
