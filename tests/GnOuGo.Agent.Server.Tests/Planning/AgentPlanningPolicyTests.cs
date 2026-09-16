using System.Text.Json;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests.Planning;

public sealed class AgentPlanningPolicyTests
{
    [Fact]
    public void TypedPolicyCoversUnchangedHostTextAndRetainsPermissionException()
    {
        var policy = AgentPlanningPolicy.Create();
        var text = policy["instructions"]!.ToString();
        const string retained = "Generate a self-contained chat-agent workflow. Host configuration, credentials and saving the agent are outside its runtime boundary. Preserve every required operation, runtime outcome, and resource cleanup. The .GnOuGo directory is reserved for internal state. Workflow-created files belong under workflows/<purpose-specific-name>; propagate declared materialization outputs to subsequent steps. Unless explicitly requested otherwise, obtain runtime human confirmation before the first external write, with zero writes after rejection. Do not request review of the workflow's own YAML during execution.";
        Assert.Equal(retained, text);
        var evidence = JsonSerializer.Deserialize(policy["declared_evidence"], PlanningJsonContext.Default.PlanningDeclaredPolicyEvidence)!;
        Assert.Equal(1, evidence.Version); Assert.Equal(PlanningGraphCompiler.Fingerprint(text), evidence.SourceFingerprint);
        Assert.Equal(text, string.Join(" ", evidence.Clauses.Select(c => text.Substring(c.Start, c.Length))));
        var permission = Assert.Single(evidence.Clauses, c => c.Meanings.Any(m => m.Kind == "confirmation_required"));
        Assert.Contains("Unless explicitly requested otherwise", text.Substring(permission.Start, permission.Length));
        Assert.Contains(permission.Meanings, m => m.Kind == "rejection_condition");
        Assert.Equal("workflow_policy", Assert.Single(evidence.Clauses[^1].Meanings).Kind);
        Assert.DoesNotContain(evidence.Clauses.SelectMany(c => c.Meanings), m => m.Kind == "confirmation_forbidden");
        Assert.False(policy["allow_remote_workflow_refs"]!.GetValue<bool>());
    }
}
