using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using System.Text.Json.Nodes;
using Xunit;

namespace GnOuGo.Flow.Tests.Compilation;

public class WorkflowValidatorTests
{
    private readonly WorkflowValidator _validator = new();

    private static WorkflowDocument ParseDoc(string yaml) => WorkflowParser.Parse(yaml);

    [Fact]
    public void Validate_ValidDocument_NoErrors()
    {
        var doc = ParseDoc("""
version: 1
skill:
  description: Test workflow.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: s1
        type: template.render
""");
        var errors = _validator.Validate(doc);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MissingSkill_ReportsError()
    {
        var doc = ParseDoc("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == ErrorCodes.SkillRequired && e.Field == "skill");
    }

    [Fact]
    public void Validate_InvalidDslVersion_ReportsError()
    {
        var doc = ParseDoc("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        doc.Version = 99;
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "DSL_VERSION");
    }

    [Fact]
    public void Validate_DuplicateStepIds_ReportsError()
    {
        var doc = ParseDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
      - id: s1
        type: template.render
");
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "DUPLICATE_STEP_ID");
    }

    [Fact]
    public void Validate_UnknownStepType_ReportsError()
    {
        var doc = ParseDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: unknown_type
");
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == ErrorCodes.StepTypeUnknown);
    }

    [Fact]
    public void Validate_InvalidLocalWorkflowRef_ReportsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: workflow.call
        input:
          ref:
            kind: local
            name: nonexistent
";
        var doc = ParseDoc(yaml);
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "INVALID_WORKFLOW_REF");
    }

    [Fact]
    public void Validate_InvalidEntrypoint_ReportsError()
    {
        var doc = ParseDoc("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        doc.Entrypoint = "nonexistent";
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "INVALID_ENTRYPOINT");
    }

    [Fact]
    public void Validate_InvalidExport_ReportsError()
    {
        var doc = ParseDoc("version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n");
        doc.Exports = new List<string> { "nonexistent" };
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "INVALID_EXPORT");
    }

    [Fact]
    public void Validate_WithRegistry_RecursivelyRejectsUnknownNestedInputField()
    {
        var doc = ParseDoc("""
version: 1
skill:
  description: Contract validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: outer
        type: sequence
        steps:
          - id: call
            type: mcp.call
            input:
              server: github
              method: search
              request: {}
              error_policy:
                unexpected: true
""");

        var errors = new WorkflowValidator(new WorkflowEngine().Registry).Validate(doc);

        Assert.Contains(errors, error =>
            error.Code == ErrorCodes.InputValidation
            && error.StepId == "call"
            && error.Field == "input.error_policy.unexpected"
            && error.Message.Contains("unknown field", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithRegistry_RejectsMutuallyExclusiveInputFields()
    {
        var doc = ParseDoc("""
version: 1
skill:
  description: Contract validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: github
          method: one
          methods: [two]
""");

        var errors = new WorkflowValidator(new WorkflowEngine().Registry).Validate(doc);

        Assert.Contains(errors, error =>
            error.StepId == "call"
            && error.Message.Contains("mutually exclusive", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultRegistry_EveryExecutorDeclaresInputAndOutputContract()
    {
        var registry = new WorkflowEngine().Registry;

        foreach (var type in registry.RegisteredTypes)
        {
            var contract = registry.Get(type)!.Contract;
            Assert.NotNull(contract);
            Assert.NotNull(contract!.InputSchema);
            Assert.NotNull(contract.OutputSchema);
        }
    }

    [Fact]
    public void Validate_RegisteredCustomExecutorWithoutContract_FailsClosed()
    {
        var registry = new StepExecutorRegistry();
        registry.Register(new ContractlessExecutor());
        var doc = ParseDoc("""
version: 1
skill:
  description: Contract validation test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: custom
        type: custom.contractless
""");

        var errors = new WorkflowValidator(registry).Validate(doc);

        Assert.Contains(errors, error =>
            error.StepId == "custom"
            && error.Message.Contains("does not declare an input/output contract", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnknownYamlStructuralField_ReportsPreciseError()
    {
        var doc = ParseDoc("""
version: 1
skill:
  description: Unknown YAML field test.
  tags: [test]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: render
        type: template.render
        inputs:
          template: Hello
""");

        var errors = _validator.Validate(doc);

        Assert.Contains(errors, error =>
            error.Code == ErrorCodes.InputValidation
            && error.Field == "workflows.main.steps[0].inputs"
            && error.Message.Contains("Unknown YAML field 'inputs'", StringComparison.Ordinal)
            && error.Message.Contains("input", StringComparison.Ordinal));
    }

    private sealed class ContractlessExecutor : IStepExecutor
    {
        public string StepType => "custom.contractless";
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct) =>
            Task.FromResult<JsonNode?>(null);
    }

    [Fact]
    public void Validate_EmptySteps_ReportsError()
    {
        var doc = new WorkflowDocument
        {
            Version = 1,
            Workflows = { ["main"] = new WorkflowDef { Steps = new() } }
        };
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "EMPTY_STEPS");
    }

    [Fact]
    public void Validate_SequenceWithoutSteps_ReportsError()
    {
        var doc = ParseDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: sequence
");
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "MISSING_STEPS");
    }

    [Fact]
    public void Validate_ParallelWithoutBranches_ReportsError()
    {
        var doc = ParseDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: parallel
");
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "MISSING_BRANCHES");
    }

    [Fact]
    public void Validate_SwitchWithoutCases_ReportsError()
    {
        var doc = ParseDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: switch
");
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == "MISSING_CASES");
    }

    [Fact]
    public void Validate_CycleDetected_ReportsError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: workflow.call
        input:
          ref:
            kind: local
            name: helper
  helper:
    steps:
      - id: s1
        type: workflow.call
        input:
          ref:
            kind: local
            name: main
";
        var doc = ParseDoc(yaml);
        var errors = _validator.Validate(doc);
        Assert.Contains(errors, e => e.Code == ErrorCodes.WorkflowCycleDetected);
    }

    [Fact]
    public void Validate_ValidLocalRef_NoError()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: workflow.call
        input:
          ref:
            kind: local
            name: helper
  helper:
    steps:
      - id: h1
        type: template.render
";
        var doc = ParseDoc(yaml);
        var errors = _validator.Validate(doc);
        Assert.DoesNotContain(errors, e => e.Code == "INVALID_WORKFLOW_REF");
    }
}
