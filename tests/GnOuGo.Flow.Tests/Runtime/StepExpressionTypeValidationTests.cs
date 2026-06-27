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
  /**
   * Parses a GitHub repository URL into owner and repository names.
   *
   * @param {string} url - Repository URL.
   * @returns {object} Parsed repository parts with owner and repo fields.
   */
  function parseRepoUrl(url) {
    return { owner: "AxaFrance", repo: "oidc-client" };
  }
workflows:
  main:
    functions: |
      /**
       * Clamps a number inside an inclusive range.
       *
       * @param {number} value - Value to clamp.
       * @param {number} min - Minimum allowed value.
       * @param {number} max - Maximum allowed value.
       * @returns {number} Clamped value.
       */
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
    public void SemanticValidation_RejectsDeclaredFunctionWithoutJsDoc()
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
    inputs:
      repo_url: string
    steps:
      - id: parse
        type: set
        input:
          owner: "${functions.parseRepoUrl(data.inputs.repo_url).owner}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("FUNCTION_JSDOC_MISSING", exception.InnerException!.Message);
        Assert.Contains("parseRepoUrl", exception.InnerException.Message);
        Assert.Contains("@param", exception.InnerException.Message);
        Assert.Contains("@returns", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsDeclaredFunctionWithoutTypedParamAndReturnDocs()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Function validation test.
  tags: [test]
  inputs: {}
  outputs: {}
functions: |
  /**
   * Parses a GitHub repository URL.
   */
  function parseRepoUrl(url) {
    return { owner: "AxaFrance", repo: "oidc-client" };
  }
workflows:
  main:
    inputs:
      repo_url: string
    steps:
      - id: parse
        type: set
        input:
          owner: "${functions.parseRepoUrl(data.inputs.repo_url).owner}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("FUNCTION_JSDOC_PARAM_MISSING", exception.InnerException!.Message);
        Assert.Contains("FUNCTION_JSDOC_RETURNS_MISSING", exception.InnerException.Message);
        Assert.Contains("@param {type} url", exception.InnerException.Message);
        Assert.Contains("@returns {type}", exception.InnerException.Message);
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
    public void SemanticValidation_PropagatesLlmStructuredOutputJsonType()
    {
        var doc = Parse("""
steps:
  - id: normalize
    type: llm.call
    input:
      prompt: Normalize issues.
      structured_output:
        strict: true
        schema_inline:
          type: object
          properties:
            issue_count: { type: integer }
            issues:
              type: array
              items:
                type: object
                properties:
                  number: { type: integer }
                  title: { type: string }
                required: [number, title]
                additionalProperties: false
          required: [issue_count, issues]
          additionalProperties: false
  - id: consume
    type: llm.call
    input:
      prompt: Summarize.
      max_tokens: "${data.steps.normalize.json.issue_count}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsLlmStructuredOutputArrayAssignedToIntegerField()
    {
        var doc = Parse("""
steps:
  - id: normalize
    type: llm.call
    input:
      prompt: Normalize issues.
      structured_output:
        strict: true
        schema_inline:
          type: object
          properties:
            issues:
              type: array
              items:
                type: object
                properties:
                  number: { type: integer }
                  title: { type: string }
                required: [number, title]
                additionalProperties: false
          required: [issues]
          additionalProperties: false
  - id: consume
    type: llm.call
    input:
      prompt: Summarize.
      max_tokens: "${data.steps.normalize.json.issues}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("data.steps.normalize.json.issues", exception.InnerException.Message);
        Assert.Contains("resolves to array", exception.InnerException.Message);
        Assert.Contains("requires integer", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_PropagatesMcpStructuredOutputJsonType()
    {
        var doc = Parse("""
steps:
  - id: choose
    type: mcp.call
    input:
      server: docs
      model: gpt-4
      prompt: Choose and summarize the right document.
      structured_output:
        strict: true
        schema_inline:
          type: object
          properties:
            token_budget: { type: integer }
            title: { type: string }
          required: [token_budget, title]
          additionalProperties: false
  - id: consume
    type: llm.call
    input:
      prompt: Summarize.
      max_tokens: "${data.steps.choose.json.token_budget}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_PropagatesSetOutputSchema()
    {
        var doc = Parse("""
inputs:
  token_budget: integer
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        token_budget: { type: integer }
        title: { type: string }
      required: [token_budget, title]
      additionalProperties: false
    input:
      token_budget: "${data.inputs.token_budget}"
      title: Example
  - id: consume
    type: llm.call
    input:
      prompt: Summarize.
      max_tokens: "${data.steps.normalize.token_budget}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsNullableSetOutputAssignedToRequiredString()
    {
        var doc = Parse("""
inputs:
  owner: string
steps:
  - id: derive
    type: set
    output_schema:
      type: object
      properties:
        owner:
          anyOf:
            - type: string
            - type: "null"
      required: [owner]
      additionalProperties: false
    input:
      owner: "${data.inputs.owner}"
  - id: consume
    type: llm.call
    input:
      prompt: "${data.steps.derive.owner}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("data.steps.derive.owner", exception.InnerException.Message);
        Assert.Contains("resolves to null or string", exception.InnerException.Message);
        Assert.Contains("requires string", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_AcceptsAssertNonNullRefinedOutputAssignedToRequiredString()
    {
        var doc = Parse("""
inputs:
  owner: string
steps:
  - id: derive
    type: set
    output_schema:
      type: object
      properties:
        owner:
          anyOf:
            - type: string
            - type: "null"
      required: [owner]
      additionalProperties: false
    input:
      owner: "${data.inputs.owner}"
  - id: require_identity
    type: assert.non_null
    input:
      owner: "${data.steps.derive.owner}"
  - id: consume
    type: llm.call
    input:
      prompt: "${data.steps.require_identity.owner}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsSetOutputSchemaExpressionMismatch()
    {
        var doc = Parse("""
inputs:
  title: string
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        issue_number: { type: integer }
      required: [issue_number]
      additionalProperties: false
    input:
      issue_number: "${data.inputs.title}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("data.inputs.title", exception.InnerException.Message);
        Assert.Contains("resolves to string", exception.InnerException.Message);
        Assert.Contains("requires integer", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_AcceptsStringTernaryWithComparisonCondition()
    {
        var doc = Parse("""
inputs:
  classification: string
  issue:
    type: object
    properties:
      title: { type: string }
    required_properties: [title]
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        title: { type: string }
      required: [title]
      additionalProperties: false
    input:
      title: "${data.inputs.classification == 'bug' ? ('Fix ' + data.inputs.issue.title) : ('Implement ' + data.inputs.issue.title)}"
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsStringTernaryAssignedToBoolean()
    {
        var doc = Parse("""
inputs:
  classification: string
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        should_fix: { type: boolean }
      required: [should_fix]
      additionalProperties: false
    input:
      should_fix: "${data.inputs.classification == 'bug' ? 'Fix' : 'Implement'}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("input.should_fix", exception.InnerException.Message);
        Assert.Contains("resolves to string", exception.InnerException.Message);
        Assert.Contains("requires boolean", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsNonBooleanTernaryCondition()
    {
        var doc = Parse("""
inputs:
  classification: string
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        title: { type: string }
      required: [title]
      additionalProperties: false
    input:
      title: "${data.inputs.classification ? 'Fix' : 'Implement'}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("Ternary condition", exception.InnerException.Message);
        Assert.Contains("resolves to string", exception.InnerException.Message);
        Assert.Contains("must be boolean", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsSetInputMissingRequiredOutputSchemaField()
    {
        var doc = Parse("""
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        title: { type: string }
        url: { type: string }
      required: [title, url]
      additionalProperties: false
    input:
      title: Example
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("SET_OUTPUT_SCHEMA_MISMATCH", exception.InnerException!.Message);
        Assert.Contains("input.url", exception.InnerException.Message);
        Assert.Contains("missing required property", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsSetInputAdditionalOutputSchemaField()
    {
        var doc = Parse("""
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        title: { type: string }
      required: [title]
      additionalProperties: false
    input:
      title: Example
      unexpected: nope
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("SET_OUTPUT_SCHEMA_MISMATCH", exception.InnerException!.Message);
        Assert.Contains("input.unexpected", exception.InnerException.Message);
        Assert.Contains("property is not allowed by schema", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsUnknownPathFromSetOutputSchema()
    {
        var doc = Parse("""
steps:
  - id: normalize
    type: set
    output_schema:
      type: object
      properties:
        title: { type: string }
      required: [title]
      additionalProperties: false
    input:
      title: Example
  - id: consume
    type: template.render
    input:
      template: "Title ${data.steps.normalize.missing}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("STEP_OUTPUT_PROPERTY_UNKNOWN", exception.InnerException!.Message);
        Assert.Contains("data.steps.normalize.missing", exception.InnerException.Message);
        Assert.Contains("data.steps.normalize.title", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_TypesLoopItemFromStructuredOutput()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Loop item type validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: normalize
        type: llm.call
        input:
          prompt: Normalize issues.
          structured_output:
            strict: true
            schema_inline:
              type: object
              properties:
                issues:
                  type: array
                  items:
                    type: object
                    properties:
                      number: { type: integer }
                      title: { type: string }
                    required: [number, title]
                    additionalProperties: false
              required: [issues]
              additionalProperties: false
      - id: process
        type: loop.sequential
        input:
          items: "${data.steps.normalize.json.issues}"
        item_var: issue
        index_var: idx
        steps:
          - id: call_helper
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
              args:
                issue_number: "${data.issue.number}"
                issue_index: "${data.idx}"
  helper:
    inputs:
      issue_number: integer
      issue_index: integer
    steps:
      - id: ok
        type: set
        input: { ok: true }
""");

        InvokeSemanticValidation(doc);
    }

    [Fact]
    public void SemanticValidation_RejectsLoopItemFieldAssignedToIncompatibleInput()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Loop item type validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: process
        type: loop.sequential
        input:
          items:
            - number: 42
              title: Example issue
        item_var: issue
        steps:
          - id: call_helper
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
              args:
                issue_number: "${data.issue.title}"
  helper:
    inputs:
      issue_number: integer
    steps:
      - id: ok
        type: set
        input: { ok: true }
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("data.issue.title", exception.InnerException.Message);
        Assert.Contains("resolves to string", exception.InnerException.Message);
        Assert.Contains("requires integer", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_RejectsUnknownLoopItemProperty()
    {
        var doc = Parse("""
steps:
  - id: process
    type: loop.parallel
    input:
      items:
        - number: 42
    item_var: issue
    steps:
      - id: reshape
        type: set
        input:
          title: "${data.issue.title}"
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains("DATA_VARIABLE_PROPERTY_UNKNOWN", exception.InnerException!.Message);
        Assert.Contains("data.issue.title", exception.InnerException.Message);
        Assert.Contains("data.issue.number", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_TypesSequentialLoopIndexContext()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Loop index type validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: repeat
        type: loop.sequential
        input:
          times: 2
        steps:
          - id: call_helper
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
              args:
                label: "${data._loop.index}"
  helper:
    inputs:
      label: string
    steps:
      - id: ok
        type: set
        input: { ok: true }
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("data._loop.index", exception.InnerException.Message);
        Assert.Contains("resolves to integer", exception.InnerException.Message);
        Assert.Contains("requires string", exception.InnerException.Message);
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
    public void SemanticValidation_RejectsNullableSetOutputAssignedToRequiredWorkflowCallArg()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Local workflow nullable input validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    inputs:
      owner: string
    steps:
      - id: derive
        type: set
        output_schema:
          type: object
          properties:
            owner:
              anyOf:
                - type: string
                - type: "null"
          required: [owner]
          additionalProperties: false
        input:
          owner: "${data.inputs.owner}"
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args:
            owner: "${data.steps.derive.owner}"
  helper:
    inputs:
      owner: string
    steps:
      - id: value
        type: set
        input: { ok: true }
""");

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeSemanticValidation(doc));

        Assert.Contains(ErrorCodes.ExprTypeMismatch, exception.InnerException!.Message);
        Assert.Contains("workflow.call input.args.owner", exception.InnerException.Message);
        Assert.Contains("data.steps.derive.owner", exception.InnerException.Message);
        Assert.Contains("resolves to null or string", exception.InnerException.Message);
        Assert.Contains("requires string", exception.InnerException.Message);
    }

    [Fact]
    public void SemanticValidation_AcceptsAssertNonNullRefinedWorkflowCallArg()
    {
        var doc = WorkflowParser.Parse("""
version: 1
skill:
  description: Local workflow nullable input validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    inputs:
      owner: string
    steps:
      - id: derive
        type: set
        output_schema:
          type: object
          properties:
            owner:
              anyOf:
                - type: string
                - type: "null"
          required: [owner]
          additionalProperties: false
        input:
          owner: "${data.inputs.owner}"
      - id: require_identity
        type: assert.non_null
        input:
          owner: "${data.steps.derive.owner}"
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args:
            owner: "${data.steps.require_identity.owner}"
  helper:
    inputs:
      owner: string
    steps:
      - id: value
        type: set
        input: { ok: true }
""");

        InvokeSemanticValidation(doc);
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
