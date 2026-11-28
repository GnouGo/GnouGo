using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class AgentObservationPageTests
{
    [Fact]
    public void ReadPageSelectsNestedEvidenceWithoutIncludingEarlierLargeValues()
    {
        var observation = new JsonObject { ["payload"] = new string('x', 1_675_315),
            ["pages"] = new JsonArray((JsonNode)new JsonObject { ["status"] = "captured" }) };
        var page = AgentTaskContracts.ReadObservationValue("observation", "browser", observation, null, 0)!;
        Assert.Equal(AgentTaskContracts.ObservationPageSize, page["text"]!.GetValue<string>().Length);
        Assert.Equal(["payload", "pages"], page["fields"]!.AsArray().Select(k => k!.GetValue<string>()));
        var path = new JsonArray("pages", 0, "status");
        var selected = AgentTaskContracts.ReadObservationValue("observation", "browser", observation, path, 0)!;
        Assert.Equal("\"captured\"", selected["text"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(path, selected["path"]));
        Assert.Null(AgentTaskContracts.ReadObservationValue("observation", "browser", observation, new JsonArray("pages", 1), 0));
    }
}
