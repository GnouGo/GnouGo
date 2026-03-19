using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowInputDefaultsTests
{
    [Fact]
    public void Apply_AddsMissingDefaults()
    {
        var workflow = new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef>
            {
                ["site"] = new() { Type = "string", Required = false, Default = "https://example.com/" },
                ["count"] = new() { Type = "number", Required = false, Default = 3 },
                ["flags"] = new() { Type = "array", Required = false, Default = new List<object?> { true, false } }
            }
        };

        var merged = WorkflowInputDefaults.Apply(workflow, new JsonObject());

        Assert.Equal("https://example.com/", merged["site"]!.GetValue<string>());
        Assert.Equal(3, merged["count"]!.GetValue<int>());
        var flags = Assert.IsType<JsonArray>(merged["flags"]);
        Assert.Equal(2, flags.Count);
        Assert.True(flags[0]!.GetValue<bool>());
        Assert.False(flags[1]!.GetValue<bool>());
    }

    [Fact]
    public void Apply_DoesNotOverrideExplicitInputs()
    {
        var workflow = new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef>
            {
                ["site"] = new() { Type = "string", Required = false, Default = "https://example.com/" }
            }
        };

        var merged = WorkflowInputDefaults.Apply(
            workflow,
            new JsonObject { ["site"] = "https://www.iana.org/" });

        Assert.Equal("https://www.iana.org/", merged["site"]!.GetValue<string>());
    }
}

