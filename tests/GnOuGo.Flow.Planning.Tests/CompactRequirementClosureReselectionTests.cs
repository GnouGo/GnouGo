using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactRequirementClosureReselectionTests
{
    private const string Scope = "/groups/worker/body";
    private const string Reason = "The worker body is complete, but applying it to every input item is still pending. Close this body without claiming completion of the original requirement.";

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    [InlineData(5, false, false)]
    [InlineData(5, true, false)]
    [InlineData(5, true, true)]
    public async Task RejectedRequirementClaimCanBeWithdrawnWhileClosingTheSameAcceptedBody(int presentation, bool recover, bool retainedGuidance)
    {
        var (state, runtime) = await Ready(presentation);
        var acceptedHash = PlanningConstruction.Hash(state.Plan);
        var originalRequirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var acceptedCheckpoints = JsonSerializer.SerializeToNode(state.Construction!, PlanningJsonContext.Default.PlanningConstructionState)!["checkpoints"]!.ToJsonString();
        runtime.Respond = (request, _) => Wire(request, Draft(request));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.True(state.Construction.Candidate.NeedsIntermediateReview);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            if (presentation == 5)
            {
                Assert.Equal(["req_0"], Context(request)["constructionStage"]!["closeRequirementIds"]!.AsArray().Select(id => id!.ToString()));
                Assert.Equal(["local", "intermediate"], Context(request)["reviewCoverage"]!.AsArray().Select(value => value!.ToString()));
                var rejection = Rejected(presentation);
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(rejection, request.StructuredOutputSchema!));
                return Wire(request, rejection);
            }
            Assert.True(Context(request)["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            return Wire(request, Reviewed());
        };
        state = await Advance(state, runtime);
        if (presentation != 5)
        {
            runtime.Respond = (request, purpose) =>
            {
                Assert.Equal(PlanningConstruction.IntermediatePurpose, purpose);
                return Wire(request, Rejected(presentation));
            };
            state = await Advance(state, runtime);
        }
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        var rejectedIdentity = Assert.Single(state.Construction!.FailedCandidates);
        var historical = PlannerFixture.Clone(state);
        historical.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        historical.Construction!.Limits.PhasePolicy = null;
        var oldGuidance = PlanningCompactConstruction.DraftSemantics(historical, historical.Construction.Compact!.CurrentStep!);
        Assert.Contains("Scope closure is separate:", oldGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("via reselectStep", oldGuidance, StringComparison.Ordinal);

        string? issued = null;
        LLMResponse? savedReceipt = null;
        if (recover)
        {
            var request = PlanningConstruction.DraftRequest(state, repair: true);
            if (retainedGuidance) request.StructuredOutputSchema!["description"] = oldGuidance;
            request = PlanningModelCalls.PrepareRequest(state, "replan", request.Prompt, request.StructuredOutputSchema!.AsObject());
            state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "replan", Request = request, CorrectionAccounting = 3 };
            state.ModelCalls++;
            issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            savedReceipt = Wire(request, Reselect(request, presentation));
            state = PlannerFixture.Clone(state);
        }
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.False(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            var guidance = request.StructuredOutputSchema["description"]!.ToString();
            if (retainedGuidance) Assert.Equal(oldGuidance, guidance);
            else
            {
                Assert.Contains("broader requirements remain open", guidance, StringComparison.Ordinal);
                Assert.Contains("via reselectStep", guidance, StringComparison.Ordinal);
            }
            Assert.Empty(PlanningContractValidation.ValidateInstance(Reselect(request, presentation), request.StructuredOutputSchema));
            return savedReceipt ?? Wire(request, Reselect(request, presentation));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.PendingCall);
        Assert.Equal(calls + (recover ? 0 : 1), state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(acceptedCheckpoints, JsonSerializer.SerializeToNode(state.Construction, PlanningJsonContext.Default.PlanningConstructionState)!["checkpoints"]!.ToJsonString());
        Assert.Equal(Reason, Assert.Single(state.Construction.Compact!.RejectedSteps)["reason"]!.ToString());
        if (presentation == 5) Assert.Equal("abandon", state.Construction.Compact.RejectedSteps[0]["disposition"]!.ToString());
        Assert.Equal(Scope, state.Construction.Compact.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction.Compact.ReplacementRequirements);

        state = PlannerFixture.Clone(state);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var response = new JsonObject
            {
                ["baseHash"] = acceptedHash, ["evidenceRequests"] = null, ["clarifications"] = null,
                ["step"] = new JsonObject
                {
                    ["scope"] = Scope, ["kind"] = "close", ["groupId"] = null,
                    ["objective"] = "Close the completed reusable body; iteration over caller items is future work.",
                    ["requirementIds"] = new JsonArray("req_0"), ["closeRequirementIds"] = new JsonArray(),
                    ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "output", ["source"] = "calculate", ["port"] = "result" }),
                    ["dependencyTaskIds"] = new JsonArray("calculate"), ["expectedOutputs"] = new JsonArray("result"), ["outputNames"] = new JsonArray()
                }
            };
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var step = state.Construction!.Compact!.CurrentStep!;
        Assert.Equal(Scope, step.Scope); Assert.Equal(["req_0"], step.RequirementIds); Assert.Empty(step.CloseRequirementIds);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            return Wire(request, Draft(request));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Equal(acceptedHash, state.Construction.Candidate.Hash);
        Assert.NotEqual(rejectedIdentity, PlanningConstruction.FailureIdentity(state, state.Construction.Candidate));
        runtime.Respond = (request, _) =>
        {
            if (presentation == 5)
            {
                var context = Context(request);
                Assert.Empty(context["constructionStage"]!["closeRequirementIds"]!.AsArray());
                Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
                var prior = Assert.Single(context["defectProgress"]!.AsArray())!;
                Assert.Equal("claims_changed", prior["status"]!.ToString());
                return Wire(request, new()
                {
                    ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray(),
                        ["resolutions"] = new JsonArray(new JsonObject { ["id"] = prior["id"]!.DeepClone(), ["status"] = "resolved",
                            ["obligation"] = "/requirements/0", ["implementation"] = "/scopeInterface",
                            ["explanation"] = "The original obligation remains open; this candidate now claims only the completed body closure." }) },
                    ["uncertainty"] = "Execution remains unverified."
                });
            }
            Assert.False(Context(request)["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            Assert.False(Context(request)["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            return Wire(request, Reviewed());
        };
        for (var i = 0; i < 2 && state.Construction!.Candidate is not null; i++) state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(3, state.Construction.Checkpoints.Count);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(originalRequirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal("in_progress", Assert.Single(state.Construction.Requirements).Status);
        Assert.Empty(state.Construction.Checkpoints[^1].ClosedRequirementIds!);
        Assert.Equal([Scope], state.Construction.Checkpoints[^1].ClosedScopes);
        Assert.DoesNotContain(Scope, state.Construction.OpenScopes);
        Assert.Contains("/root", state.Construction.OpenScopes);
        Assert.False(state.Construction.Complete);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(0, state.Construction.DraftCorrections);
        Assert.Equal(calls + (recover ? 0 : 1) + (presentation == 5 ? 3 : 4), state.ModelCalls);
        Assert.Contains(rejectedIdentity, state.Construction.FailedCandidates);
        var restored = PlannerFixture.Clone(state);
        Assert.Equal(state.ModelCalls, restored.ModelCalls); Assert.Equal(state.ReplanAttempts, restored.ReplanAttempts);
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(restored.Plan));
    }

    private static async Task<(PlanningSession, TestRuntime)> Ready(int presentation)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone();
            JsonObject response = request.StructuredOutputSchema["properties"]!["clauses"] is not null
                ? new() { ["sourceId"] = source, ["clauses"] = new JsonArray(new JsonObject
                    { ["text"] = Context(request)["sources"]![0]!["text"]!.DeepClone(), ["kind"] = "requirement", ["interfaces"] = "output" }) }
                : new JsonObject { ["sourceId"] = source, ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray(new JsonObject
                    { ["name"] = "result", ["required"] = true, ["type"] = new JsonObject { ["kind"] = "array", ["nullable"] = false,
                        ["items"] = new JsonObject { ["kind"] = "number", ["nullable"] = false }, ["minItems"] = null, ["maxItems"] = null } }) };
            if (request.StructuredOutputSchema["properties"]!["interfaces"] is not null)
                response = new JsonObject { ["sourceId"] = source.DeepClone(), ["interfaces"] = new JsonObject
                {
                    ["inputs"] = response["inputs"]!.DeepClone(), ["outputs"] = response["outputs"]!.DeepClone(),
                    ["dispositions"] = new JsonArray(Context(request)["sources"]!.AsArray().Select(original => (JsonNode)new JsonObject
                    {
                        ["sourceId"] = original!["id"]!.DeepClone(), ["version"] = original["version"]!.DeepClone(),
                        ["kind"] = "interfaces", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray("result"),
                        ["reason"] = "The original clause declares the ordered numeric array as the public result.",
                        ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = original["id"]!.DeepClone(), ["version"] = original["version"]!.DeepClone() })
                    }).ToArray())
                }, ["evidenceRequests"] = null, ["clarifications"] = null };
            return Wire(request, response);
        } };
        var state = await Advance(PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "requirement-closure",
            Prompt = "Apply reusable numeric processing to every item and return the ordered numeric array as result.",
            Options = new() { ["automatic_correction"] = true } } }, presentation), runtime);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Groups = [new() { Id = "worker", Inputs = [new() { Name = "item", Type = new() { Kind = "number" } }] }] };
        state.Construction!.OpenScopes = ["/root", Scope];
        state.Construction.Checkpoints.Add(new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(state.Plan), RequirementIds = ["req_0"] });
        state.Plan.Groups[0].Body = new()
        {
            Tasks = [new() { Id = "calculate", Kind = "value", Objective = "Increment the worker's input.",
                Outputs = [new("result", new() { Kind = "arithmetic", Text = "add", Items = [new() { Kind = "input", Source = "item" }, new() { Kind = "number", Number = 1 }] })] }],
            Outputs = [new("result", new() { Kind = "output", Source = "calculate", Port = "result" })]
        };
        var hash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Checkpoints.Add(new() { Sequence = 2, PlanHash = hash, TaskIds = ["calculate"], RequirementIds = ["req_0"] });
        state.Construction.Requirements[0].Status = "in_progress";
        state.Construction.Requirements[0].Checkpoints = [1, 2];
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Commitments = [] };
        state.Construction.Compact!.CurrentStep = new() { Id = "close", BaseHash = hash, Scope = Scope, Kind = "close",
            Objective = "Close the reusable worker body.", RequirementIds = ["req_0"], CloseRequirementIds = ["req_0"],
            Inputs = [new() { Kind = "output", Source = "calculate", Port = "result" }], InputTaskIds = ["calculate"], DiscoveryComplete = true };
        return (state, runtime);
    }

    private static JsonObject Draft(LLMRequest request) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.DeepClone(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
            { Scope = Scope, RequirementIds = ["req_0"], CloseScopes = [Scope] }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["clarifications"] = null,
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static JsonObject Reselect(LLMRequest request, int presentation)
    {
        var response = Draft(request); response["extension"] = null;
        response["reselectStep"] = presentation == 5
            ? new JsonObject { ["reason"] = Reason, ["disposition"] = "abandon", ["prerequisite"] = null }
            : JsonValue.Create(Reason);
        return response;
    }
    private static JsonObject Rejected(int presentation)
    {
        var finding = new JsonObject
        {
            ["kind"] = "inconsistency", ["location"] = "/requirements/0",
            ["explanation"] = "Completing the reusable body does not yet apply it to every input item.",
            ["uncertainty"] = "The body is complete; the broader completion claim is premature."
        };
        if (presentation == 5)
        {
            finding["obligation"] = "/requirements/0"; finding["implementation"] = "/scopeInterface";
            finding["fact"] = null; finding["requiredBinding"] = null;
            return new() { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray(finding) },
                ["uncertainty"] = "No execution has occurred." };
        }
        finding["evidence"] = new JsonArray("/requirements/0", "/step", "/constructionStage");
        return new() { ["findings"] = new JsonArray(finding), ["uncertainty"] = "No execution has occurred.", ["evidenceRequests"] = null };
    }
    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
