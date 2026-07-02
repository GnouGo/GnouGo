using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using Xunit;

namespace GnOuGo.Flow.Tests.Compilation;

public class WorkflowCompilerTests
{
    private readonly WorkflowCompiler _compiler = new();

    [Fact]
    public void Compile_ValidDocument_ReturnsCompiledDocument()
    {
        var doc = WorkflowParser.Parse("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        var compiled = _compiler.Compile(doc);
        Assert.NotNull(compiled);
        Assert.Single(compiled.Workflows);
        Assert.True(compiled.Workflows.ContainsKey("main"));
    }

    [Fact]
    public void Compile_SetsEntrypoint_ToMain()
    {
        var doc = WorkflowParser.Parse("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        var compiled = _compiler.Compile(doc);
        Assert.Equal("main", compiled.Entrypoint);
    }

    [Fact]
    public void Compile_SetsEntrypoint_ToExplicit()
    {
        var doc = WorkflowParser.Parse("version: 1\nentrypoint: myWf\nworkflows:\n  myWf:\n    steps:\n      - id: s1\n        type: template.render\n");
        var compiled = _compiler.Compile(doc);
        Assert.Equal("myWf", compiled.Entrypoint);
    }

    [Fact]
    public void Compile_SetsDocumentReference()
    {
        var doc = WorkflowParser.Parse("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        var compiled = _compiler.Compile(doc);
        Assert.NotNull(compiled.Workflows["main"].Document);
        Assert.Same(compiled, compiled.Workflows["main"].Document);
    }

    [Fact]
    public void Compile_WithSubSteps_CompilesRecursively()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: seq1
        type: sequence
        steps:
          - id: inner1
            type: template.render
";
        var compiled = _compiler.Compile(WorkflowParser.Parse(yaml));
        var seq = compiled.Workflows["main"].Steps[0];
        Assert.NotNull(seq.Steps);
        Assert.Single(seq.Steps!);
    }

    [Fact]
    public void Compile_WithBranches_CompilesAll()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: par1
        type: parallel
        branches:
          - steps:
              - id: b1
                type: template.render
          - steps:
              - id: b2
                type: template.render
";
        var compiled = _compiler.Compile(WorkflowParser.Parse(yaml));
        var par = compiled.Workflows["main"].Steps[0];
        Assert.NotNull(par.Branches);
        Assert.Equal(2, par.Branches!.Count);
    }

    [Fact]
    public void Compile_WithSwitchCases_CompilesAll()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: sw1
        type: switch
        cases:
          - when: ""${true}""
            steps:
              - id: c1
                type: template.render
        default:
          - id: d1
            type: template.render
";
        var compiled = _compiler.Compile(WorkflowParser.Parse(yaml));
        var sw = compiled.Workflows["main"].Steps[0];
        Assert.NotNull(sw.Cases);
        Assert.Single(sw.Cases!);
        Assert.NotNull(sw.Default);
    }

    [Fact]
    public void Compile_InvalidDocument_Throws()
    {
        var doc = WorkflowParser.Parse("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        doc.Version = 99; // invalid
        Assert.Throws<WorkflowCompilationException>(() => _compiler.Compile(doc));
    }

    [Fact]
    public void Compile_WithDuplicateStepIds_Throws()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: duplicate
        type: set
        input:
          value: one
      - id: branch
        type: switch
        cases:
          - when: ""${true}""
            steps:
              - id: duplicate
                type: set
                input:
                  value: two
";

        var ex = Assert.Throws<WorkflowCompilationException>(() => _compiler.Compile(WorkflowParser.Parse(yaml)));

        Assert.Contains(ex.Errors, e => e.Code == "DUPLICATE_STEP_ID" && e.StepId == "duplicate");
    }

    [Fact]
    public void Compile_WithCycle_Throws()
    {
        var yaml = @"
version: 1
workflows:
  a:
    steps:
      - id: s1
        type: workflow.call
        input:
          ref:
            kind: local
            name: b
  b:
    steps:
      - id: s1
        type: workflow.call
        input:
          ref:
            kind: local
            name: a
";
        Assert.Throws<WorkflowCompilationException>(() => _compiler.Compile(WorkflowParser.Parse(yaml)));
    }

