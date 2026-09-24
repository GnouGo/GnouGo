using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Agent.Server.Planning;
internal static class AgentPlanningPolicy
{
    internal static PlanningPolicy Create() => new()
    {
        RequireExternalConfirmation = true,
        AllowedStepTypes = ["mcp.call", "llm.call", "set", "agent.run", "array.project", "value.project", "number.add", "number.multiply", "number.default", "value.validate", "emit", "assert.non_null", "template.render", "sequence", "parallel", "loop.sequential", "loop.parallel", "switch", "decision.evaluate", "human.input", "workflow.call"],
        Instructions = "Generate a self-contained chat-agent workflow. Host configuration, credentials and saving the agent belong to the host. Preserve requested outputs and cleanup. The .GnOuGo directory is reserved for internal state; workflow-created files belong under workflows/<purpose-specific-name>. Propagate declared materialization outputs. Do not request review of the workflow's YAML during execution."
    };
}
