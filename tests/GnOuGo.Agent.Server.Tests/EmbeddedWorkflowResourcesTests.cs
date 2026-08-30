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

    [Fact]
    public void ConfigureAgentsWorkflow_UsesOnlyGenericCapabilityPreflightRules()
    {
        var yaml = LoadEmbeddedYaml("configure-agents-agent.yaml");

        Assert.Contains("capability_preflight:", yaml, StringComparison.Ordinal);
        Assert.Contains("mode: infer", yaml, StringComparison.Ordinal);
        Assert.Contains("intent_clarification:", yaml, StringComparison.Ordinal);
        Assert.Contains("mode: always", yaml, StringComparison.Ordinal);
        Assert.Contains("max_rounds: 2", yaml, StringComparison.Ordinal);
        Assert.Contains("max_questions: 8", yaml, StringComparison.Ordinal);
        Assert.Contains("llm_budget:", yaml, StringComparison.Ordinal);
        Assert.Contains("max_calls: 100", yaml, StringComparison.Ordinal);
        Assert.Contains("max_total_tokens: 15000000", yaml, StringComparison.Ordinal);
        Assert.Contains("max_elapsed_ms: 18000000", yaml, StringComparison.Ordinal);
        Assert.Contains("unverifiable: fail", yaml, StringComparison.Ordinal);
        Assert.Contains("raw_prompt: \"${data.steps.normalized_prompt.description}\"", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoning: medium", yaml, StringComparison.Ordinal);
        Assert.Contains("Enumerate every required positive external read, write, side effect", yaml, StringComparison.Ordinal);
        Assert.Contains("Classify prohibitions, safety rules, ordering requirements, and invariants as constraints", yaml, StringComparison.Ordinal);
        Assert.Contains("workflow-level finally array", yaml, StringComparison.Ordinal);
        Assert.Contains("Never invent, rename, substitute, or silently omit a required capability", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("git_compare_refs", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copilot_review", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull-request", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publication_policy", yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DynamicWorkflowAgent_EnablesInferredCapabilityPreflight()
    {
        var yaml = LoadEmbeddedYaml("dynamic-workflow-agent.yaml");

        Assert.Contains("capability_preflight:", yaml, StringComparison.Ordinal);
        Assert.Contains("mode: infer", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("configure-agents-agent.yaml")]
    [InlineData("dynamic-workflow-agent.yaml")]
    public void PlanningWorkflows_AdvertiseVisibleWorkflowWorkspaceBoundary(string resourceSuffix)
    {
        var yaml = LoadEmbeddedYaml(resourceSuffix);

        Assert.Contains("`.GnOuGo` is reserved for GnOuGo internal state", yaml, StringComparison.Ordinal);
        Assert.Contains("`workflows/<purpose-specific-name>`", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(".GnOuGo/data/reviews", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".GnOuGo/data/e2e", yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeRepairWorkflow_AdvertisesVisibleWorkflowWorkspaceBoundary()
    {
        var yaml = SmartFlowService.BuildRepairWorkflowYaml();

        Assert.Contains("`.GnOuGo` is reserved for GnOuGo internal state", yaml, StringComparison.Ordinal);
        Assert.Contains("`workflows/<purpose-specific-name>`", yaml, StringComparison.Ordinal);
        Assert.Contains("repair every occurrence of the same proven server/method/request-field contract violation", yaml, StringComparison.Ordinal);
        Assert.Contains("replace it with a compatible discovered capability", yaml, StringComparison.Ordinal);
        Assert.Contains("Never invent or transform enum, const, discriminator, or other constrained MCP literals", yaml, StringComparison.Ordinal);
        Assert.Contains("Treat host-policy-gated values as unavailable", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(".GnOuGo/data/reviews", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".GnOuGo/data/e2e", yaml, StringComparison.OrdinalIgnoreCase);
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
