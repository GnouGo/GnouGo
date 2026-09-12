using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ObservationRepairTests
{
    [Theory]
    [InlineData("items", "emit", "permission")]
    [InlineData("éléments", "publier", "consentement")]
    public void CollectionLoopsDoNotInventObservationSequencesFromStaticPermission(string loopKey, string effectKey, string permissionKey)
    {
        var effect = new PlanningNode { Key = effectKey, Type = "mcp.call" };
        var loop = new PlanningNode
        {
            Key = loopKey,
            Type = "loop.sequential",
            Steps = [effect],
            Input = Obj(
            ("items", new() { Kind = "input", Source = "findings" }),
            ("while", new() { Kind = "output", Source = permissionKey, Path = ["response"] }))
        };
        var workflow = new PlanningWorkflow { Steps = [new() { Key = permissionKey, Type = "human.input" }, loop] };
        Assert.Empty(PlanningScenarioFixtures.ScenarioObservationSources(workflow, loop));
        loop.Input.Members.RemoveAt(1);
        loop.Input.Members.Add(new("while", new()
        {
            Kind = "compute",
            Text = "previous == null || previous.more",
            Members =
            [new("previous", new() { Kind = "loop_previous", Source = loopKey, Path = [effectKey, "response"] })]
        }));
        Assert.Equal(effect, Assert.Single(PlanningScenarioFixtures.ScenarioObservationSources(workflow, loop)));
        var alias = new PlanningNode { Key = "alias", Type = "set", Input = new() { Kind = "output", Source = effectKey } };
        loop.Steps.Add(alias);
        loop.Input.Members[1] = new("while", new() { Kind = "loop_previous", Source = loopKey, Path = [alias.Key] });
        Assert.Equal(effect, Assert.Single(PlanningScenarioFixtures.ScenarioObservationSources(workflow, loop)));
    }
}
