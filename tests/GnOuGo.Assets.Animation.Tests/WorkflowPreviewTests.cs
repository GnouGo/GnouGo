using GnOuGo.Assets.Animation.Preview;
using Xunit;

namespace GnOuGo.Assets.Animation.Tests;

public sealed class WorkflowPreviewTests
{
    [Fact]
    public void ParseAndValidate_AcceptsNestedPreviewAndWarnsAboutUnknownFields()
    {
        var result = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            name: Team demo
            entrypoint: main
            future_root: retained-as-warning
            workflows:
              main:
                steps:
                  - id: grouped
                    type: sequence
                    steps:
                      - id: think
                        type: llm.call
                      - id: fork
                        type: parallel
                        branches:
                          - name: first
                            steps:
                              - id: radio
                                type: mcp.call
                          - name: second
                            steps:
                              - id: write
                                type: future.atomic
                  - id: child
                    type: workflow.call
                    input:
                      ref:
                        kind: local
                        name: helper
              helper:
                steps:
                  - id: finish
                    type: emit
            """);

        Assert.True(result.IsValid);
        Assert.Equal("main", result.Entrypoint);
        Assert.Equal(2, result.Document.Workflows.Count);
        Assert.Contains(result.Diagnostics, item => item.Code == "UNKNOWN_FIELD" && item.Severity == WorkflowPreviewDiagnosticSeverity.Warning);
        Assert.Contains(result.FailureTargets, item => item is { WorkflowName: "main", StepId: "think", StepType: "llm.call" });
        Assert.Contains(result.FailureTargets, item => item is { WorkflowName: "main", StepId: "write", StepType: "future.atomic" });
    }

    [Theory]
    [InlineData("version: 2\nworkflows:\n  main:\n    steps:\n      - id: a\n        type: set", "PREVIEW_VERSION")]
    [InlineData("version: 1\nentrypoint: missing\nworkflows:\n  main:\n    steps:\n      - id: a\n        type: set", "INVALID_ENTRYPOINT")]
    [InlineData("version: 1\nworkflows:\n  main:\n    steps:\n      - id: duplicate\n        type: set\n      - id: duplicate\n        type: emit", "DUPLICATE_STEP_ID")]
    [InlineData("version: 1\nworkflows:\n  main:\n    steps:\n      - id: empty\n        type: parallel", "MISSING_BRANCHES")]
    public void Validate_ReportsShapeErrors(string yaml, string expectedCode)
    {
        var result = WorkflowPreviewValidator.ParseAndValidate(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Validate_ReportsMissingCallsAndLocalCallCycles()
    {
        var missing = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            workflows:
              main:
                steps:
                  - id: call
                    type: workflow.call
                    input: { ref: { kind: local, name: absent } }
            """);
        var cyclic = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: one
            workflows:
              one:
                steps:
                  - id: to-two
                    type: workflow.call
                    input: { ref: { kind: local, name: two } }
              two:
                steps:
                  - id: to-one
                    type: workflow.call
                    input: { ref: { kind: local, name: one } }
            """);

        Assert.Contains(missing.Diagnostics, item => item.Code == "WORKFLOW_NOT_FOUND");
        Assert.Contains(cyclic.Diagnostics, item => item.Code == "WORKFLOW_CYCLE");
    }

    [Fact]
    public void ParseAndValidate_ConvertsMalformedYamlToDiagnostic()
    {
        var result = WorkflowPreviewValidator.ParseAndValidate("workflows: [not: valid");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Code == "YAML_PARSE");
    }
}
