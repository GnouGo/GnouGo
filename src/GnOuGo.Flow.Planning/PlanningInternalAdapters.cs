using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Predefined pure result adapters. They have no capability or operation ownership.</summary>
internal static class PlanningInternalAdapters
{
    internal static void Insert(PlanningWorkflow workflow, PlanningBehaviorWorkflow behavior)
    {
        var identities = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        Visit(workflow.Steps); Visit(workflow.Finally);
        void Visit(List<PlanningNode> nodes)
        {
            foreach (var node in nodes.ToArray())
            {
                Visit(node.Steps); Visit(node.Default);
                foreach (var branch in node.Branches) Visit(branch.Steps);
                foreach (var outcome in node.Cases) Visit(outcome.Steps);
                if (node.Type != "switch") continue;
                var accepted = PlanningBehaviorPlans.Enumerate(behavior.Steps.Concat(behavior.Finally)).Single(n => n.Key == node.Key);
                var outcomes = new List<(string Outcome, string Adapter)>();
                foreach (var outcome in node.Cases) AddMarker(outcome.Value!, outcome.Steps);
                AddMarker(accepted.Outcomes.Single(o => o.IsDefault).Key, node.Default);
                var selector = new PlanningNode
                {
                    Key = Identity(node, "$selection", "branch_result"), Type = "set", InternalRole = "branch_result", Purpose = "Selected decision outcome",
                    Input = new() { Kind = "object", Members = [new("outcome", new() { Kind = "compute", Members = [new("selected", new() { Kind = "output", Source = node.Key })],
                        Text = string.Join(" ?? ", outcomes.Select(o => "selected[" + JsonSerializer.Serialize(o.Adapter, PlanningJsonContext.Default.String) + "]?.outcome")) })] },
                    OutputSchema = new() { Type = "object", Properties = [new() { Name = "outcome", Schema = new() { Type = "string", Enum = outcomes.Select(o => o.Outcome).ToList() } }] }
                };
                nodes.Insert(nodes.IndexOf(node) + 1, selector);
                void AddMarker(string outcome, List<PlanningNode> target)
                {
                    var key = Identity(node, outcome, "decision_outcome");
                    target.Add(new()
                    {
                        Key = key, Type = "set", InternalRole = "decision_outcome", Purpose = "Internal decision outcome",
                        Input = new() { Kind = "object", Members = [new("outcome", new() { Kind = "string", Text = outcome })] },
                        OutputSchema = new() { Type = "object", Properties = [new() { Name = "outcome", Schema = new() { Type = "string", Enum = [outcome] } }] }
                    });
                    outcomes.Add((outcome, key));
                }
            }
        }
        string Identity(PlanningNode owner, string outcome, string role)
        {
            var key = "adapter_" + PlanningGraphCompiler.Fingerprint(workflow.Key + "\n" + owner.Key + "\n" + outcome + "\n" + role)[..16];
            if (!identities.Add(key)) throw new InvalidOperationException("A reserved adapter identity collides with accepted behavior.");
            return key;
        }
    }
}
