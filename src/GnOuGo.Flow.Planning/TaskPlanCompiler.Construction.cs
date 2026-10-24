using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed record TaskConstructionValidation(IReadOnlyList<PlanningDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, IReadOnlyList<TaskPlanCompiler.RepairSource>> Interfaces);

public sealed partial class TaskPlanCompiler
{
    // Construction metadata is not an executable representation. Validate every
    // concrete binding with the ordinary semantic engine; only unfinished branch
    // interfaces are deferred, and their container results remain unavailable.
    internal static TaskConstructionValidation ValidateConstruction(TaskPlan plan, PlanningCatalog catalog, PlanningRequest request,
        IReadOnlySet<string> openScopes, IReadOnlyDictionary<string, IReadOnlyList<TaskValue>>? requestedInterfaces = null,
        IReadOnlyDictionary<string, IReadOnlyList<TaskValue>>? requestedBindings = null)
    {
        var compiler = new TaskPlanCompiler
        {
            _plan = plan, _symbols = new(plan), _catalog = catalog,
            _preciseDeclarations = request.Options["planning_diagnostics_version"]?.ToString() == "1",
            _constructionUnavailable = new(StringComparer.Ordinal),
            _constructionAllowsUnboundChoices = request.Options["construction_version"]?.ToString() == "2" && openScopes.Count > 0
        };
        var findings = new List<PlanningDiagnostic>();
        var interfaces = new Dictionary<string, IReadOnlyList<RepairSource>>(StringComparer.Ordinal);
        if (compiler._symbols.InvalidIds.Count > 0)
            return new(IdentityDiagnostics(plan), interfaces);
        var scopes = compiler._symbols.Scopes;
        foreach (var path in openScopes)
        {
            var matches = scopes.Where(s => s.Path == path).ToArray();
            if (matches.Length != 1)
            {
                findings.Add(new("TASK_CONSTRUCTION_SCOPE", path, "An open construction scope must name exactly one existing semantic scope."));
                continue;
            }
            for (var current = matches[0]; current is not null; current = current.Parent)
            {
                if (!openScopes.Contains(current.Path))
                    findings.Add(new("TASK_CONSTRUCTION_SCOPE", current.Path, "A scope cannot close while a descendant construction scope remains open."));
                if (current.Owner is { } owner) compiler._constructionUnavailable.Add(owner.Id);
            }
        }
        // A reusable group's unfinished descendants also withhold its callers and
        // the enclosing containers, including calls through another group.
        bool changed;
        do
        {
            changed = false;
            foreach (var (task, scope) in compiler._symbols.Tasks.Values.Where(t => t.Task.Kind == "call"))
            {
                var group = plan.Groups.SingleOrDefault(g => g.Id == task.Group);
                if (group is null || !openScopes.Contains("/groups/" + group.Id + "/body") &&
                    !TaskPlanRevisions.Tasks(group.Body).Any(t => compiler._constructionUnavailable.Contains(t.Id))) continue;
                changed |= compiler._constructionUnavailable.Add(task.Id);
                for (var current = scope; current is not null; current = current.Parent)
                    if (current.Owner is { } owner) changed |= compiler._constructionUnavailable.Add(owner.Id);
            }
        } while (changed);

        if (requestedInterfaces is not null)
        {
            if (requestedInterfaces.Count > 8 || requestedInterfaces.Values.Sum(v => (long)v.Count) > 128)
                findings.Add(new("TASK_INTERFACE_LIMIT", "/construction/interfaces", "Request at most eight frontier scopes and 128 exact boundary references at once."));
            foreach (var path in requestedInterfaces.Keys)
                if (!scopes.Any(s => s.Path == path || s.Path + "/always" == path))
                    findings.Add(new("TASK_CONSTRUCTION_SCOPE", path, "A requested interface must identify an existing scope or its finalizer frontier."));
        }
        if (findings.Count > 0) return new(findings, interfaces);

        findings.AddRange(compiler.Preflight(inspect: (path, scope) =>
        {
            if (requestedBindings?.TryGetValue(path, out var bindings) == true)
                Capture(path, scope, bindings);
        }, inspectFrontier: (path, scope) =>
        {
            if (requestedInterfaces is null || !requestedInterfaces.TryGetValue(path, out var requested)) return;
            Capture(path, scope, requested);
        }));
        if (findings.Count == 0 && request.Options["planning_validation_profile"]?.ToString() == AssertionValidationProfile)
            findings.AddRange(compiler.AssertionContradictions());
        if (findings.Count > 0) interfaces.Clear();
        return new(findings, interfaces);

        void Capture(string path, Scope scope, IReadOnlyList<TaskValue> requested)
        {
            var visible = new List<RepairSource>();
            foreach (var value in requested)
            {
                var saved = compiler._location; compiler._location = path + "/outputs";
                try
                {
                    var bound = compiler.Value(value, scope);
                    visible.Add(new(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!
                        .Deserialize(PlanningJsonContext.Default.TaskValue)!, bound.Schema.DeepClone().AsObject()));
                }
                catch (InvalidTask) { /* Unavailable references are not frontier interfaces. */ }
                catch (UnavailableValue) { }
                finally { compiler._location = saved; }
            }
            interfaces[path] = visible;
        }
    }
}
