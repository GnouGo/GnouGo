﻿using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Compilation;

/// <summary>
/// Validates a WorkflowDocument for correctness.
/// </summary>
public sealed class WorkflowValidator
{
    private static readonly HashSet<string> KnownStepTypes = new()
    {
        "sequence", "parallel",
        "loop.sequential", "loop.parallel",
        "switch", "set",
        "template.render",
        "llm.call",
        "workflow.call", "workflow.plan", "workflow.execute",
        "chat_history.get", "chat_history.append",
        "mcp.call", "mcp.list",
        "emit",
        "human.input"
    };

    public List<ValidationError> Validate(WorkflowDocument doc)
    {
        var errors = new List<ValidationError>();

        // DSL version
        if (doc.Dsl != 1)
            errors.Add(new ValidationError { Code = "DSL_VERSION", Message = $"Unsupported DSL version: {doc.Dsl}" });

        // Must have at least one workflow
        if (doc.Workflows.Count == 0)
            errors.Add(new ValidationError { Code = "NO_WORKFLOWS", Message = "Document must have at least one workflow" });

        // Entrypoint validation
        if (doc.Entrypoint != null && !doc.Workflows.ContainsKey(doc.Entrypoint))
            errors.Add(new ValidationError { Code = "INVALID_ENTRYPOINT", Message = $"Entrypoint '{doc.Entrypoint}' not found in workflows" });

        // Exports validation
        if (doc.Exports != null)
        {
            foreach (var exp in doc.Exports)
            {
                if (!doc.Workflows.ContainsKey(exp))
                    errors.Add(new ValidationError { Code = "INVALID_EXPORT", Message = $"Exported workflow '{exp}' not found" });
            }
        }

        // Validate each workflow
        foreach (var (name, wf) in doc.Workflows)
        {
            ValidateWorkflow(name, wf, doc, errors);
        }

        // Detect local cycles
        DetectCycles(doc, errors);

        return errors;
    }

    private void ValidateWorkflow(string name, WorkflowDef wf, WorkflowDocument doc, List<ValidationError> errors)
    {
        if (wf.Steps.Count == 0)
            errors.Add(new ValidationError { Code = "EMPTY_STEPS", WorkflowName = name, Message = "Workflow has no steps" });

        // Check unique step IDs
        var ids = new HashSet<string>();
        CollectStepIds(wf.Steps, ids, name, errors);

        // Validate each step
        foreach (var step in wf.Steps)
            ValidateStep(step, name, doc, errors);

        // Validate output expressions and type schemas
        if (wf.Outputs != null)
        {
            foreach (var (key, outputDef) in wf.Outputs)
            {
                if (!string.IsNullOrEmpty(outputDef.Expr))
                    ValidateExpression(outputDef.Expr, name, null, "outputs." + key, errors);

                ValidateOutputDef(outputDef, name, key, errors);
            }
        }

        // Validate input type schemas
        if (wf.Inputs != null)
        {
            foreach (var (inputName, def) in wf.Inputs)
                ValidateInputDef(def, name, inputName, errors);
        }
    }

    private void CollectStepIds(List<StepDef> steps, HashSet<string> ids, string wfName, List<ValidationError> errors)
    {
        foreach (var step in steps)
        {
            if (!ids.Add(step.Id))
                errors.Add(new ValidationError
                {
                    Code = "DUPLICATE_STEP_ID",
                    WorkflowName = wfName,
                    StepId = step.Id,
                    Message = $"Duplicate step ID: '{step.Id}'"
                });

            if (step.Steps != null) CollectStepIds(step.Steps, ids, wfName, errors);
            if (step.Branches != null)
                foreach (var b in step.Branches)
                    CollectStepIds(b.Steps, ids, wfName, errors);
            if (step.Cases != null)
                foreach (var c in step.Cases)
                    CollectStepIds(c.Steps, ids, wfName, errors);
            if (step.Default != null)
                CollectStepIds(step.Default, ids, wfName, errors);
        }
    }

