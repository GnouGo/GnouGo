using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactConvergentJudgeTests
{
    [Fact]
    public async Task ExplicitAcceptedOperationRevisionPreservesIndependentWorkAndAccountingAcrossRestart()
    {
        var state = await ExplicitRevisionState();
        var originalSource = state.Construction!.Compact!.Sources.Single(source => source.Kind == "requirement");
        // Preserve a received clean advisory report on the wrong arguments, then
        // exercise the host's independently authorized correction of accepted work.
        var runtime = new TestRuntime { Respond = (_, _) => Response(Report()) };
        state = await Advance(state, runtime);
        Assert.Single(state.Construction!.Checkpoints);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var independent = JsonSerializer.Serialize(state.Plan!.Root.Tasks[0], PlanningJsonContext.Default.PlanTask);
        var calls = state.ModelCalls;
        var firstReview = Assert.Single(state.Construction.Checkpoints[0].Reviews,
            review => review["purpose"]?.ToString() == PlanningConstruction.LocalPurpose).ToJsonString();
        state.Status = PlanningStatus.Stopped;
        const string path = "/tasks/create/inputs";
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(), PreserveRequirements = true,
            EditablePaths = [path], Text = "Correct only create's inputs to target approved/location and revision head, as originally required."
        }, runtime, PlannerFixture.Ct);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal([path], state.Construction!.Candidate!.AuthorizedPaths);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash);
        var unrelated = PlanningConstruction.Clone(state.Construction.Candidate.Plan);
        unrelated.Root.Tasks[0].Objective = "Unrequested replacement";
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.PreserveAccepted(state, unrelated));

        var desired = PlanningConstruction.Clone(state.Plan!);
        desired.Root.Tasks.Single(task => task.Id == "create").Inputs =
        [new("target", new() { Kind = "string", Text = "approved/location" }), new("revision", new() { Kind = "string", Text = "head" })];
        const string address = "task:create";
        runtime.Respond = (request, purpose) =>
        {
            var context = Context(request);
            if (purpose == PlanningCompactConstruction.IntentPurpose)
            {
                var source = Assert.Single(context["sources"]!.AsArray())!;
                return Response(new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["clauses"] = new JsonArray(new JsonObject
                {
                    ["text"] = source["text"]!.DeepClone(), ["kind"] = "global", ["interfaces"] = "none"
                }) });
            }
            if (purpose == "replan")
            {
                Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
                Assert.Equal("null", request.StructuredOutputSchema["properties"]!["reselectStep"]!["type"]!.ToString());
                var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
                Assert.All(slots, slot => Assert.Equal(path, slot!["location"]!.ToString()));
                var authority = PlannerFixture.Clone(state);
                authority.Plan = authority.Construction!.Candidate!.Plan;
                authority.EditablePaths = [path]; authority.RevisionScope = [path];
                return Response(TestRuntime.PatchResponse(request, authority, desired));
            }
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            if (context["evidence"]?[address] is not JsonObject task)
            {
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), request.StructuredOutputSchema!));
                return Response(EvidenceAction(address));
            }
            var implementation = "/evidence/task:create";
            if (context["defectProgress"] is JsonArray { Count: > 0 } currentDefects)
            {
                implementation = currentDefects[0]!["currentImplementationPointer"]!.ToString();
                Assert.StartsWith("/draft/tasks/", implementation, StringComparison.Ordinal);
                Assert.True(PlanningConstruction.TryPointer(context, implementation, out var current));
                task = Assert.IsType<JsonObject>(current);
                Assert.Equal("wrong/location", context["evidence"]![address]!["inputs"]![0]!["value"]!["text"]!.ToString());
                Assert.Equal("authorized_revision", context["draft"]!["basis"]!.ToString());
                Assert.Equal(path, Assert.Single(context["draft"]!["authorizedPaths"]!.AsArray())!.ToString());
            }
            var inputs = task["inputs"]!.AsArray();
            var corrected = inputs.Any(input => input!["name"]!.ToString() == "revision") &&
                inputs.Single(input => input!["name"]!.ToString() == "target")!["value"]!["text"]!.ToString() == "approved/location";
            var report = Report();
            if (!corrected)
            {
                var finding = Finding(); finding["location"] = "/requirements/0";
                finding["implementation"] = "/evidence/task:create";
                finding["explanation"] = "The accepted operation uses wrong/location and omits revision head, contrary to the exact original requirement.";
                report["action"]!["findings"] = new JsonArray(finding);
            }
            if (context["defectProgress"] is JsonArray { Count: > 0 } defects)
                report["action"]!["resolutions"] = new JsonArray(defects.Select(defect => (JsonNode)new JsonObject
                {
                    ["id"] = defect!["id"]!.DeepClone(), ["status"] = corrected ? "resolved" : "still_present",
                    ["obligation"] = "/requirements/0", ["implementation"] = implementation,
                    ["explanation"] = corrected ? "Both exact authorized input bindings now match the original requirement." : "The original binding defect remains."
                }).ToArray());
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
            return Response(report);
        };
        for (var count = 0; count < 12 && state.Construction!.Checkpoints.Count == 1 && state.Status == PlanningStatus.Generating; count++)
            state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.True(state.Construction!.Checkpoints.Count == 2, string.Join("; ", state.Diagnostics));
        Assert.Equal(PlanningConstruction.Hash(desired), PlanningConstruction.Hash(state.Plan));
        Assert.Equal(independent, JsonSerializer.Serialize(state.Plan!.Root.Tasks[0], PlanningJsonContext.Default.PlanTask));
        Assert.Equal(firstReview, Assert.Single(state.Construction.Checkpoints[0].Reviews,
            review => review["purpose"]?.ToString() == PlanningConstruction.LocalPurpose).ToJsonString());
        Assert.Equal(originalSource.Version, state.Construction.Compact!.Sources.Single(source => source.Id == originalSource.Id).Version);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.True(state.ModelCalls > calls);
        Assert.Null(state.ApprovedHash);
        Assert.Null(state.PendingCall);
        Assert.Contains(runtime.Calls, request => PlanningRepairPatch.Issued(request.StructuredOutputSchema?.AsObject()));
    }

    [Theory]
    [InlineData(6, "changed", "changed")]
    [InlineData(6, "unchanged", "unchanged")]
    [InlineData(6, "unauthorized_objective", "unverified")]
    [InlineData(6, "missing", "unverified")]
    [InlineData(6, "ambiguous", "unverified")]
    [InlineData(5, "changed", "unchanged")]
    public async Task ExplicitRevisionDefectComparisonUsesOnlyUniqueCurrentAuthorizedTask(int presentation, string change, string expected)
    {
        var state = await ExplicitRevisionState();
        state = await Advance(state, new TestRuntime { Respond = (_, _) => Response(Report()) });
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        state.Status = PlanningStatus.Stopped;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(), PreserveRequirements = true,
            EditablePaths = ["/tasks/create/inputs"], Text = "Correct the original target binding."
        }, new TestRuntime(), PlannerFixture.Ct);
        PlanningConstruction.LoadEvidence(state, ["task:create"]);
        var context = PlanningCompactConstruction.Context(state, review: true);
        var finding = Finding(); finding["location"] = "/evidence/task:create/inputs/0";
        finding["evidence"] = new JsonArray("/requirements/0", "/evidence/task:create");
        var witness = PlanningConstruction.DefectWitness(state, context, new JsonArray(finding))!;
        Assert.NotNull(witness);
        var originalWitness = witness.ToJsonString();
        var originalEvidence = state.Construction!.Evidence!.ToJsonString();
        var candidate = state.Construction.Candidate!;
        candidate.Reviews.Add(new() { ["defectWitness"] = witness });
        var task = candidate.Plan.Root.Tasks.Single(task => task.Id == "create");
        if (change == "changed") task.Inputs[0] = new("target", new() { Kind = "string", Text = "approved/location" });
        if (change == "unauthorized_objective") task.Objective = "A different non-executable description outside the authorized input path.";
        if (change == "missing") candidate.Plan.Root.Tasks.Remove(task);
        if (change == "ambiguous") candidate.Plan.Root.Tasks.Add(JsonSerializer.Deserialize(
            JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask), PlanningJsonContext.Default.PlanTask)!);
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        context = PlanningCompactConstruction.Context(state, review: true);
        var progress = Assert.Single(context["defectProgress"]!.AsArray())!;
        Assert.Equal(expected, progress["status"]!.ToString());
        if (presentation == 6 && change is "changed" or "unchanged")
            Assert.Equal("/draft/tasks/0/inputs/0", progress["currentImplementationPointer"]!.ToString());
        else if (presentation == 6) Assert.Null(progress["currentImplementationPointer"]);
        else Assert.Equal("/evidence/task:create/inputs/0", progress["currentImplementationPointer"]!.ToString());
        Assert.Equal(originalEvidence, state.Construction.Evidence.ToJsonString());
        Assert.Equal(originalWitness, witness.ToJsonString());
    }

    private static async Task<PlanningSession> ExplicitRevisionState()
    {
        var state = await DecisionState();
        const string original = "Accept caller string label. Create the resource at approved/location using revision head; retain the label independently.";
        state.Request.Prompt = original;
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Description = original };
        foreach (var source in state.Construction!.Compact!.Sources)
        { source.Text = original; source.Version = PlanningGraphCompiler.Fingerprint(original); }
        var originalSource = state.Construction.Compact.Sources.Single(source => source.Kind == "requirement");
        state.Construction.Compact.InterfaceExtractions![0].Dispositions![0].Version = originalSource.Version;
        state.Construction.Compact.InterfaceExtractions![0].Dispositions![0].Evidence[originalSource.Id] = originalSource.Version;
        var capability = new PlanningCapability
        {
            Id = "create_resource", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "resources", Method = "create", EffectKind = "write",
            Description = "Create the resource at target using the optional revision; return its resource identity.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["target"] = new JsonObject { ["type"] = "string" }, ["revision"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("target"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["resource"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("resource"), ["additionalProperties"] = false }
        };
        if (!state.Catalog!.AllowedStepTypes.Contains("mcp.call", StringComparer.Ordinal)) state.Catalog.AllowedStepTypes.Add("mcp.call");
        state.Catalog.Capabilities.Add(capability);
        state.Discovery.Resolved = [capability];
        var step = state.Construction.Compact.CurrentStep!;
        step.Id = "create"; step.Kind = "operation"; step.OperationIds = [capability.Id];
        step.Objective = "Create the required resource.";
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], Tasks = [new()
            {
                Id = "create", Kind = "operation", Objective = step.Objective, Operation = capability.Id,
                Inputs = [new("target", new() { Kind = "string", Text = "wrong/location" })]
            }]
        });
        return state;
    }

}
