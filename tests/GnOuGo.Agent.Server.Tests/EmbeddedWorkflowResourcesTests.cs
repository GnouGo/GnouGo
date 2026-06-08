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
    [InlineData("main-routing-agent.yaml")]
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

    [Fact]
    public void ConfigureAgentsWorkflow_GeneratesAgentYamlThroughWorkflowPlan()
    {
        var yaml = LoadEmbeddedYaml("configure-agents-agent.yaml");

        Assert.Contains("- id: generate_workflow", yaml, StringComparison.Ordinal);
        Assert.Contains("type: workflow.plan", yaml, StringComparison.Ordinal);
        Assert.Contains("${data.steps.generate_workflow.yaml}", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("You are a GnOuGo.Flow workflow YAML expert. Generate a valid workflow YAML", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("${data.steps.generate_workflow.text}", yaml, StringComparison.Ordinal);
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