    private void ValidateStep(StepDef step, string wfName, WorkflowDocument doc, List<ValidationError> errors)
    {
        // Known step type
        if (!KnownStepTypes.Contains(step.Type))
            errors.Add(new ValidationError
            {
                Code = ErrorCodes.StepTypeUnknown,
                WorkflowName = wfName,
                StepId = step.Id,
                Message = $"Unknown step type: '{step.Type}'"
            });

        // If guard expression
        if (step.If != null)
            ValidateExpression(step.If, wfName, step.Id, "if", errors);

        // Expr for switch
        if (step.Expr != null)
            ValidateExpression(step.Expr, wfName, step.Id, "expr", errors);

        // Type-specific validation
        switch (step.Type)
        {
            case "sequence":
                if (step.Steps == null || step.Steps.Count == 0)
                    errors.Add(new ValidationError { Code = "MISSING_STEPS", WorkflowName = wfName, StepId = step.Id, Message = "sequence requires 'steps'" });
                break;

            case "parallel":
                if (step.Branches == null || step.Branches.Count == 0)
                    errors.Add(new ValidationError { Code = "MISSING_BRANCHES", WorkflowName = wfName, StepId = step.Id, Message = "parallel requires 'branches'" });
                break;

            case "loop.sequential":
            case "loop.parallel":
                if (step.Steps == null || step.Steps.Count == 0)
                    errors.Add(new ValidationError { Code = "MISSING_STEPS", WorkflowName = wfName, StepId = step.Id, Message = $"{step.Type} requires 'steps'" });
                break;

            case "switch":
                if (step.Cases == null || step.Cases.Count == 0)
                    errors.Add(new ValidationError { Code = "MISSING_CASES", WorkflowName = wfName, StepId = step.Id, Message = "switch requires 'cases'" });
                break;

            case "workflow.call":
                ValidateWorkflowCallRef(step, wfName, doc, errors);
                break;
        }

        // Recurse
        if (step.Steps != null)
            foreach (var s in step.Steps)
                ValidateStep(s, wfName, doc, errors);
        if (step.Branches != null)
            foreach (var b in step.Branches)
                foreach (var s in b.Steps)
                    ValidateStep(s, wfName, doc, errors);
        if (step.Cases != null)
            foreach (var c in step.Cases)
            {
                if (c.When != null)
                    ValidateExpression(c.When, wfName, step.Id, "cases.when", errors);
                foreach (var s in c.Steps)
                    ValidateStep(s, wfName, doc, errors);
            }
        if (step.Default != null)
            foreach (var s in step.Default)
                ValidateStep(s, wfName, doc, errors);
    }

    private void ValidateWorkflowCallRef(StepDef step, string wfName, WorkflowDocument doc, List<ValidationError> errors)
    {
        if (step.Input == null) return;
        var refNode = step.Input["ref"];
        if (refNode == null) return;

        var kind = refNode["kind"]?.GetValue<string>();
        if (kind == "local")
        {
            var name = refNode["name"]?.GetValue<string>();
            if (name != null && !doc.Workflows.ContainsKey(name))
                errors.Add(new ValidationError
                {
                    Code = "INVALID_WORKFLOW_REF",
                    WorkflowName = wfName,
                    StepId = step.Id,
                    Message = $"Local workflow '{name}' not found"
                });
        }
    }

