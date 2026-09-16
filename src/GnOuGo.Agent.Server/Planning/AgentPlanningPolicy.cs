using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>The host declares the meaning of its own fixed instructions; the planner validates ownership and scope.</summary>
internal static class AgentPlanningPolicy
{
    internal static JsonObject Create()
    {
        (string Text, PlanningDeclaredPolicyMeaning[] Meanings)[] clauses =
        [
            ("Generate a self-contained chat-agent workflow.", [new("workflow_policy", true)]),
            ("Host configuration, credentials and saving the agent are outside its runtime boundary.", [new("workflow_policy", true)]),
            ("Preserve every required operation, runtime outcome, and resource cleanup.", [new("implementation_policy", true)]),
            ("The .GnOuGo directory is reserved for internal state.", [new("implementation_policy", true)]),
            ("Workflow-created files belong under workflows/<purpose-specific-name>;", [new("workflow_policy", true)]),
            ("propagate declared materialization outputs to subsequent steps.", [new("workflow_policy", true)]),
            ("Unless explicitly requested otherwise, obtain runtime human confirmation before the first external write, with zero writes after rejection.", [new("confirmation_required", true), new("rejection_condition", true)]),
            ("Do not request review of the workflow's own YAML during execution.", [new("workflow_policy", true)])
        ];
        var text = string.Join(" ", clauses.Select(c => c.Text));
        var offset = 0;
        var evidence = clauses.Select(c =>
        {
            var clause = new PlanningDeclaredPolicyClause(offset, c.Text.Length, c.Meanings.ToList());
            offset += c.Text.Length + 1;
            return clause;
        }).ToList();
        return new()
        {
            ["instructions"] = text,
            ["declared_evidence"] = JsonSerializer.SerializeToNode(new PlanningDeclaredPolicyEvidence(1, PlanningGraphCompiler.Fingerprint(text), evidence), PlanningJsonContext.Default.PlanningDeclaredPolicyEvidence),
            ["allowed_step_types"] = new JsonArray("mcp.list", "mcp.call", "llm.call", "set", "emit", "assert.non_null", "template.render", "sequence", "parallel", "loop.sequential", "loop.parallel", "switch", "decision.evaluate", "human.input", "workflow.call"),
            ["denied_step_types"] = new JsonArray("workflow.plan", "workflow.execute"),
            ["allow_remote_workflow_refs"] = false
        };
    }
}
