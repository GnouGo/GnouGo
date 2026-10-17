using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed class NestedMappingLimitsTests
{
    [Theory]
    [InlineData(1800, true)]
    [InlineData(2200, false)]
    public void ImportAndOutputMaterializationReportTheEnforcedNestedCeiling(int characters, bool output)
    {
        var sandbox = new JintSandbox(memoryLimitBytes: 8192);
        var shared = new JintSandbox.MappingAllowance(100_000, TimeSpan.FromSeconds(15), 1_000_000_000);
        var error = Assert.Throws<WorkflowRuntimeException>(() => sandbox.ExecuteMappingValue("source",
            JsonValue.Create(new string('x', characters)), new(), null, shared, CancellationToken.None));
        Assert.Equal("CONTRACT_UNSATISFIED", error.Code); Assert.False(error.Retryable);
        Assert.Equal("materialized_memory", error.Details!["exhausted_resource"]!.ToString());
        var counters = error.Details["sandbox"]!;
        Assert.Equal(8192, counters["memory_limit_bytes"]!.GetValue<long>());
        Assert.Equal(1_000_000_000, counters["shared_memory_limit_bytes"]!.GetValue<long>());
        Assert.Equal("nested", counters["enforcement_scope"]!.ToString());
        Assert.True(counters["materialized_bytes"]!.GetValue<long>() > 8192);
        Assert.Equal(output, counters["output_bytes"]!.GetValue<long>() > 0);
        Assert.Contains("8192 bytes", error.Message);
        Assert.Equal(1_000_000_000, shared.Snapshot()["memory_limit_bytes"]!.GetValue<long>());
    }

    [Fact]
    public void EngineAllocationFailureReportsNestedRatherThanSharedMemoryLimit()
    {
        var sandbox = new JintSandbox(maxStatements: 100_000, memoryLimitBytes: 100_000);
        var shared = new JintSandbox.MappingAllowance(100_000, TimeSpan.FromSeconds(15), 1_000_000_000);
        var input = new JsonArray(Enumerable.Range(0, 64).Select(_ => (JsonNode?)JsonValue.Create("observed")).ToArray());
        var error = Assert.Throws<WorkflowRuntimeException>(() => sandbox.ExecuteMappingValue(
            "source.map(x=>source.map(y=>({a:x,b:y})))", input, new(), null, shared, CancellationToken.None));
        Assert.Equal("allocated_memory", error.Details!["exhausted_resource"]!.ToString());
        Assert.Equal(100_000, error.Details["sandbox"]!["memory_limit_bytes"]!.GetValue<long>());
        Assert.Equal(1_000_000_000, error.Details["sandbox"]!["shared_memory_limit_bytes"]!.GetValue<long>());
        Assert.Contains("100000 bytes", error.Message);
    }

    [Fact]
    public void SharedFailureKeepsItsOwnLimitAndCancellationRemainsCancellation()
    {
        var shared = new JintSandbox.MappingAllowance(1000, TimeSpan.FromSeconds(15), 1000) { ImportedBytes = 1001 };
        var error = Assert.Throws<WorkflowRuntimeException>(shared.Check);
        Assert.Equal(1000, error.Details!["sandbox"]!["memory_limit_bytes"]!.GetValue<long>());
        Assert.Null(error.Details["sandbox"]!["shared_memory_limit_bytes"]);
        Assert.Null(error.Details["sandbox"]!["enforcement_scope"]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new JintSandbox().ExecuteMappingValue("source", JsonValue.Create(true), new(), null, shared, cancellation.Token));
    }
}
