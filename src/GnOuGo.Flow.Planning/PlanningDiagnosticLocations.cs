using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Maps a runtime argument path to its exact typed value coordinates.</summary>
internal static class PlanningDiagnosticLocations
{
    internal static PlanningDiagnostic TypedInput(PlanningDiagnostic finding, PlanningGraph graph)
    {
        for (var i = 0; i < graph.Workflows.Count; i++)
        {
            var workflow = graph.Workflows[i];
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, $"/workflows/{i}/steps")
                .Concat(PlanningGraphValidation.Located(workflow.Finally, $"/workflows/{i}/finally")))
            {
                var root = path + "/input/";
                if (!finding.Location.StartsWith(root, StringComparison.Ordinal)) continue;
                var suffix = finding.Location[root.Length..];
                if (suffix.StartsWith("members/", StringComparison.Ordinal) || suffix.StartsWith("items/", StringComparison.Ordinal)) return finding;
                var value = node.Input; var location = path + "/input";
                foreach (var part in suffix.Split('/'))
                {
                    if (value.Kind == "object")
                    {
                        var index = value.Members.FindIndex(m => m.Name == part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                        if (index < 0) break;
                        location += "/members/" + index + "/value"; value = value.Members[index].Value;
                    }
                    else if (value.Kind == "array" && int.TryParse(part, out var index) && index >= 0 && index < value.Items.Count)
                    { location += "/items/" + index; value = value.Items[index]; }
                    else break;
                }
                return finding with { Location = location };
            }
        }
        return finding;
    }
}
