using System.Reflection;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class EmbeddedWorkflowResourcesTests
{
    [Theory]
    [InlineData("configure-agents-agent.yaml")]
    [InlineData("dynamic-workflow-agent.yaml")]
    public void EmbeddedWorkflowYaml_ParsesAndCompiles(string resourceSuffix)
    {
        var yaml = LoadEmbeddedYaml(resourceSuffix);

        var document = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(document);

        Assert.NotNull(compiled.Entrypoint);
        Assert.True(compiled.Workflows.Count > 0);
    }

    [Fact]
    public void ConfigureAgentsWorkflow_DoesNotHardcodeAgentCreationProviderOrModel()
    {
        var yaml = LoadEmbeddedYaml("configure-agents-agent.yaml");

        Assert.DoesNotContain("provider: \"${data.inputs.agent_llm_provider}\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("model: \"${data.inputs.agent_llm_model}\"", yaml, StringComparison.Ordinal);
    }

    private static string LoadEmbeddedYaml(string resourceSuffix)
    {
        var assembly = typeof(SmartFlowService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName!);
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}

