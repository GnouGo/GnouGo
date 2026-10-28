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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredPublicResultRejectsChildClaimBeforeJudgeAndAllowsExactReselection(bool recover)
    {
        var (state, runtime) = await Ready();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            return Wire(request, Draft(request));
        };
        state = await Advance(state, runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("CONSTRUCTION_REQUIREMENT_OUTPUT_MISSING", diagnostic.Code);
        Assert.Equal("/step/closeRequirementIds", diagnostic.Location);
        Assert.Equal(PlanningPhase.Replanning, state.Phase);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.DoesNotContain(state.Construction.Candidate!.Reviews, review => review["purpose"] is not null);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));

        string? issued = null;
        LLMResponse? receipt = null;
        if (recover)
        {
            var request = PlanningConstruction.DraftRequest(state, repair: true);
            request.Prompt = request.Prompt.Replace(
                "For a replacement extension, preserve its target scope and accepted requirements; the accepted TaskPlan is immutable.",
                "Correct only the retained tentative extension. Preserve its target scope and accepted requirements; the accepted TaskPlan is immutable.", StringComparison.Ordinal);
            request = PlanningModelCalls.PrepareRequest(state, "replan", request.Prompt, request.StructuredOutputSchema!.AsObject());
            state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "replan", Request = request, CorrectionAccounting = 3 };
            state.ModelCalls++;
            issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            receipt = Wire(request, Reselect(request, 5));
            state = PlannerFixture.Clone(state);
        }
        calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.False(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            return receipt ?? Wire(request, Reselect(request, 5));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.PendingCall);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(calls + (recover ? 0 : 1), state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction.DraftCorrections);
        Assert.Equal(2, state.Construction.Checkpoints.Count);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(Scope, state.Construction.Compact!.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction.Compact.ReplacementRequirements);
        Assert.Equal("in_progress", Assert.Single(state.Construction.Requirements).Status);
    }

    [Theory]
    [InlineData("linked", true)]
    [InlineData("optional", false)]
    [InlineData("unlinked", false)]
    [InlineData("global_evidence_only", false)]
    [InlineData("shared_extraction", false)]
    [InlineData("stale", false)]
    [InlineData("incomplete", false)]
    [InlineData("split_source", true)]
    [InlineData("root_bound", false)]
    [InlineData("no_claim", false)]
    public async Task PublicResultPrerequisiteUsesOnlyCurrentDirectRequiredDeclaration(string variation, bool missing)
    {
        var (state, _) = await Ready();
        var compact = state.Construction!.Compact!;
        var extraction = Assert.Single(compact.InterfaceExtractions!);
        var disposition = Assert.Single(extraction.Dispositions!);
        if (variation == "optional") state.Requirements!.Outputs![0].Required = false;
        if (variation == "unlinked") compact.RequirementSources["req_0"] = [];
        if (variation == "stale") disposition.Version = "stale";
        if (variation == "incomplete") extraction.Complete = false;
        if (variation is "global_evidence_only" or "shared_extraction" or "split_source")
        {
            var original = compact.Sources.Single(source => source.Id == disposition.SourceId);
            var other = new PlanningConstructionSource { Id = "other", Text = "Another exact original declaration.",
                Kind = variation == "split_source" ? "requirement" : "global", InterfaceKind = "output", Indexed = true,
                Version = PlanningGraphCompiler.Fingerprint("Another exact original declaration.") };
            compact.Sources.Add(other);
            if (variation == "split_source") compact.RequirementSources["req_0"] = [other.Id, original.Id];
            else
            {
                disposition.SourceId = other.Id; disposition.Version = other.Version;
                extraction.SourceIds = [other.Id];
                // Corroborating the claimed original is not direct declaration ownership.
                disposition.Evidence = new(StringComparer.Ordinal) { [original.Id] = original.Version, [other.Id] = other.Version };
                if (variation == "shared_extraction")
                {
                    extraction.SourceIds.Add(original.Id);
                    extraction.Dispositions!.Add(new() { SourceId = original.Id, Version = original.Version, Kind = "no_public_interface",
                        Reason = "This original has no public interface.", Evidence = new(StringComparer.Ordinal) { [original.Id] = original.Version } });
                }
            }
        }
        if (variation == "no_claim") compact.CurrentStep!.CloseRequirementIds.Clear();
        if (variation == "root_bound") state.Plan!.Root.Outputs.Add(new("result", NumericArray()));
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = Scope, RequirementIds = ["req_0"], CloseScopes = [Scope] });
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var findings = PlanningCompactConstruction.CandidateFindings(state).ToArray();
        Assert.Equal(missing, findings.Any(finding => finding.Code == "CONSTRUCTION_REQUIREMENT_OUTPUT_MISSING"));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task RootCandidateMayAddTheRequiredResultBeforeClaimingClosure()
    {
        var (state, runtime) = await Ready();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Scope = "/root"; step.OutputNames = ["result"]; step.Inputs.Clear(); step.InputTaskIds.Clear();
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var draft = Draft(request);
            draft["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                { Scope = "/root", RequirementIds = ["req_0"], Outputs = [new("result", NumericArray())] }, PlanningJsonContext.Default.PlanningConstructionExtension);
            return Wire(request, draft);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(PlanningConstruction.LocalPurpose, state.Phase);
        Assert.Empty(state.Plan!.Root.Outputs);
        Assert.Equal("result", Assert.Single(state.Construction!.Candidate!.Plan.Root.Outputs).Name);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(true)]
    public async Task LocalScalarExportRemainsLegalWithoutPublicCompletionClaim(bool clearCompletionClaim)
    {
        var (state, runtime) = await Ready();
        if (clearCompletionClaim) state.Construction!.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        runtime.Respond = (request, _) => Wire(request, Draft(request));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(PlanningConstruction.LocalPurpose, state.Phase);
        Assert.Empty(state.Construction!.Candidate!.Plan.Root.Outputs);
        Assert.Single(state.Construction.Candidate.Plan.Groups[0].Body.Outputs);
        Assert.Equal(0, state.ReplanAttempts);
    }

    private static TaskValue NumericArray() => new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] };

    [Fact]
    public async Task ExplicitPartialRevisionDoesNotInventAPublicClosureClaim()
    {
        var (state, runtime) = await Ready();
        var accepted = PlanningConstruction.Hash(state.Plan);
        const string path = "/tasks/calculate/outputs/result";
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, PreserveRequirements = true,
            ArtifactHash = state.ComputeArtifactHash(), Text = "Increment the accepted producer's input by two instead of one.",
            EditablePaths = [path]
        }, runtime, PlannerFixture.Ct);
        Assert.Equal([path], state.Construction!.Candidate!.AuthorizedPaths);
        Assert.False(state.Construction.Candidate.Extension.Complete);
        Assert.Equal(["req_0"], state.Construction.Compact!.CurrentStep!.CloseRequirementIds);
        var calls = state.ModelCalls;
        var corrections = state.ReplanAttempts;
        // Use the actual revise-created authority with the existing typed-patch
        // API. Source-indexing and subsequent Judge scheduling are separate.
        var authority = PlannerFixture.Clone(state);
        authority.Plan = authority.Construction!.Candidate!.Plan;
        authority.EditablePaths = authority.Construction.Candidate.AuthorizedPaths;
        authority.RevisionScope = authority.EditablePaths!;
        authority.Diagnostics = authority.Construction.Candidate.Diagnostics;
        var request = PlanningConstruction.RepairRequest(state, authority);
        Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
        Assert.Equal("null", request.StructuredOutputSchema["properties"]!["reselectStep"]!["type"]!.ToString());
        var slot = Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!;
        Assert.Equal(path, slot["location"]!.ToString());
        var revised = PlanningRepairPatch.Apply(authority, new RepairPatch { Edits = [new RepairEdit
        {
            Slot = slot["id"]!.ToString(), Action = "replace",
            Value = new JsonObject { ["kind"] = "arithmetic", ["text"] = "add", ["items"] = new JsonArray(
                new JsonObject { ["kind"] = "input", ["source"] = "item" }, new JsonObject { ["kind"] = "number", ["number"] = 2 }) }
        }] }, request, state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal));
        PlanningConstruction.PreserveAccepted(state, revised);
        state.Construction.Candidate.Plan = revised;
        state.Construction.Candidate.Hash = PlanningConstruction.Hash(revised);
        Assert.Empty(PlanningCompactConstruction.CandidateFindings(state));
        Assert.Equal([path], state.Construction.Candidate.AuthorizedPaths);
        Assert.Empty(revised.Root.Outputs);
        Assert.Equal(2, revised.Groups[0].Body.Tasks[0].Outputs[0].Value.Items[1].Number);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal(2, state.Construction.Checkpoints.Count);
    }

    private static async Task<(PlanningSession, TestRuntime)> Ready(bool declaresPublicOutput = true)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone();
            JsonObject response = request.StructuredOutputSchema["properties"]!["clauses"] is not null
                ? new() { ["sourceId"] = source, ["clauses"] = new JsonArray(new JsonObject
                    { ["text"] = Context(request)["sources"]![0]!["text"]!.DeepClone(), ["kind"] = "requirement", ["interfaces"] = declaresPublicOutput ? "output" : "none" }) }
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
        var state = await Advance(PlannerFixture.Capture(new() { Request = new() { TenantId = "requirement-closure",
            Prompt = declaresPublicOutput ? "Apply reusable numeric processing to every item and return the ordered numeric array as result." :
                "Apply reusable numeric processing to every item.",
            Options = new() { ["automatic_correction"] = true } } }), runtime);
        if (declaresPublicOutput) state = await Advance(state, runtime);
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
    private static JsonObject Reselect(LLMRequest request, int _)
    {
        var response = Draft(request); response["extension"] = null;
        response["reselectStep"] = new JsonObject { ["reason"] = Reason, ["disposition"] = "abandon", ["prerequisite"] = null };
        return response;
    }
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
