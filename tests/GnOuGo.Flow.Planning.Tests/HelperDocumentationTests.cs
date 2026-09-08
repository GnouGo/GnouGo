using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class HelperDocumentationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DocumentationRepairIsSmallDurableAndCannotRewriteExecutableBodies(bool finalValidation, bool changeBody)
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); state.Graph = Graph();
        var unit = new PlanningConstructionUnit { Key = "helper-unit", Kind = "implementation", WorkflowKey = "main", NodeKeys = ["greeting"],
            Status = finalValidation ? "validated" : "invalid", ContractVersion = PlanningDataflow.ContractVersion, Calls = 1 };
        var name = "u_" + PlanningGraphCompiler.Fingerprint(unit.Key)[..8] + "_normalize";
        var script = "function " + name + "(value) { return String(value); }";
        var fixedScript = "/** Convert a value to text.\n * @param {*} value - Input value.\n * @returns {string} The text.\n */\n" + script;
        unit.Functions = script; state.Graph.Workflows[0].Functions = script;
        unit.Candidate = PlanningConstruction.UpgradeCandidate(state.Graph, unit, PlanningConstruction.Values(state.Graph.Workflows[0], unit), state.Preparation!);
        unit.CandidateHash = PlanningGraphCompiler.Fingerprint(unit.Candidate.ToJsonString());
        unit.Diagnostics = [new("FUNCTION_JSDOC_MISSING", "/workflows/0/functions/" + name, "Document the existing function.")];
        state.ConstructionUnits = [unit]; state.Diagnostics = unit.Diagnostics.ToList(); state.Request.MaxRepairs = 1;
        state.RepairAttempt = finalValidation ? 1 : 0;
        // Large unrelated contracts must not be copied into documentation repair.
        state.Preparation!.Capabilities.Add(new() { Id = "unrelated", OutputSchema = new JsonObject { ["description"] = new string('x', 40_000) } });
        var approval = state.ApprovedBehaviorHash; var originalCandidate = unit.Candidate.ToJsonString();
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            calls++; Assert.Equal("repair_unit", phase);
            Assert.InRange(PlanningConstruction.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()), 1, 2000);
            Assert.DoesNotContain("unrelated", request.Prompt);
            Assert.Equal("functions", Assert.Single(request.StructuredOutputSchema!["properties"]!["changes"]!["properties"]!.AsObject()).Key);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = new JsonObject { ["functions"] = changeBody ? fixedScript.Replace("String(value)", "'changed'", StringComparison.Ordinal) : fixedScript }, ["remove"] = new JsonArray() } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(approval, state.ApprovedBehaviorHash); Assert.Equal(1, state.ConstructionUnits[0].RepairCalls);
        if (changeBody)
        {
            Assert.Equal(PlanningStatus.Recovery, state.Status);
            Assert.Equal(originalCandidate, state.ConstructionUnits[0].Candidate!.ToJsonString());
            Assert.Contains(state.Attempts, a => !a.Retained && a.Diagnostics.Any(d => d.Code == "UNIT_PATCH_REJECTED"));
        }
        else
        {
            Assert.Equal(fixedScript, state.ConstructionUnits[0].Functions);
            Assert.Equal(fixedScript, state.Graph!.Workflows[0].Functions);
            Assert.Empty(GeneratedFunctionDocumentation.Validate(state.Graph.Workflows[0].Functions));
            Assert.Equal("Hello", state.Graph.Workflows[0].Steps[0].Input.Members[0].Value.Text);
        }
    }

    [Theory]
    [InlineData("function helper(v) { return v; }", "FUNCTION_JSDOC_MISSING")]
    [InlineData("/** @returns {string} Text */ function helper(v) { return v; }", "FUNCTION_JSDOC_PARAM_MISSING")]
    [InlineData("/** @param {string} v Text */ function helper(v) { return v; }", "FUNCTION_JSDOC_RETURNS_MISSING")]
    public void ConstructionUsesTheRuntimeDocumentationRequirements(string script, string code)
    {
        var finding = Assert.Single(GeneratedFunctionDocumentation.Validate(script));
        Assert.Equal(code, finding.Code); Assert.Equal("helper", finding.Function);
    }

    [Fact]
    public void DocumentationCannotHideChangesInRegexStringsOrAdditionalStatements()
    {
        const string script = "function sample(v) { return /a{2}/.test(v) ? '/** literal */' : `value ${v}`; }";
        PlanningHelperDocumentation.RequireUnchangedExecutable(script, "/** @param {string} v Text\n * @returns {string} Text */\n" + script);
        Assert.Throws<InvalidOperationException>(() => PlanningHelperDocumentation.RequireUnchangedExecutable(script, script.Replace("a{2}", "a{3}", StringComparison.Ordinal)));
        Assert.Throws<InvalidOperationException>(() => PlanningHelperDocumentation.RequireUnchangedExecutable(script, script + "throw Error('changed');"));
    }
}