    private static void ValidateExpression(string expr, string wfName, string? stepId, string field, List<ValidationError> errors)
    {
        if (!StringInterpolator.HasExpressions(expr)) return;
        try
        {
            // Try parsing all expressions in the string using Jint
            var regex = new System.Text.RegularExpressions.Regex(@"\$\{([^}]+)\}");
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(expr))
            {
                var inner = match.Groups[1].Value.Trim();
                ExpressionEvaluator.Validate(inner);
            }
        }
        catch (ExpressionParseException ex)
        {
            errors.Add(new ValidationError
            {
                Code = ErrorCodes.ExprParse,
                WorkflowName = wfName,
                StepId = stepId,
                Field = field,
                Message = $"Invalid expression: {ex.Message}"
            });
        }
    }

    private static void DetectCycles(WorkflowDocument doc, List<ValidationError> errors)
    {
        // Build call graph
        var graph = new Dictionary<string, HashSet<string>>();
        foreach (var (name, wf) in doc.Workflows)
        {
            var called = new HashSet<string>();
            CollectLocalCalls(wf.Steps, called);
            graph[name] = called;
        }

        // DFS for cycles
        var visited = new HashSet<string>();
        var stack = new HashSet<string>();

        foreach (var name in graph.Keys)
        {
            if (DetectCycleDfs(name, graph, visited, stack))
            {
                errors.Add(new ValidationError
                {
                    Code = ErrorCodes.WorkflowCycleDetected,
                    Message = $"Cycle detected involving workflow '{name}'"
                });
            }
        }
    }

    private static void CollectLocalCalls(List<StepDef> steps, HashSet<string> called)
    {
        foreach (var step in steps)
        {
            if (step.Type == "workflow.call" && step.Input != null)
            {
                var refNode = step.Input["ref"];
                if (refNode?["kind"]?.GetValue<string>() == "local")
                {
                    var name = refNode["name"]?.GetValue<string>();
                    if (name != null) called.Add(name);
                }
            }
            if (step.Steps != null) CollectLocalCalls(step.Steps, called);
            if (step.Branches != null)
                foreach (var b in step.Branches)
                    CollectLocalCalls(b.Steps, called);
            if (step.Cases != null)
                foreach (var c in step.Cases)
                    CollectLocalCalls(c.Steps, called);
            if (step.Default != null) CollectLocalCalls(step.Default, called);
        }
    }

    private static bool DetectCycleDfs(string node, Dictionary<string, HashSet<string>> graph,
        HashSet<string> visited, HashSet<string> stack)
    {
        if (stack.Contains(node)) return true;
        if (visited.Contains(node)) return false;

        visited.Add(node);
        stack.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (DetectCycleDfs(neighbor, graph, visited, stack))
                    return true;
            }
        }

        stack.Remove(node);
        return false;
    }

    private static readonly HashSet<string> KnownInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "any", "string", "number", "boolean", "array", "object", "dictionary"
    };

    private void ValidateInputDef(InputDef def, string wfName, string path, List<ValidationError> errors)
    {
        // Unknown base type
        if (!KnownInputTypes.Contains(def.Type))
            errors.Add(new ValidationError
            {
                Code = "INVALID_INPUT_TYPE",
                WorkflowName = wfName,
                Message = $"Input '{path}': unknown type '{def.Type}'. Known types: {string.Join(", ", KnownInputTypes)}"
            });

        var typeLc = def.Type.ToLowerInvariant();

        // items only valid on array
        if (def.Items != null && typeLc != "array")
            errors.Add(new ValidationError
            {
                Code = "INVALID_INPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Input '{path}': 'items' is only valid when type is 'array', got '{def.Type}'"
            });

        // properties / required_properties only valid on object
        if (def.Properties != null && typeLc != "object")
            errors.Add(new ValidationError
            {
                Code = "INVALID_INPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Input '{path}': 'properties' is only valid when type is 'object', got '{def.Type}'"
            });

        if (def.RequiredProperties != null && typeLc != "object")
            errors.Add(new ValidationError
            {
                Code = "INVALID_INPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Input '{path}': 'required' is only valid when type is 'object', got '{def.Type}'"
            });

        // additional_properties valid on object or dictionary
        if (def.AdditionalProperties != null && typeLc != "object" && typeLc != "dictionary")
            errors.Add(new ValidationError
            {
                Code = "INVALID_INPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Input '{path}': 'additional_properties' is only valid when type is 'object' or 'dictionary', got '{def.Type}'"
            });

        // required_properties names must exist in properties (if declared)
        if (def.RequiredProperties != null && def.Properties != null)
        {
            foreach (var rp in def.RequiredProperties)
            {
                if (!def.Properties.ContainsKey(rp))
                    errors.Add(new ValidationError
                    {
                        Code = "INVALID_INPUT_SCHEMA",
                        WorkflowName = wfName,
                        Message = $"Input '{path}': required property '{rp}' is not declared in 'properties'"
                    });
            }
        }

        // Recurse into sub-schemas
        if (def.Items != null)
            ValidateInputDef(def.Items, wfName, $"{path}.items", errors);

        if (def.Properties != null)
        {
            foreach (var (propName, propDef) in def.Properties)
                ValidateInputDef(propDef, wfName, $"{path}.properties.{propName}", errors);
        }

        if (def.AdditionalProperties != null)
            ValidateInputDef(def.AdditionalProperties, wfName, $"{path}.additional_properties", errors);
    }

    private void ValidateOutputDef(OutputDef def, string wfName, string path, List<ValidationError> errors)
    {
        // Unknown base type
        if (!KnownInputTypes.Contains(def.Type))
            errors.Add(new ValidationError
            {
                Code = "INVALID_OUTPUT_TYPE",
                WorkflowName = wfName,
                Message = $"Output '{path}': unknown type '{def.Type}'. Known types: {string.Join(", ", KnownInputTypes)}"
            });

        var typeLc = def.Type.ToLowerInvariant();

        // items only valid on array
        if (def.Items != null && typeLc != "array")
            errors.Add(new ValidationError
            {
                Code = "INVALID_OUTPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Output '{path}': 'items' is only valid when type is 'array', got '{def.Type}'"
            });

        // properties / required_properties only valid on object
        if (def.Properties != null && typeLc != "object")
            errors.Add(new ValidationError
            {
                Code = "INVALID_OUTPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Output '{path}': 'properties' is only valid when type is 'object', got '{def.Type}'"
            });

        if (def.RequiredProperties != null && typeLc != "object")
            errors.Add(new ValidationError
            {
                Code = "INVALID_OUTPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Output '{path}': 'required' is only valid when type is 'object', got '{def.Type}'"
            });

        // additional_properties valid on object or dictionary
        if (def.AdditionalProperties != null && typeLc != "object" && typeLc != "dictionary")
            errors.Add(new ValidationError
            {
                Code = "INVALID_OUTPUT_SCHEMA",
                WorkflowName = wfName,
                Message = $"Output '{path}': 'additional_properties' is only valid when type is 'object' or 'dictionary', got '{def.Type}'"
            });

        // required_properties names must exist in properties (if declared)
        if (def.RequiredProperties != null && def.Properties != null)
        {
            foreach (var rp in def.RequiredProperties)
            {
                if (!def.Properties.ContainsKey(rp))
                    errors.Add(new ValidationError
                    {
                        Code = "INVALID_OUTPUT_SCHEMA",
                        WorkflowName = wfName,
                        Message = $"Output '{path}': required property '{rp}' is not declared in 'properties'"
                    });
            }
        }

        // Validate expressions inside nested properties (backward-compat object outputs)
        if (def.Properties != null)
        {
            foreach (var (propName, propDef) in def.Properties)
            {
                if (!string.IsNullOrEmpty(propDef.Expr))
                    ValidateExpression(propDef.Expr, wfName, null, $"outputs.{path}.{propName}", errors);
                ValidateOutputDef(propDef, wfName, $"{path}.properties.{propName}", errors);
            }
        }

        // Recurse into sub-schemas
        if (def.Items != null)
            ValidateOutputDef(def.Items, wfName, $"{path}.items", errors);

        if (def.AdditionalProperties != null)
            ValidateOutputDef(def.AdditionalProperties, wfName, $"{path}.additional_properties", errors);
    }
}

