using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    private static bool IsNewHelperContractFinding(PlanningDiagnostic finding, PlanningGraph before, PlanningGraph after, IReadOnlyList<PlanningDiagnostic> previous)
    {
        if (!finding.Code.StartsWith("FUNCTION_JSDOC_", StringComparison.Ordinal)) return false;
        var split = finding.Location.LastIndexOf('/');
        if (split <= 0) return false;
        var path = finding.Location[..split]; var name = finding.Location[(split + 1)..];
        if (!previous.Any(d => d.Location.StartsWith(path + "/", StringComparison.Ordinal) && d.Code.StartsWith("FUNCTION_JSDOC_", StringComparison.Ordinal))) return false;
        string? Script(PlanningGraph graph)
        {
            if (path == "/functions") return graph.Functions;
            var parts = path.Split('/');
            return parts is ["", "workflows", var index, "functions"] && int.TryParse(index, out var wi) && wi >= 0 && wi < graph.Workflows.Count ? graph.Workflows[wi].Functions : null;
        }
        bool Declares(string? script)
        {
            if (script is null) return false;
            bool Find(Acornima.Ast.Node node) => node is Acornima.Ast.FunctionDeclaration { Id: { } id } && id.Name == name || node.ChildNodes.Any(Find);
            try { return Find(new Acornima.Parser().ParseScript(script)); }
            catch (Acornima.ParseErrorException) { return false; }
        }
        // New helpers inside an already diagnosed script may need their own contract.
        // Never excuse a lost contract on a previously existing, valid function.
        return !Declares(Script(before)) && Declares(Script(after));
    }

    private List<PlanningDiagnostic> BehaviorDiagnostics(PlanningGraph graph, PlanningPreparation preparation)
    {
        var diagnostics = PlanningExecutableValidation.Validate(graph, preparation).ToList();
        try { ValidateOwnership(graph, preparation); }
        catch (InvalidOperationException ex) { diagnostics.Add(new("BEHAVIOR_OWNERSHIP_INVALID", "/workflows", ex.Message)); }
        return diagnostics;
    }

    private static void PreserveBehavior(PlanningGraph before, PlanningGraph after, PlanningPreparation preparation, List<PlanningDiagnostic> priorDiagnostics, List<PlanningDiagnostic> diagnostics)
    {
        foreach (var workflow in before.Workflows)
        {
            var replacement = after.Workflows.FirstOrDefault(w => w.Key == workflow.Key);
            if (replacement is null || !workflow.Inputs.Select(p => (p.Name, p.Required)).SequenceEqual(replacement.Inputs.Select(p => (p.Name, p.Required))) ||
                !workflow.Outputs.Select(p => p.Name).SequenceEqual(replacement.Outputs.Select(p => p.Name)) ||
                !workflow.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(replacement.OperationIds.Order(StringComparer.Ordinal)))
            {
                diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key, "Preserve workflow identity, ownership and every input/output obligation while repairing their invalid contracts."));
                continue;
            }
            var originalNodes = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
            if (originalNodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != originalNodes.Length) continue;
            var nodes = PlanningGraphCompiler.Enumerate(replacement.Steps.Concat(replacement.Finally)).ToLookup(n => n.Key, StringComparer.Ordinal);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
                if (nodes[node.Key].Count() != 1 || (preparation.AllowedStepTypes.Contains(node.Type, StringComparer.Ordinal) && nodes[node.Key].First().Type != node.Type) || (node.CapabilityId is null || preparation.Capabilities.Any(c => c.Id == node.CapabilityId)) && nodes[node.Key].First().CapabilityId != node.CapabilityId)
                    diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key + "/" + node.Key, "Preserve existing actions and their selected capabilities; add validated shaping when needed."));
            var originalLocations = PlanningGraphValidation.Located(workflow.Steps, "/workflows/" + before.Workflows.IndexOf(workflow) + "/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, "/workflows/" + before.Workflows.IndexOf(workflow) + "/finally")).ToDictionary(n => n.Node.Key, n => n.Path, StringComparer.Ordinal);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
            {
                if (nodes[node.Key].Count() != 1) continue;
                var next = nodes[node.Key].First();
                var location = originalLocations[node.Key];
                bool Invalid(string field) => priorDiagnostics.Any(d => d.Location == "/workflows" || d.Location.StartsWith(location + "/" + field, StringComparison.Ordinal));
                bool Same(PlanningValue? left, PlanningValue? right) => JsonSerializer.Serialize(left, PlanningJsonContext.Default.PlanningValue) == JsonSerializer.Serialize(right, PlanningJsonContext.Default.PlanningValue);
                if ((!Invalid("if") && !Same(node.If, next.If)) || (!Invalid("expr") && !Same(node.Expr, next.Expr)) ||
                    node.Cases.Count != next.Cases.Count || !node.Cases.Select(c => c.Value).SequenceEqual(next.Cases.Select(c => c.Value)) ||
                    node.Branches.Count != next.Branches.Count)
                    diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", location, "Preserve validated conditions and every declared branch outcome."));
                for (var i = 0; i < Math.Min(node.Cases.Count, next.Cases.Count); i++)
                    if (!Invalid("cases/" + i + "/when") && !Same(node.Cases[i].When, next.Cases[i].When))
                        diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", location + "/cases/" + i, "Preserve the validated branch condition."));
            }
            var placements = Placements(replacement);
            if (Placements(workflow).Any(p => !placements.TryGetValue(p.Key, out var parent) || parent != p.Value))
                diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key, "Keep existing actions inside their original branches, loops and finalizers."));
            var finalizers = PlanningGraphCompiler.Enumerate(replacement.Finally).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            if (PlanningGraphCompiler.Enumerate(workflow.Finally).Any(n => !finalizers.Contains(n.Key)))
                diagnostics.Add(new("BEHAVIOR_REPAIR_REGRESSION", workflow.Key + "/finally", "Every existing finalizer must remain in finally."));
        }
    }

    private static Dictionary<string, string> Placements(PlanningWorkflow workflow)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(workflow.Steps, "steps"); Visit(workflow.Finally, "finally");
        return result;
        void Visit(List<PlanningNode> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                result[node.Key] = parent;
                Visit(node.Steps, node.Key + "/steps"); Visit(node.Default, node.Key + "/default");
                for (var i = 0; i < node.Branches.Count; i++) Visit(node.Branches[i].Steps, node.Key + "/branches/" + i);
                for (var i = 0; i < node.Cases.Count; i++) Visit(node.Cases[i].Steps, node.Key + "/cases/" + i);
            }
        }
    }

    private static Dictionary<string, PlanningNode> UnaffectedNodes(PlanningGraph graph, List<PlanningDiagnostic> diagnostics)
    {
        var result = new Dictionary<string, PlanningNode>(StringComparer.Ordinal);
        if (diagnostics.Any(d => d.Location is "$" or "/workflows")) return result;
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            var prefix = "/workflows/" + wi;
            var all = PlanningGraphValidation.Located(workflow.Steps, prefix + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, prefix + "/finally")).ToArray();
            var affected = all.Where(n => diagnostics.Any(d => d.Location.StartsWith(n.Path + "/", StringComparison.Ordinal) || n.Path.StartsWith(d.Location + "/", StringComparison.Ordinal)))
                .Select(n => n.Node.Key).ToHashSet(StringComparer.Ordinal);
            for (var oi = 0; oi < workflow.Outputs.Count; oi++)
                if (diagnostics.Any(d => d.Location.StartsWith(prefix + "/outputs/" + oi + "/", StringComparison.Ordinal)))
                    affected.UnionWith(Reads(workflow.Outputs[oi].Value));
            bool changed;
            do
            {
                changed = false;
                foreach (var (node, _) in all)
                {
                    var reads = Reads(node.Input).Concat(node.If is null ? [] : Reads(node.If)).Concat(node.Expr is null ? [] : Reads(node.Expr)).ToArray();
                    if (reads.Any(affected.Contains)) changed |= affected.Add(node.Key);
                    if (affected.Contains(node.Key)) foreach (var key in reads) changed |= affected.Add(key);
                }
            } while (changed);
            foreach (var (node, _) in all.Where(n => !affected.Contains(n.Node.Key))) result[workflow.Key + "/" + node.Key] = node;
        }
        return result;

        static IEnumerable<string> Reads(PlanningValue value)
        {
            if (value.Kind == "output" && value.Source is not null) yield return value.Source;
            foreach (var child in value.Members.Select(m => m.Value).Concat(value.Items)) foreach (var key in Reads(child)) yield return key;
        }
    }
}
