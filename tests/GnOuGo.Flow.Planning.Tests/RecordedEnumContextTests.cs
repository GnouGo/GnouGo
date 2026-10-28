using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RecordedEnumContextTests
{
    [Fact]
    public void ArchivedOriginalContractsPreserveExactFiniteDomains()
    {
        var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", "retained-enums.json")))!;
        var original = recording.ToJsonString();
        var catalog = recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
        var discovery = recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        JsonNode Domain(string id, string input)
        {
            var capability = catalog.Capabilities.Concat(discovery.Resolved).First(c => c.Id == id);
            return TaskOperations.Describe(capability).Inputs.Single(port => port.Name == input).Schema;
        }
        var command = Domain("cap_5c01f9aa688163d23a30ff8b", "commandName")["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
        Assert.Contains("delete_directory", command); Assert.DoesNotContain("bash", command);
        Assert.Equal(["LEFT", "RIGHT"], Domain("cap_0c45707eabb02da3b0885cf0", "side")["enum"]!.AsArray().Select(v => v!.ToString()));
        Assert.Equal(["FILE", "LINE"], Domain("cap_0c45707eabb02da3b0885cf0", "subjectType")["enum"]!.AsArray().Select(v => v!.ToString()));
        Assert.Equal(original, recording.ToJsonString());
    }
}
