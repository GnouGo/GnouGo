using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BindingIdentifierTests
{
    private static string Legacy(PlanningValue value) => "ref_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(
        new[] { value.Kind, value.Source ?? "", value.ResultChannel ?? "default" }.Concat(value.Path).ToArray(), PlanningJsonContext.Default.StringArray))[..20];

    [Theory]
    [InlineData("source", "message")]
    [InlineData("producteur_renommé", "résumé/utile")]
    public void CompactIdentityPreservesEveryFingerprintBitAndLegacyReferences(string source, string field)
    {
        var value = new PlanningValue { Kind = "output", Source = source, ResultChannel = "structured", Path = [field] };
        var legacy = Legacy(value); var compact = PlanningOutputBindings.Id(value);
        Assert.Equal(16, compact.Length);
        Assert.Equal(legacy[4..], Convert.ToHexStringLower(Convert.FromBase64String(compact[2..].Replace('-', '+').Replace('_', '/') + "==")));
        Assert.Equal(compact, PlanningOutputBindings.NormalizeIdentifier(legacy));
        Assert.Equal(compact, PlanningOutputBindings.NormalizeIdentifier(compact));
        Assert.NotEqual(compact, PlanningOutputBindings.Id(new() { Kind = "output", Source = source, Path = [field] }));
    }

    [Theory]
    [InlineData("input_name")]
    [InlineData("entrée")]
    public void RetainedPartialCandidateUpgradesWithoutChangingLiteralsOrBindings(string name)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var prep = Preparation();
        workflow.Inputs = [new() { Name = name, Schema = new() { Type = "string" } }];
        var value = new PlanningValue { Kind = "input", Source = name }; var legacy = Legacy(value);
        var unit = new PlanningConstructionUnit { WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = new JsonObject { ["nodes"] = new JsonObject { ["greeting"] = new JsonObject { ["input"] = new JsonObject {
            ["kind"] = "compute", ["text"] = "source", ["members"] = new JsonArray(new JsonObject { ["name"] = "source", ["value"] = new JsonObject { ["kind"] = "binding", ["reference"] = legacy } }),
            ["literalEvidence"] = legacy } } } };
        var retained = candidate.ToJsonString();
        var upgraded = PlanningConstruction.UpgradeCandidate(graph, unit, candidate, prep);
        Assert.Equal(retained, candidate.ToJsonString());
        Assert.Equal(legacy, upgraded["nodes"]!["greeting"]!["input"]!["literalEvidence"]!.GetValue<string>());
        Assert.Equal(PlanningOutputBindings.Id(value), upgraded["nodes"]!["greeting"]!["input"]!["members"]![0]!["value"]!["reference"]!.GetValue<string>());
        var index = PlanningDataflow.Index(workflow, prep, graph, "greeting");
        Assert.True(JsonNode.DeepEquals(PlanningDataflow.Expand(candidate, index), PlanningDataflow.Expand(upgraded, index)));
        var restored = JsonNode.Parse(upgraded.ToJsonString())!;
        Assert.True(JsonNode.DeepEquals(upgraded, PlanningConstruction.UpgradeCandidate(graph, unit, restored.AsObject(), prep)));
    }

    [Fact]
    public void LegacyPublicExportUpgradesAndStillSelectsTheOriginalProducer()
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var prep = Preparation();
        var value = workflow.Outputs[0].Value!;
        var unit = new PlanningConstructionUnit { WorkflowKey = workflow.Key, Kind = "outputs", ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = new JsonObject { ["outputs"] = new JsonObject { ["message"] = new JsonObject { ["reference"] = Legacy(value) } } };
        var upgraded = PlanningConstruction.UpgradeCandidate(graph, unit, candidate, prep);
        Assert.Equal(PlanningOutputBindings.Id(value), upgraded["outputs"]!["message"]!["reference"]!.GetValue<string>());
        var result = PlanningConstruction.Apply(graph, unit, upgraded, prep);
        Assert.Equal(value.Source, result.Workflows[0].Outputs[0].Value!.Source);
        Assert.Equal(value.Path, result.Workflows[0].Outputs[0].Value!.Path);
    }

    [Theory]
    [InlineData("ref_0123456789abcdef012g")]
    [InlineData("ref_0123456789abcdef0123")]
    [InlineData("ref_1234")]
    [InlineData("b_AAAAAAAAAAAAAA")]
    public void UnknownReferencesStayInvalidAndUnchanged(string identifier)
    {
        var candidate = new JsonObject { ["kind"] = "binding", ["reference"] = identifier };
        var index = new Dictionary<string, PlanningBinding>();
        Assert.True(JsonNode.DeepEquals(candidate, PlanningDataflow.Transport(candidate, index)));
        Assert.Throws<PlanningDataflow.BindingException>(() => PlanningDataflow.Expand(candidate, index));
    }
}
