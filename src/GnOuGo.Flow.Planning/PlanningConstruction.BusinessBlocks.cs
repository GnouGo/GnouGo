using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningConstruction
{
    private static void CompleteNewBusinessScopes(PlanningSession state, PlanningConstructionExtension extension,
        TaskPlanSymbols previous, TaskPlanSymbols symbols)
    {
        if (state.Construction?.Compact?.CurrentStep is not { } step || !PlanningCompactConstruction.SupportsBusinessFragment(step.Kind)) return;
        var added = symbols.Scopes.Where(scope => previous.Scopes.All(old => old.Path != scope.Path)).ToArray();
        if (added.Length == 0) return;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        if (extension.ContinueInScope is { } continuation)
            KeepAncestors(symbols.Scopes.SingleOrDefault(scope => scope.Path == continuation));

        // An intentionally empty branch belongs to its supplied control. Only an
        // evidenced prerequisite can leave an empty opening awaiting later work.
        var empty = added.Where(scope => !extension.CloseScopes.Contains(scope.Path, StringComparer.Ordinal) && Empty(scope) &&
            (scope.Owner is null || symbols.Scopes.Where(sibling => sibling.Owner == scope.Owner).All(Empty))).ToArray();
        if (empty.Length > 0 && PlanningCompactConstruction.HasCurrentOpeningPrerequisite(state))
            foreach (var scope in empty) KeepAncestors(scope);

        // Existing boundaries, especially the workflow root, retain their explicit
        // closure authority. Only complete newly supplied bodies close mechanically.
        extension.CloseScopes.AddRange(added.Where(scope => !retained.Contains(scope.Path) &&
                !extension.CloseScopes.Contains(scope.Path, StringComparer.Ordinal))
            .OrderByDescending(scope => scope.Depth).ThenBy(scope => scope.Path, StringComparer.Ordinal).Select(scope => scope.Path));

        void KeepAncestors(TaskPlanSymbols.Scope? scope)
        {
            for (; scope is not null; scope = scope.Parent) retained.Add(scope.Path);
        }

        bool Empty(TaskPlanSymbols.Scope scope) =>
            TaskPlanRevisions.Tasks(scope.Source).All(task => task.Kind is "sequence" or "conditional" or "foreach" or "parallel") &&
            symbols.Scopes.Where(child => child == scope || IsDescendantScope(symbols, child.Path, scope.Path))
                .All(child => child.Source.Outputs.Count == 0);
    }
}
