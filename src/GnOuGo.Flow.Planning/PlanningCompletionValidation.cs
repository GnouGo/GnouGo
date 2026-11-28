using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// A transient assessment of the existing TaskPlan. No transitions, discovery,
// review, admission or accepted-plan mutation belongs to this boundary.
// Normal validation explicitly opts into host observation of advisory warnings.
internal static class PlanningCompletionValidation
{
    internal sealed record Assessment(TaskCompilation Compilation,
        List<PlanningValidationResult> Coverage, List<PlanningDiagnostic> Diagnostics, IReadOnlySet<string> ReachableTaskIds);

    internal static Assessment Assess(PlanningSession state, TaskPlan plan, PlanningRequirements? requirements = null, bool publishWarnings = false)
    {
        // Eligibility probes must not write through an ambient host observer.
        // Ordinary candidate/final validation retains its journaled warnings.
        using var warnings = publishWarnings ? null : PlanningTokenThresholds.Observe((_, _, _, _) => { }, includePrevious: false);
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
            PlanningJsonContext.Default.PlanningSession)!;
        probe.Plan = PlanningConstruction.Clone(plan);
        if (requirements is not null)
            probe.Requirements = JsonSerializer.Deserialize(JsonSerializer.Serialize(requirements, PlanningJsonContext.Default.PlanningRequirements),
                PlanningJsonContext.Default.PlanningRequirements)!;
        var compilation = new TaskPlanCompiler().Compile(probe.Plan, probe.Catalog!, probe.Request);
        var diagnostics = compilation.Diagnostics.ToList();
        var bindings = new CommitmentBindings(probe.Plan);
        var coverage = PlanningCommitments.Assess(probe, bindings);
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
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ToList(), bindings.ReachableTaskIds);
    }

    private static PlanningDiagnostic CompilerFinding(PlanningDiagnostic diagnostic) => diagnostic with
        { Code = "TASK_COMPILER_VALIDATION", Message = diagnostic.Code + ": " + diagnostic.Message };
}
