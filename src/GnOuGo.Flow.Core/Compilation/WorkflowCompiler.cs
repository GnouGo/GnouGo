using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Core.Compilation;

/// <summary>
/// Compiles a WorkflowDocument into a CompiledDocument ready for execution.
/// Performs validation, expression pre-parsing, and WFScript compilation.
/// </summary>
public sealed class WorkflowCompiler
{
    private readonly WorkflowValidator _validator = new();

    public CompiledDocument Compile(WorkflowDocument doc)
    {
        // 1. Validate
        var errors = _validator.Validate(doc);
        if (errors.Any(e => e.Code is ErrorCodes.ExprParse or "DSL_VERSION" or "NO_WORKFLOWS"
            or ErrorCodes.InputValidation or ErrorCodes.WorkflowCycleDetected or "INVALID_ENTRYPOINT"))
        {
            throw new WorkflowCompilationException(errors);
        }

        // 2. Compile global WFScript functions
        Dictionary<string, Func<System.Text.Json.Nodes.JsonNode?[], System.Text.Json.Nodes.JsonNode?>>? globalFunctions = null;
        if (doc.Functions != null)
        {
            var sandbox = new JintSandbox();
            globalFunctions = sandbox.LoadFunctions(doc.Functions);
        }

        // 3. Compile each workflow
        var compiled = new CompiledDocument
        {
            Source = doc,
            Entrypoint = doc.Entrypoint ?? (doc.Workflows.ContainsKey("main") ? "main" : doc.Workflows.Keys.FirstOrDefault())
        };

        foreach (var (name, wf) in doc.Workflows)
        {
            // Local WFScript functions (shadow global)
            Dictionary<string, Func<System.Text.Json.Nodes.JsonNode?[], System.Text.Json.Nodes.JsonNode?>>? localFunctions = null;
            if (wf.Functions != null)
            {
                var sandbox = new JintSandbox();
                localFunctions = sandbox.LoadFunctions(wf.Functions);
            }

            var compiledWf = new CompiledWorkflow
            {
                Name = name,
                Source = wf,
                Steps = CompileSteps(wf.Steps),
                Outputs = wf.Outputs,
                Document = compiled
            };

            compiled.Workflows[name] = compiledWf;
        }

        // Report non-fatal validation errors as warnings (but still compile)
        if (errors.Count > 0)
        {
            // Store warnings in compiled document for consumers to inspect
        }

        return compiled;
    }

    /// <summary>
    /// Validate only — returns errors without throwing.
    /// </summary>
    public List<ValidationError> Validate(WorkflowDocument doc) => _validator.Validate(doc);

    private List<CompiledStep> CompileSteps(List<StepDef> steps)
    {
        return steps.Select(CompileStep).ToList();
    }

    private CompiledStep CompileStep(StepDef step)
    {
        var compiled = new CompiledStep
        {
            Source = step,
        };

        // Compile sub-steps
        if (step.Steps != null)
            compiled.Steps = CompileSteps(step.Steps);

        // Compile branches
        if (step.Branches != null)
            compiled.Branches = step.Branches.Select(b => CompileSteps(b.Steps)).ToList();

        // Compile switch cases
        if (step.Cases != null)
            compiled.Cases = step.Cases.Select(c => new CompiledSwitchCase
            {
                Source = c,
                Steps = CompileSteps(c.Steps)
            }).ToList();

        // Compile default branch
        if (step.Default != null)
            compiled.Default = CompileSteps(step.Default);

        return compiled;
    }
}

/// <summary>
/// Exception thrown when compilation fails due to validation errors.
/// </summary>
public sealed class WorkflowCompilationException : Exception
{
    public List<ValidationError> Errors { get; }

    public WorkflowCompilationException(List<ValidationError> errors)
        : base($"Workflow compilation failed with {errors.Count} error(s):\n" +
               string.Join("\n", errors.Select(e => e.ToString())))
    {
        Errors = errors;
    }
}
