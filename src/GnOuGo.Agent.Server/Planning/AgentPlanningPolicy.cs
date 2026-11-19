using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Agent.Server.Planning;
internal static class AgentPlanningPolicy
{
    internal static PlanningPolicy Create() => new()
    {
        RequireExternalConfirmation = false,
        AllowedStepTypes = ["mcp.call", "llm.call", "set", "agent.run", "mapping.dynamic", "emit", "template.render", "sequence", "parallel", "loop.sequential", "loop.parallel", "switch", "human.input", "workflow.call"],
        Instructions = "Generate a self-contained chat-agent workflow. Host configuration, credentials and saving the agent belong to the host. Preserve requested outputs and cleanup. The .GnOuGo directory is reserved for internal state; workflow-created files belong under workflows/<purpose-specific-name>. A successful write at the requested path delivers the file. When the original request also asks for a caller result, bind it to its declared public output. The host reports execution status. Propagate failures, preserve evidence of actual effects and provide any explicitly requested report. Express the requested business actions. The host manages approval of the generated plan before execution; do not add that approval as a business task."
    };
}
