using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class WorkflowRunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredInputsReportReuseWithoutRepeatingChecksOrUncertainEffects(bool receiptPersisted)
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                - id: write
                  type: test.effect
                  input: |
                    ${(()=>{const writtenValue={value:data.inputs.value};return writtenValue;})()}
                  expression_contracts:
                    '':
                      writtenValue:
                        origin: /original/write/value
                        schema: {type: object, properties: {value: {const: written}}, required: [value]}
                finally:
                - {id: cleanup, type: test.effect, input: {value: cleanup}}
            """;
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryWorkflowRunStore(); var effect = new Effect();
        var fault = new FaultStore(store, r => r.Invocations.Values.Any(i => i.Id.EndsWith("/write", StringComparison.Ordinal) && i.Status == "completed"), receiptPersisted);
        var firstTrace = new WorkflowTelemetryTests.RecordingTelemetry(); var engine = Engine(fault, effect); engine.Telemetry = firstTrace;
        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteAsync(Compile(yaml), new JsonObject { ["value"] = "written" }, ct));
        Assert.Equal("gnougo-flow.input.check", Assert.Single(Assert.Single(firstTrace.StepSpans).SpanEvents).Name);
        Assert.Equal(["written"], effect.Values);
        var saved = (await store.ReadAsync("tenant", "run", ct))!;
        await using (var lease = await store.AcquireAsync("tenant", "run", saved.Revision, ct))
        {
            // A new evaluation would fail. Recovery must reuse the committed
            // resolved input, not reconstruct it from mutable present inputs.
            lease.Run.Inputs!["value"] = "must not be re-evaluated";
            await lease.SaveAsync(ct);
        }
        saved = (await store.ReadAsync("tenant", "run", ct))!;
        var trace = new WorkflowTelemetryTests.RecordingTelemetry(); var recovered = Engine(store, effect); recovered.Telemetry = trace;
        var result = await recovered.ResumeAsync("tenant", "run", saved.Revision, Compile(yaml), ct);
        var entry = Assert.Single(Assert.Single(trace.StepSpans, s => s.Name == "write").SpanEvents);
        Assert.Equal("gnougo-flow.input.reused", entry.Name);
        Assert.Equal("committed_resolved_input", entry.Attributes!.Single(p => p.Key == "gnougo-flow.input.source").Value);
        Assert.DoesNotContain(trace.StepSpans.SelectMany(s => s.SpanEvents), e => e.Name == "gnougo-flow.input.check");
        if (receiptPersisted)
        {
            Assert.True(result.Success, result.Error?.Message); Assert.Equal(["written", "cleanup"], effect.Values);
        }
        else
        {
            Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
            Assert.Equal(["written"], effect.Values); Assert.DoesNotContain(trace.StepSpans, s => s.Name == "cleanup");
        }
    }
}
