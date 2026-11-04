using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// A transient assessment of the existing TaskPlan. No transitions, discovery,
// review, admission or journal writes belong to this technical boundary.
internal static class PlanningCompletionValidation
{
    internal sealed record Assessment(TaskCompilation Compilation,
        List<PlanningValidationResult> Coverage, List<PlanningDiagnostic> Diagnostics);

    internal static Assessment Assess(PlanningSession state, TaskPlan plan, PlanningRequirements? requirements = null)
    {
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
            PlanningJsonContext.Default.PlanningSession)!;
        probe.Plan = PlanningConstruction.Clone(plan);
        if (requirements is not null)
            probe.Requirements = JsonSerializer.Deserialize(JsonSerializer.Serialize(requirements, PlanningJsonContext.Default.PlanningRequirements),
                PlanningJsonContext.Default.PlanningRequirements)!;
        var compilation = new TaskPlanCompiler().Compile(probe.Plan, probe.Catalog!, probe.Request);
        var diagnostics = compilation.Diagnostics.ToList();
        var coverage = PlanningCommitments.Assess(probe);
        diagnostics.AddRange(PlanningClarifications.InputFindings(probe));
        diagnostics.AddRange(coverage.SelectMany(result => result.Diagnostics).Where(diagnostic => diagnostic.Required));
        if (compilation.Graph is { } graph && compilation.Diagnostics.Count == 0)
        {
            diagnostics.AddRange(PlanningClarifications.OutputFindings(probe, graph));
            var generated = PlanningGeneratedGraph.Validate(graph, probe.Catalog!, TaskPlanCompiler.UsesCompactBindings(probe.Request)).ToList();
            diagnostics.AddRange(generated.Select(compilation.Locate).Select(CompilerFinding));
            if (generated.Count == 0)
            {
                PlanningConfirmationGuards.Apply(graph, probe.Catalog!);
                diagnostics.AddRange(PlanningExecutableValidation.Validate(graph, probe.Catalog!, PlanningTokenThresholds.OptimizationAdvisory(probe.Request))
                    .Select(compilation.Locate).Select(CompilerFinding));
            }
        }
        return new(compilation, coverage, diagnostics.Distinct().OrderBy(diagnostic => diagnostic.Location, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ToList());
    }

    private static PlanningDiagnostic CompilerFinding(PlanningDiagnostic diagnostic) => diagnostic with
        { Code = "TASK_COMPILER_VALIDATION", Message = diagnostic.Code + ": " + diagnostic.Message };
}
