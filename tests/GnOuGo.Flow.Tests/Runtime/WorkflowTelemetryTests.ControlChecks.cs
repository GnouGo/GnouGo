using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public partial class WorkflowTelemetryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ControlEventsAreOrderedPrivateAndDoNotReportUnreachedChecks(bool authorized, bool invalid)
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                inputs:
                  authorized: {type: boolean}
                  label: {type: string}
                steps:
                - id: effect
                  type: set
                  if: '${(() => { const allowed = data.inputs.authorized; return allowed; })()}'
                  control_contracts:
                    /if:
                      allowed: {source: allowed, order: 0, origin: optionalAction, schema: {type: boolean}}
                  input: '${(() => { const label = data.inputs.label; return {label}; })()}'
                  expression_contracts:
                    '':
                      label: {source: label, order: 0, origin: observedLabel, schema: {type: string, minLength: 1}}
            """;
        var recording = new RecordingTelemetry();
        var result = await new WorkflowEngine { Telemetry = recording }.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
            new JsonObject { ["authorized"] = authorized, ["label"] = invalid ? "" : "PRIVATE OBSERVATION" }, TestContext.Current.CancellationToken);
        Assert.Equal(!authorized || !invalid, result.Success);
        var checks = Assert.Single(recording.StepSpans).SpanEvents.Where(e => e.Name.EndsWith(".check", StringComparison.Ordinal)).ToArray();
        Assert.Equal(authorized ? 2 : 1, checks.Length);
        Assert.Equal("gnougo-flow.control.check", checks[0].Name);
        if (authorized) Assert.Equal("gnougo-flow.input.check", checks[1].Name);
        for (var i = 0; i < checks.Length; i++)
        {
            var attributes = checks[i].Attributes!.ToDictionary(a => a.Key, a => a.Value);
            Assert.Equal(i, attributes["gnougo-flow.check.order"]);
            Assert.DoesNotContain("PRIVATE", string.Join('|', attributes.Values));
            Assert.Equal(i == 1 && invalid ? "failed" : "passed", attributes["gnougo-flow.check.outcome"]);
        }
    }
}
