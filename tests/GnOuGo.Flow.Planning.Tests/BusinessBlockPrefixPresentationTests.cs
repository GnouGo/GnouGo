using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessBlockPrefixPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenOperationCallsUseOneActualSelectionAndTwoDraftsAcrossRestart(bool resolvedOnly)
    {
        const string original = "Using the caller's string query, read records 0 through 7 using the first source, then records 8 and 9 using the second source.";
        const string later = " Produce a separate summary after reading the records.";
        var state = PlannerFixture.Capture(new() { Request = new() { TenantId = "block-prefix", Prompt = original + later,
            MaxModelCalls = 64, Generation = new("medium", 32_000, 8_192), Options = new() { ["automatic_correction"] = true } } });
        var runtime = new TestRuntime();
        var selections = 0; var drafts = 0; var reviews = 0;
        var draftContexts = new List<JsonObject>();
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose)
            {
                var indexed = CurrentPlanningFixture.Intent(Context(request), new JsonArray(new JsonObject
                { ["name"] = "query", ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false }, ["required"] = true }));
                indexed["clauses"]![0]!["text"] = original;
                indexed["clauses"]!.AsArray().Add(new JsonObject { ["text"] = later, ["kind"] = "requirement",
                    ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() });
                return Wire(request, indexed);
            }
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                selections++;
                return Wire(request, new()
                {
                    ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(),
                    ["step"] = new JsonObject
                    {
                        ["scope"] = "/root", ["kind"] = "operation", ["objective"] = original,
                        ["requirementIds"] = new JsonArray("req_0"), ["operationIds"] = new JsonArray("a_first", "a_second"),
                        ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "input", ["source"] = "query" }), ["dependencyTaskIds"] = new JsonArray(),
                        ["expectedOutputs"] = new JsonArray("The ten exact requested records have been read."),
                        ["closeRequirementIds"] = new JsonArray("req_0"), ["outputNames"] = new JsonArray(),
                        ["closeScopes"] = null, ["groupId"] = null, ["prerequisites"] = null
                    }
                });
            }
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                reviews++;
                var context = Context(request);
                Assert.Equal(10, context["actions"]!.AsArray().Count(action => action!["kind"]!.ToString() == "operation"));
                Assert.DoesNotContain("maxTasksPerDraft", request.Prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("pendingCommitments", request.Prompt, StringComparison.Ordinal);
                return new() { Json = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "ok",
                    ["reason"] = "All ten exact original record arguments use their requested sources." } } };
            }
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            draftContexts.Add(Context(request));
            drafts++;
            return Wire(request, new() { ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
            {
                Scope = "/root", RequirementIds = ["req_0"], ContinueInScope = drafts == 1 ? "/root" : null,
                Tasks = Enumerable.Range(drafts == 1 ? 0 : 8, drafts == 1 ? 8 : 2).Select(index => Operation(index, index < 8 ? "a_first" : "a_second", query: true)).ToList()
            }, PlanningJsonContext.Default.PlanningConstructionExtension) });
        };
        state = await Advance(state, runtime); // real original indexing
        Assert.Empty(state.Diagnostics);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        AddOperations(state, resolvedOnly, query: true);
        state = await Advance(state, runtime); // actual model selection of both exact contracts
        Assert.Empty(state.Diagnostics);
        Assert.Equal(1, selections);
        if (resolvedOnly) Assert.Empty(state.Catalog!.Capabilities);
        var selected = JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var stepId = state.Construction.Compact.CurrentStep!.Id;
        var originalRequirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        state = await Advance(state, runtime); // first eight operation calls, omitting the second selected contract
        Assert.Empty(state.Diagnostics);
        Assert.Equal(["a_first"], state.Construction!.Compact!.PendingCommitments["req_0"].Select(binding => binding.Operation).ToArray());
        Assert.Equal(["a_first"], PlanningCompactConstruction.Context(state, useCandidate: true)["commitments"]!["req_0"]!
            .AsArray().Select(binding => binding!["operation"]!.ToString()).ToArray());
        Assert.Equal(originalRequirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        state = await Advance(state, runtime); // technical checkpoint only
        Assert.Empty(state.Diagnostics);
        Assert.Equal(8, state.Plan!.Root.Tasks.Count);
        Assert.All(state.Plan.Root.Tasks, task => Assert.Equal("operation", task.Kind));
        Assert.Equal(0, reviews);
        Assert.Equal(stepId, state.Construction!.Compact!.CurrentStep!.Id);
        Assert.Equal(["a_first", "a_second"], state.Construction.Compact.CurrentStep.OperationIds);
        var continuedSelection = JsonNode.Parse(selected)!;
        continuedSelection["baseHash"] = state.Construction.Compact.CurrentStep.BaseHash;
        Assert.True(JsonNode.DeepEquals(continuedSelection,
            JsonSerializer.SerializeToNode(state.Construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep)));
        Assert.Empty(Assert.Single(state.Construction.Checkpoints).ClosedRequirementIds!);
        Assert.True(state.Construction.Checkpoints[0].Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Equal("in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "req_0").Status);
        Assert.Equal(original, state.Requirements!.Outcomes.Single(requirement => requirement.Id == "req_0").Description);

        var restored = PlannerFixture.Clone(state);
        Assert.Equal(PlanningConstruction.DraftRequest(state, false).Prompt, PlanningConstruction.DraftRequest(restored, false).Prompt);
        var unfinished = PlannerFixture.Clone(restored);
        var premature = PlanningConstruction.Apply(unfinished, new() { Scope = "/root", RequirementIds = ["req_0"], Tasks = [Operation(8, "a_first", query: true)] });
        unfinished.Construction!.Candidate = premature;
        PlanningCompactConstruction.StageCommitments(unfinished, null, premature);
        var missing = PlanningCompactConstruction.CandidateFindings(unfinished);
        Assert.Contains(missing, finding => finding.Code == PlanningCommitments.MissingOperation && finding.Message.Contains("a_second", StringComparison.Ordinal));

        state = await Advance(restored, runtime); // second draft on the unchanged selected block
        Assert.Empty(state.Diagnostics);
        state = await Advance(state, runtime); // one complete-block business review
        Assert.Empty(state.Diagnostics);
        Assert.Equal(1, selections); Assert.Equal(2, drafts); Assert.Equal(1, reviews);
        Assert.Equal(10, state.Plan!.Root.Tasks.Count);
        Assert.All(state.Plan.Root.Tasks, task => Assert.Equal("operation", task.Kind));
        Assert.Equal(Enumerable.Range(0, 10).Select(index => (double)index), state.Plan.Root.Tasks.Select(task => task.Inputs.Single(input => input.Name == "index").Value.Number!.Value));
        Assert.All(state.Plan.Root.Tasks, task => Assert.Equal("query", task.Inputs.Single(input => input.Name == "query").Value.Source));
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.Equal(stepId, checkpoint.Step!.Id));
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Equal("closed", state.Construction.Requirements.Single(requirement => requirement.Id == "req_0").Status);
        Assert.NotEqual("closed", state.Construction.Requirements.Single(requirement => requirement.Id == "req_1").Status);
        Assert.Equal(original, state.Requirements!.Outcomes.Single(requirement => requirement.Id == "req_0").Description);
        Assert.Equal(later, state.Requirements.Outcomes.Single(requirement => requirement.Id == "req_1").Description);
        Assert.Equal(["a_first", "a_second"], state.Requirements.Outcomes.Single(requirement => requirement.Id == "req_0").Commitments!.Select(binding => binding.Operation).ToArray());
        Assert.Equal(original + later, state.Request.Prompt);
        Assert.Equal("operation", JsonNode.Parse(selected)!["kind"]!.ToString());
        Assert.All(draftContexts, context => Assert.False(context.ContainsKey("commitments")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedAuthoringKeepsWholeBlockGoalsDistinctFromTentativeFragmentCommitments(bool repair)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = ["a_first", "a_second"];
        step.ExpectedOutputs = ["Ten records, including the later second-source records."];
        AddOperations(state);
        PlanningCompactConstruction.StageCommitments(state, null);
        state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["targetInputTokens"] = 512;
        if (repair) state.Phase = PlanningPhase.Replanning;
        var originalStep = JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep);
        var originals = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var request = PlanningConstruction.DraftRequest(state, repair);
        var repeated = PlanningConstruction.DraftRequest(PlannerFixture.Clone(state), repair);
        Assert.Equal(request.Prompt, repeated.Prompt);
        Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, repeated.StructuredOutputSchema));
        Assert.True(PlanningRequestMeasurements.Measure(request, repair ? "replan" : PlanningConstruction.DraftPurpose, 32_000).EstimatedInputTokens > 512);
        Assert.Equal(originalStep, JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(originals, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.False(Context(request).ContainsKey("commitments"));
        Assert.Contains("subset", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("expectedOutputs", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("continueInScope", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(["a_first", "a_second"], Context(request)["step"]!["operationIds"]!.AsArray().Select(value => value!.ToString()).ToArray());
        Assert.Equal(2, state.Construction.Compact.PendingCommitments["work"].Count);
    }

    private static void AddOperations(PlanningSession state, bool resolvedOnly = false, bool query = false)
    {
        if (!state.Catalog!.AllowedStepTypes.Contains("mcp.call")) state.Catalog.AllowedStepTypes.Add("mcp.call");
        foreach (var id in new[] { "a_first", "a_second" })
        {
            var capability = new PlanningCapability
            {
                Id = id, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "records", Method = id,
                EffectKind = "read", Description = "Read the exact authorized record index from this source.",
                InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["index"] = new JsonObject { ["type"] = "number" } },
                    ["required"] = new JsonArray("index"), ["additionalProperties"] = false },
                OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
            };
            if (query)
            {
                capability.InputSchema["properties"]!["query"] = new JsonObject { ["type"] = "string" };
                capability.InputSchema["required"]!.AsArray().Add("query");
            }
            (resolvedOnly ? state.Discovery.Resolved : state.Catalog.Capabilities).Add(capability);
        }
    }

    private static PlanTask Operation(int index, string operation, bool query = false)
    {
        var task = new PlanTask { Id = "read_" + index, Kind = "operation", Operation = operation,
            Objective = "Read exact record " + index, Inputs = [new("index", new() { Kind = "number", Number = index })] };
        if (query) task.Inputs.Add(new("query", new() { Kind = "input", Source = "query" }));
        return task;
    }
    private static JsonObject Context(LLMRequest request) => BusinessProjectionDeclarationsTests.Context(request);
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => BusinessReviewProtocolTests.Advance(state, runtime);
}
