using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContinuationValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("length")]
    public async Task InvalidArrayProjectionKeepsCompilerDiagnosticInsteadOfBlamingCallerInput(string field)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Inputs = [new() { Kind = "input", Source = "x" }];
        var extension = new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/root", Tasks =
        [new() { Id = "records", Kind = "value", Objective = "Retain numeric records.",
            Outputs = [new("items", new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] })] },
         new() { Id = "invalid", Kind = "value", Objective = "Attempt an invalid object field on an array.",
            Outputs = [new("result", new() { Kind = "field", Port = field, Items = [new() { Kind = "output", Source = "records", Port = "items" }] })] }] };
        var candidate = PlanningConstruction.Apply(state, extension);
        var expected = TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics;
        Assert.Equal("TASK_FIELD_TYPE", Assert.Single(expected).Code);
        var original = Snapshot(state);
        var raw = JsonSerializer.Serialize(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
        Assert.Equal(expected, failure.Diagnostics);
        Assert.Equal("/tasks/invalid/outputs/result", Assert.Single(failure.Diagnostics).Location);
        Assert.Equal(original, Snapshot(state));
        Assert.Equal(raw, JsonSerializer.Serialize(extension, PlanningJsonContext.Default.PlanningConstructionExtension));
    }

    [Fact]
    public async Task ContinuationKeepsExactMissingInputDiagnosticWhenCandidateIsTechnicallyValid()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        candidate.Extension.CompleteBlock = false;
        candidate.Extension.ContinueInScope = "/root";
        state.Construction.Compact!.CurrentStep!.Inputs = [new() { Kind = "input", Source = "not_declared" }];
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var original = Snapshot(state);
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("CONSTRUCTION_STEP_INPUT_INVALID", diagnostic.Code);
        Assert.Equal("/step/inputs/0", diagnostic.Location);
        Assert.Contains("'not_declared'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(original, Snapshot(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationProbeUsesResolvedContractsWithoutMutatingCatalogOrAdmittingAmbiguity(bool ambiguous)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "operation"; step.Inputs = [new() { Kind = "input", Source = "x" }]; step.OperationIds = ["read"];
        state.Catalog!.AllowedStepTypes.Add("mcp.call");
        state.Discovery.Resolved.Add(Contract("v1"));
        if (ambiguous) state.Discovery.Resolved.Add(Contract("v2"));
        var candidate = PlanningConstruction.Apply(state, new() { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/root",
            Tasks = [new() { Id = "read", Kind = "operation", Operation = "read", Objective = "Read the caller's exact record.",
                Inputs = [new("number", new() { Kind = "input", Source = "x" })] }] });
        var original = Snapshot(state);
        if (ambiguous)
        {
            var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
            Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "TASK_OPERATION_UNKNOWN" && diagnostic.Location == "/tasks/read/operation");
        }
        else PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Equal(original, Snapshot(state));
        Assert.Empty(state.Catalog.Capabilities);
    }

    private static PlanningCapability Contract(string version) => new() { Id = "read", Version = version, Kind = "tool", StepType = "mcp.call",
        Server = "source", Method = "read", EffectKind = "read", Description = "Read the exact numeric record.",
        InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["number"] = new JsonObject { ["type"] = "number" } },
            ["required"] = new JsonArray("number"), ["additionalProperties"] = false },
        OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } };
    private static string Snapshot(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
}