    [Fact]
    public void Validate_ReturnsErrorsWithoutThrowing()
    {
        var doc = WorkflowParser.Parse("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: unknown\n");
        var errors = _compiler.Validate(doc);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Compile_PreservesOutputs()
    {
        var yaml = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n    outputs:\n      result: \"${data.steps.s1}\"\n";
        var compiled = _compiler.Compile(WorkflowParser.Parse(yaml));
        Assert.NotNull(compiled.Workflows["main"].Outputs);
        Assert.Equal("${data.steps.s1}", compiled.Workflows["main"].Outputs!["result"].Expr);
    }

    [Fact]
    public void Validate_ItemsOnNonArray_ReturnsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      bad:
        type: string
        items:
          type: number
    steps:
      - id: s1
        type: template.render
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.Contains(errors, e => e.Code == "INVALID_INPUT_SCHEMA" && e.Message!.Contains("items"));
    }

    [Fact]
    public void Validate_PropertiesOnNonObject_ReturnsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      bad:
        type: array
        properties:
          name: { type: string }
    steps:
      - id: s1
        type: template.render
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.Contains(errors, e => e.Code == "INVALID_INPUT_SCHEMA" && e.Message!.Contains("properties"));
    }

    [Fact]
    public void Validate_AdditionalPropertiesOnString_ReturnsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      bad:
        type: string
        additional_properties:
          type: number
    steps:
      - id: s1
        type: template.render
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.Contains(errors, e => e.Code == "INVALID_INPUT_SCHEMA" && e.Message!.Contains("additional_properties"));
    }

    [Fact]
    public void Validate_ValidRichTypes_NoSchemaErrors()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      tags:
        type: array
        items: { type: string }
      config:
        type: object
        properties:
          host: { type: string }
          port: { type: number }
        required_properties: [host]
      scores:
        type: dictionary
        additional_properties: { type: number }
    steps:
      - id: s1
        type: template.render
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.DoesNotContain(errors, e => e.Code == "INVALID_INPUT_SCHEMA" || e.Code == "INVALID_INPUT_TYPE");
    }

    [Fact]
    public void Validate_RequiredPropertyNotInProperties_ReturnsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      config:
        type: object
        properties:
          host: { type: string }
        required_properties: [host, missing_prop]
    steps:
      - id: s1
        type: template.render
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.Contains(errors, e => e.Code == "INVALID_INPUT_SCHEMA" && e.Message!.Contains("missing_prop"));
    }

    [Fact]
    public void Validate_TypedOutputs_ValidSchema_NoErrors()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
    outputs:
      result:
        expr: ""${data.steps.s1.text}""
        type: string
        description: The result
      items:
        expr: ""${data.steps.s1.items}""
        type: array
        items: { type: string }
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.DoesNotContain(errors, e => e.Code == "INVALID_OUTPUT_TYPE" || e.Code == "INVALID_OUTPUT_SCHEMA");
    }

    [Fact]
    public void Validate_TypedOutputs_UnknownType_ReturnsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
    outputs:
      result:
        expr: ""${data.steps.s1}""
        type: foobar
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.Contains(errors, e => e.Code == "INVALID_OUTPUT_TYPE" && e.Message!.Contains("foobar"));
    }

    [Fact]
    public void Validate_TypedOutputs_ItemsOnNonArray_ReturnsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
    outputs:
      result:
        expr: ""${data.steps.s1}""
        type: string
        items: { type: number }
";
        var errors = _compiler.Validate(WorkflowParser.Parse(yaml));
        Assert.Contains(errors, e => e.Code == "INVALID_OUTPUT_SCHEMA" && e.Message!.Contains("items"));
    }

    [Fact]
    public void Compile_PreservesTypedOutputs()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
    outputs:
      result:
        expr: ""${data.steps.s1.text}""
        type: string
        description: The result text
";
        var compiled = _compiler.Compile(WorkflowParser.Parse(yaml));
        var outputs = compiled.Workflows["main"].Outputs;
        Assert.NotNull(outputs);
        Assert.Equal("${data.steps.s1.text}", outputs!["result"].Expr);
        Assert.Equal("string", outputs["result"].Type);
        Assert.Equal("The result text", outputs["result"].Description);
    }
}
