using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class IncrementalReviewTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void GroundedGlobalOnlyClosureCanCompleteWithoutInventingCorrectableLocations(int version)
    {
        var context = new JsonObject
        {
            ["requirements"] = new JsonArray(),
            ["globalConstraints"] = new JsonArray(new JsonObject { ["id"] = "original", ["text"] = "Use deterministic arithmetic only." }),
            ["draft"] = new JsonObject { ["tasks"] = new JsonArray(), ["outputs"] = new JsonArray(), ["closeScopes"] = new JsonArray("/root") },
            ["evidence"] = new JsonObject { ["scope:/root"] = new JsonObject { ["path"] = "/root", ["tasks"] = new JsonArray(
                new JsonObject { ["id"] = "compute", ["kind"] = "value", ["outputs"] = new JsonArray(new JsonObject
                    { ["name"] = "value", ["value"] = new JsonObject { ["kind"] = "number", ["number"] = 2 } }) }) } },
            ["sourceCoverage"] = new JsonObject { ["version"] = version == 9 ? 2 : 1 }
        };
        if (version == 9) context["reviewPropositions"] = new JsonArray();
        var schema = HybridWorkflowPlanner.ConvergentConstructionReviewSchema(context);
        var completed = schema["properties"]!["action"]!["anyOf"]!.AsArray().OfType<JsonObject>()
            .SingleOrDefault(action => action["properties"]!["kind"]!["enum"]![0]!.ToString() == "findings");
        if (version == 8) { Assert.Null(completed); return; }
        Assert.NotNull(completed);
        Assert.Equal(0, completed["properties"]!["findings"]!["maxItems"]!.GetValue<int>());
        var clean = new JsonObject { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() }, ["uncertainty"] = "Runtime unobserved." };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(clean, schema));
        clean["action"]!["findings"]!.AsArray().Add((JsonNode)new JsonObject
            { ["kind"] = "inconsistency", ["location"] = "/invented", ["obligation"] = "/globalConstraints/0", ["implementation"] = "/evidence/scope:~1root",
                ["explanation"] = "No authorized correction location exists.", ["uncertainty"] = "", ["fact"] = null, ["requiredBinding"] = null });
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(clean, schema));
        context["evidence"] = new JsonObject();
        context["taskEvidence"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["address"] = "task:compute" }) };
        var withoutImplementation = HybridWorkflowPlanner.ConvergentConstructionReviewSchema(context);
        Assert.DoesNotContain(withoutImplementation["properties"]!["action"]!["anyOf"]!.AsArray(),
            action => action?["properties"]?["kind"]?["enum"]?[0]?.ToString() == "findings");
    }

    [Theory]
    [InlineData(false, "scaled", 3)]
    [InlineData(false, "shifted", 2)]
    [InlineData(true, "projected", 3)]
    [InlineData(true, "mapped", 2)]
    public Task NormalSessionsReuseExecutablePropositionsWithoutSkippingNewWork(bool group, string prefix, int clauses) =>
        Compare(group, prefix, clauses);

    [Theory]
    [InlineData(false, "scoped", 3, 2)]
    [InlineData(true, "parameterized", 2, 3)]
    public Task WholeOriginalBoundaryBehaviorDoesNotAuthorizeNewCallers(bool group, string prefix, int clauses, int consumers) =>
        Compare(group, prefix, clauses, boundaryLocal: true, consumers);

    [Theory]
    [InlineData(false, 3, 2)]
    [InlineData(true, 3, 2)]
    [InlineData(true, 3, 3)]
    public async Task FreshContributionProofsSaveActualCallsAgainstV9IncludingTheirEstablishment(bool mixed, int clauses, int consumers)
    {
        var prefix = mixed ? "mixed" : "tasklocal";
        var control = await Run(mixed, prefix, clauses, 9, boundaryLocal: true, consumers: consumers, taskLocal: !mixed, mixedPrefix: mixed);
        var candidate = await Run(mixed, prefix, clauses, 10, boundaryLocal: true, consumers: consumers, taskLocal: !mixed, mixedPrefix: mixed);
        foreach (var run in new[] { control, candidate })
        {
            Assert.Equal(PlanningStatus.FinalReview, run.State.Status);
            output.WriteLine($"approval v{run.State.Construction!.Compact!.PresentationVersion}: complete={run.State.Construction.Complete}, open={string.Join(',', run.State.Construction.OpenScopes)}, requirements={string.Join(',', run.State.Construction.Requirements.Select(r => r.Id + ':' + r.Status))}");
            output.WriteLine("reviewed checkpoints: " + string.Join(",", run.State.Construction.Checkpoints.Select(cp => cp.Sequence + ":" +
                PlanningConstructionReviewCoverage.Reviewed(run.State, cp, PlanningConstruction.LocalPurpose) + "/" +
                PlanningConstructionReviewCoverage.Reviewed(run.State, cp, PlanningConstruction.IntermediatePurpose))));
            PlanningArtifactApproval.Verify(run.State);
            Assert.Equal(0, run.State.Construction!.DraftCorrections);
            Assert.All(run.State.Construction.Checkpoints, checkpoint =>
                Assert.True(PlanningConstructionReviewCoverage.Reviewed(run.State, checkpoint, PlanningConstruction.LocalPurpose)));
        }
        Assert.Equal(PlanningConstruction.Hash(control.State.Plan), PlanningConstruction.Hash(candidate.State.Plan));
        var controlManifests = control.State.Construction!.Checkpoints.SelectMany(cp => cp.Reviews)
            .Where(row => row["purpose"]?.ToString() == "construction_source_coverage").ToArray();
        var manifests = candidate.State.Construction!.Checkpoints.SelectMany(cp => cp.Reviews)
            .Where(row => row["purpose"]?.ToString() == "construction_source_coverage").ToArray();
        Assert.Contains(controlManifests, row => row["windows"] is JsonArray { Count: > 1 });
        Assert.DoesNotContain(controlManifests.SelectMany(row => row["reuse"]!.AsArray()),
            row => row?["subject"]?["kind"]?.ToString() == (mixed ? "group" : "task"));
        Assert.Contains(manifests.SelectMany(row => row["reuse"]!.AsArray()),
            row => row?["subject"]?["kind"]?.ToString() == (mixed ? "group" : "task"));
        Assert.All(manifests, manifest =>
        {
            Assert.Equal(manifest["sources"]!.AsObject().Count, manifest["reuseDisposition"]!.AsObject().Count);
            Assert.Equal(manifest["sources"]!.AsObject().Count, manifest["basis"]!["subjectEligibility"]!.AsObject().Count);
        });
        Assert.Contains(manifests.SelectMany(row => row["reuseDisposition"]!.AsObject()), pair => pair.Value?.ToString() == "reused");
        Assert.All(Judges(candidate.Runtime), request => Assert.Contains(Context(request)["reviewPropositions"]!.AsArray(),
            proposition => proposition?["kind"]?.ToString() == "interaction"));
        foreach (var input in new[] { 3, -2 })
        {
            var values = new JsonObject { [prefix + "Input"] = input, [prefix + "Other"] = 7 };
            if (consumers == 3) values[prefix + "ThirdInput"] = 11;
            var before = await Execute(control.State, values, mixed); var after = await Execute(candidate.State, values, mixed);
            Assert.True(JsonNode.DeepEquals(before, after));
            Assert.Equal(input * 2 + 1, after![prefix + "Left"]!.GetValue<double>());
            Assert.Equal(15, after[prefix + "Right"]!.GetValue<double>());
            if (consumers == 3) Assert.Equal(23, after[prefix + "Third"]!.GetValue<double>());
        }
        var oldJudges = Judges(control.Runtime).Count(); var newJudges = Judges(candidate.Runtime).Count();
        output.WriteLine($"fresh10 {prefix}, clauses={clauses}, consumers={consumers}: v9={control.Runtime.Calls.Count}/{oldJudges} Judge, v10={candidate.Runtime.Calls.Count}/{newJudges} Judge; all establishment and retrieval calls included; same artifact={PlanningConstruction.Hash(candidate.State.Plan)}");
        Assert.Equal(control.Runtime.Calls.Count - oldJudges, candidate.Runtime.Calls.Count - newJudges);
        output.WriteLine("v9 windows=" + string.Join(",", controlManifests.Select(row => row["windows"]!.AsArray().Count)));
        output.WriteLine("v10 windows/reuse=" + string.Join(",", manifests.Select(row => row["windows"]!.AsArray().Count + "/" + row["reuse"]!.AsArray().Count)));
        Assert.True(newJudges < oldJudges, $"Judge calls {oldJudges} -> {newJudges}");
        Assert.True(candidate.Runtime.Calls.Count < control.Runtime.Calls.Count,
            $"Total calls {control.Runtime.Calls.Count} -> {candidate.Runtime.Calls.Count}");
    }

    [Fact]
    public async Task LargerTaskLocalControlRetainsTheCapturedCallCapWithoutAnEfficiencyClaim()
    {
        var control = await Run(false, "tasklocal", 2, 9, expectStop: true, boundaryLocal: true,
            consumers: 3, taskLocal: true);
        Assert.Equal(PlanningStatus.Stopped, control.State.Status);
        Assert.Contains(control.State.Diagnostics, diagnostic => diagnostic.Required && diagnostic.Code == "CONSTRUCTION_REVIEW_ALLOWANCE");
        Assert.Equal(64, control.State.Request.MaxModelCalls);
        Assert.Equal(64, control.Runtime.Calls.Count);
        Assert.False(control.State.Construction!.Complete);
        Assert.Null(control.State.Yaml);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(control.State));
        output.WriteLine("Retained v9 task-local control: clauses=2, consumers=3, navigation=12000, draft=18000, review=10000, calls=64; allowance refusal before final artifact. No completed comparison or savings claim.");
    }

    [Theory]
    [InlineData("opaque_objective")]
    [InlineData("invalidation")]
    [InlineData("unreviewed_closure")]
    public async Task MixedHistoricalReceiptsDoNotAuthorizeChangedContextOrUnreviewedClosure(string change)
    {
        var run = await Run(true, "guardedmixed", 1, 10, boundaryLocal: true, mixedPrefix: true);
        PlanningArtifactApproval.Verify(run.State);
        var state = PlannerFixture.Clone(run.State);
        if (change == "opaque_objective") state.Plan!.Root.Tasks[0].Objective += " Changed interpretation.";
        else if (change == "unreviewed_closure") state.Construction!.Checkpoints[^1].Reviews.Clear();
        else
        {
            var reuse = state.Construction!.Checkpoints.SelectMany(cp => cp.Reviews)
                .Where(row => row["purpose"]?.ToString() == "construction_source_coverage")
                .SelectMany(row => row["reuse"]!.AsArray()).First(row => row?["subject"]?["kind"]?.ToString() == "group")!;
            state.Construction.Checkpoints.Single(cp => cp.Sequence == reuse["checkpoint"]!.GetValue<int>())
                .InvalidatedTaskIds.Add("guardedmixedOpaque");
        }
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    private async Task Compare(bool group, string prefix, int clauses, bool boundaryLocal = false, int consumers = 2)
    {
        var control = await Run(group, prefix, clauses, 8, boundaryLocal: boundaryLocal, consumers: consumers);
        var candidate = await Run(group, prefix, clauses, 9, boundaryLocal: boundaryLocal, consumers: consumers);
        Assert.Equal(PlanningStatus.FinalReview, control.State.Status);
        Assert.Equal(PlanningStatus.FinalReview, candidate.State.Status);
        PlanningArtifactApproval.Verify(control.State);
        PlanningArtifactApproval.Verify(candidate.State);
        var oldJudges = Judges(control.Runtime).Count(); var newJudges = Judges(candidate.Runtime).Count();
        output.WriteLine($"{prefix}: control={control.Runtime.Calls.Count} ({oldJudges} Judge); candidate={candidate.Runtime.Calls.Count} ({newJudges} Judge)");
        output.WriteLine("windows: " + string.Join(",", candidate.State.Construction!.Checkpoints.Select(cp => cp.Reviews.LastOrDefault(r => r["purpose"]?.ToString() == "construction_source_coverage") is { } m ? cp.Sequence + ":" + m["windows"]!.AsArray().Count + "/reuse" + (m["reuse"] as JsonArray)?.Count : "none")));
        Assert.Equal(control.State.Plan is null ? null : PlanningConstruction.Hash(control.State.Plan), PlanningConstruction.Hash(candidate.State.Plan));
        Assert.Equal(0, candidate.State.ReplanAttempts);
        Assert.Equal(0, candidate.State.Construction!.DraftCorrections);
        Assert.All(candidate.State.Construction.Checkpoints, checkpoint => Assert.True(PlanningConstructionReviewCoverage.Reviewed(candidate.State, checkpoint, PlanningConstruction.LocalPurpose)));
        var manifests = candidate.State.Construction.Checkpoints.SelectMany(checkpoint => checkpoint.Reviews)
            .Where(row => row["purpose"]?.ToString() == "construction_source_coverage").ToArray();
        Assert.Contains(manifests, row => row["reuse"] is JsonArray { Count: > 0 });
        Assert.Contains(manifests, row => row["windows"] is JsonArray { Count: > 1 });
        if (boundaryLocal) Assert.Contains(manifests.SelectMany(manifest => manifest["reuse"]!.AsArray()),
            row => row?["subject"]?["kind"]?.ToString() == (group ? "group" : "scope"));
        Assert.All(Judges(candidate.Runtime), request =>
        {
            var context = Context(request);
            Assert.Contains(context["reviewPropositions"]!.AsArray(), proposition => proposition?["kind"]?.ToString() == "interaction");
            Assert.NotEmpty(context["requirements"]!.AsArray().Concat(context["globalConstraints"]!.AsArray()));
        });
        foreach (var inputs in new[] { new JsonObject { [prefix + "Input"] = 3, [prefix + "Other"] = 7 },
                     new JsonObject { [prefix + "Input"] = -2, [prefix + "Other"] = 0 } })
        {
            if (consumers == 3) inputs[prefix + "ThirdInput"] = 11;
            var before = await Execute(control.State, inputs); var after = await Execute(candidate.State, inputs);
            Assert.True(JsonNode.DeepEquals(before, after));
            Assert.Equal(inputs[prefix + "Input"]!.GetValue<int>() * 2 + 1, after![prefix + "Left"]!.GetValue<double>());
            Assert.Equal(inputs[prefix + "Other"]!.GetValue<int>() * 2 + 1, after[prefix + "Right"]!.GetValue<double>());
            if (consumers == 3) Assert.Equal(23, after[prefix + "Third"]!.GetValue<double>());
        }
        foreach (var request in Judges(candidate.Runtime))
        {
            var context = Context(request);
            var alternative = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
            var finding = alternative.StructuredOutputSchema!["properties"]!["action"]!["anyOf"]!.AsArray().OfType<JsonObject>()
                .SingleOrDefault(action => action["properties"]!["kind"]!["enum"]![0]!.ToString() == "findings");
            if (finding is null) continue;
            finding["properties"]!["acknowledgments"]!["items"] = new JsonObject { ["anyOf"] = new JsonArray(
                context["reviewPropositions"]!.AsArray().OfType<JsonObject>().Select(row => (JsonNode)PlanningSchemas.Object(
                    ("id", PlanningSchemas.Enum(row["id"]!.ToString())), ("applicability", row["subject"] is JsonObject
                        ? PlanningSchemas.Enum("subject", "broad") : PlanningSchemas.Enum("broad")))).ToArray()) };
            var grouped = PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose, 10_000);
            var repeated = PlanningRequestMeasurements.Measure(alternative, PlanningConstruction.LocalPurpose, 10_000);
            Assert.True(grouped.CompleteRequestBytes <= repeated.CompleteRequestBytes);
            output.WriteLine($"ack schema encoding: {repeated.CompleteRequestBytes}->{grouped.CompleteRequestBytes} complete bytes");
        }
        if (boundaryLocal ? group : clauses > 2)
        {
            Assert.Contains(control.State.Construction!.Checkpoints.SelectMany(checkpoint => checkpoint.Reviews),
                row => row["purpose"]?.ToString() == "construction_source_coverage" && row["windows"] is JsonArray { Count: > 1 });
            Assert.True(newJudges < oldJudges, $"Expected strictly fewer Judge calls; {oldJudges} -> {newJudges}.");
            Assert.True(candidate.Runtime.Calls.Count < control.Runtime.Calls.Count);
        }
        else if (!boundaryLocal)
        {
            // The one-window control is already at the mandatory-review floor.
            // Retain its observed overhead; reuse promises no universal savings.
            Assert.Equal(control.State.Construction!.Checkpoints.Count, oldJudges);
            Assert.All(control.State.Construction.Checkpoints.SelectMany(checkpoint => checkpoint.Reviews)
                .Where(row => row["purpose"]?.ToString() == "construction_source_coverage"),
                row => Assert.Single(row["windows"]!.AsArray()));
            Assert.True(newJudges >= candidate.State.Construction.Checkpoints.Count);
        }
        else Assert.True(newJudges >= candidate.State.Construction.Checkpoints.Count);
    }

    [Theory]
    [InlineData("missing_ack")]
    [InlineData("duplicate_ack")]
    [InlineData("unknown_id")]
    [InlineData("global_subject")]
    public async Task IssuedAcknowledgmentsCannotOmitOrInventApplicability(string defect)
    {
        var observed = false;
        var result = await Run(false, "checked", 1, 9, (context, report) =>
        {
            observed = true;
            var acknowledgments = report["action"]!["acknowledgments"]!.AsArray();
            if (defect == "missing_ack") acknowledgments.RemoveAt(0);
            if (defect == "duplicate_ack") acknowledgments[^1] = acknowledgments[0]!.DeepClone();
            if (defect == "unknown_id") acknowledgments[0]!["id"] = "proposition:invented";
            if (defect == "global_subject") acknowledgments.Last(row => context["reviewPropositions"]!.AsArray()
                .Any(proposition => proposition?["id"]?.ToString() == row?["id"]?.ToString() && proposition?["subject"] is null))!["applicability"] = "subject";
        }, expectStop: true);
        Assert.True(observed);
        Assert.Empty(result.State.Construction!.Checkpoints);
        Assert.NotEqual(PlanningStatus.FinalReview, result.State.Status);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(result.State));
    }

    [Theory]
    [InlineData("inconclusive", 9)]
    [InlineData("report_repair", 9)]
    [InlineData("inconclusive", 10)]
    [InlineData("report_repair", 10)]
    public async Task SupportedFollowupRetainsItsPriorReportAndCompletesTheVerifiedUnion(string kind, int version)
    {
        var issued = false;
        var run = await Run(false, "followup", 1, version, (context, report) =>
        {
            if (issued || kind == "inconclusive" && !(context["interfaces"] is JsonArray interfaces &&
                interfaces.Any(item => item?["binding"]?["kind"]?.ToString() == "output"))) return;
            issued = true;
            report["action"] = kind == "report_repair" ? null : new JsonObject
                { ["kind"] = "inconclusive", ["reason"] = "The exact current scope export declaration is needed to finish this review." };
        });
        Assert.True(issued);
        Assert.Equal(PlanningStatus.FinalReview, run.State.Status);
        if (kind == "report_repair")
        {
            var repaired = Assert.Single(Judges(run.Runtime), request => Context(request)["reportRepair"] is not null);
            Assert.Contains(PlanningConstructionReviewCoverage.IncrementalInstructions, repaired.Prompt, StringComparison.Ordinal);
        }
        Assert.All(Judges(run.Runtime), request =>
            Assert.Equal(version == 10, request.Prompt.Contains("A claim concerning an original assigned to another window does not by itself require that original here.", StringComparison.Ordinal)));
        PlanningArtifactApproval.Verify(run.State);
        var prior = Assert.Single(run.State.Construction!.Checkpoints.SelectMany(checkpoint => checkpoint.Reviews),
            review => review["reassessment"] is not null);
        Assert.Equal("issued", prior["reassessment"]!.ToString());
        Assert.All(run.State.Construction.Checkpoints, checkpoint =>
            Assert.True(PlanningConstructionReviewCoverage.Reviewed(run.State, checkpoint, PlanningConstruction.LocalPurpose)));
    }

    [Theory]
    [InlineData("requestHash")]
    [InlineData("rawResponse")]
    [InlineData("duplicateMeasurement")]
    public async Task ApprovalRejectsChangedOrAmbiguousOriginalReceiptProvenance(string mutation)
    {
        var run = await Run(false, "provenance", 1, 9);
        PlanningArtifactApproval.Verify(run.State);
        var checkpoint = run.State.Construction!.Checkpoints[0];
        var receipt = checkpoint.Reviews.First(review => review["sourceRequestHash"] is not null);
        if (mutation == "requestHash") receipt["sourceRequestHash"] = new string('a', 64);
        if (mutation == "rawResponse") receipt["rawResponse"]!["uncertainty"] = "Changed after receipt.";
        if (mutation == "duplicateMeasurement") run.State.RequestMeasurements!.Add(run.State.RequestMeasurements.Single(
            measurement => measurement.RequestId == receipt["sourceRequestId"]!.ToString()));
        Assert.False(PlanningConstructionReviewCoverage.Reviewed(run.State, checkpoint, PlanningConstruction.LocalPurpose));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(run.State));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("proof")]
    [InlineData("new_global")]
    public async Task StaleGenuineReuseRestoresFreshWholeOriginalCoverage(string mutation)
    {
        var run = await Run(false, "stale", 1, 9);
        var state = ReusePending(run);
        var frozen = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var original = state.Construction!.Candidate!.Reviews.Last(row => row["purpose"]?.ToString() == "construction_source_coverage");
        Assert.NotEmpty(original["reuse"]!.AsArray());
        if (mutation == "new_global") state.Request.Policy.Instructions += "All public numeric outputs must be non-negative for every supplied input.";
        else foreach (var checkpoint in state.Construction.Checkpoints)
            if (mutation == "receipt")
                foreach (var receipt in checkpoint.Reviews.Where(row => row["sourceRequestHash"] is not null)) receipt["sourceRequestHash"] = new string('a', 64);
            else
                foreach (var manifest in checkpoint.Reviews.Where(row => row["purpose"]?.ToString() == "construction_source_coverage"))
                    foreach (var proposition in manifest["basis"]!["propositions"]!.AsArray().OfType<JsonObject>())
                        if (proposition["dependencyProof"] is JsonObject proof) proof["authority"] = new string('a', 64);
        Assert.False(PlanningConstructionReviewCoverage.Complete(state, state.Construction.Candidate));
        Assert.True(PlanningConstructionReviewCoverage.RemainingCalls(state) > 0);
        var (context, _) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        var fresh = state.Construction.Candidate.Reviews.Last(row => row["purpose"]?.ToString() == "construction_source_coverage");
        Assert.Empty(fresh["reuse"]!.AsArray());
        Assert.Equal(fresh["sources"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal), fresh["windows"]!.AsArray()
            .SelectMany(window => window!.AsArray()).Select(key => key!.ToString()).Order(StringComparer.Ordinal));
        if (mutation == "new_global") Assert.Contains("non-negative", context["instructions"]!.ToString(), StringComparison.Ordinal);
        // Inspecting fresh admission must not rewrite a request already issued.
        Assert.Equal(frozen, JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task IssuedReviewRecoversExactEnvelopeAndGenuineReceiptBeforeAcceptance(int version)
    {
        var run = await Run(false, "recovery", 1, version);
        var state = version >= 9 ? ReusePending(run) : PlannerFixture.Clone(run.Runtime.Checkpoints.First(saved =>
            saved.Status == PlanningStatus.Generating && saved.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        var pending = state.PendingCall!; var request = JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest);
        var context = state.Construction!.PendingReviewContext!.ToJsonString(); var candidateHash = state.Construction.Candidate!.Hash;
        var receipt = run.Responses[pending.Request.ClientRequestId!];
        var accepted = run.Runtime.Checkpoints.First(saved => saved.PendingCall is null &&
            saved.Construction!.Checkpoints.LastOrDefault()?.PlanHash == candidateHash);
        var physicalSends = 0;
        var runtime = new TestRuntime { Respond = (issued, purpose) =>
        {
            Assert.Equal(request, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(context, Context(issued).ToJsonString());
            if (!run.Responses.ContainsKey(issued.ClientRequestId!)) physicalSends++;
            return receipt;
        } };
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(0, physicalSends); Assert.Single(runtime.Calls);
        Assert.Equal(accepted.ModelCalls, recovered.ModelCalls);
        Assert.Equal(candidateHash, PlanningConstruction.Hash(recovered.Plan));
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(recovered, recovered.Construction!.Checkpoints[^1], PlanningConstruction.LocalPurpose));
        Assert.Null(recovered.PendingCall);
    }

    private static PlanningSession ReusePending(Result run) => PlannerFixture.Clone(run.Runtime.Checkpoints.First(saved =>
        saved.Status == PlanningStatus.Generating && saved.PendingCall?.Purpose == PlanningConstruction.LocalPurpose &&
        saved.Construction!.Candidate?.Reviews.Any(row => row["purpose"]?.ToString() == "construction_source_coverage" &&
            row["reuse"] is JsonArray { Count: > 0 }) == true));

    [Fact]
    public async Task ReceivedUnresolvedFindingRetainsItsWitnessAndDisablesReuse()
    {
        var issued = false;
        var run = await Run(false, "witness", 1, 9, (context, response) =>
        {
            if (issued || context["reusedPropositions"] is not JsonArray { Count: > 0 } ||
                context["draft"]?["tasks"] is not JsonArray { Count: > 0 }) return;
            issued = true;
            // A received advisory finding is not host-established truth. Its
            // actual issued source and implementation must remain unresolved.
            response["action"]!["findings"]!.AsArray().Add((JsonNode)new JsonObject
            {
                ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0", ["obligation"] = "/globalConstraints/0",
                ["implementation"] = "/draft/tasks/0", ["explanation"] = "Review this consumer against the exact original before accepting it.",
                ["uncertainty"] = "This advisory finding requires scoped resolution.", ["fact"] = null, ["requiredBinding"] = null
            });
        }, expectStop: true, stopOnAdverse: true);
        Assert.True(issued);
        var candidate = run.State.Construction!.Candidate!;
        Assert.True(PlanningConstruction.UnresolvedDefects(candidate));
        var original = candidate.Reviews.Single(row => row["outcome"]?.ToString() == "revision_required");
        var retained = original.ToJsonString();
        Assert.False(PlanningConstructionReviewCoverage.Complete(run.State, candidate));
        var (context, _) = HybridWorkflowPlanner.CreateConstructionReviewRequest(run.State, intermediate: false);
        Assert.Empty(context["reusedPropositions"]!.AsArray());
        Assert.Equal(retained, original.ToJsonString());
        Assert.DoesNotContain(run.State.Construction.Checkpoints, checkpoint => checkpoint.PlanHash == candidate.Hash);
    }

    internal static JsonObject Review(JsonObject context, bool local = false)
    {
        var action = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() };
        if (context["reviewPropositions"] is JsonArray propositions)
            action["acknowledgments"] = new JsonArray(propositions.OfType<JsonObject>().Select(proposition => (JsonNode)new JsonObject
            {
                ["id"] = proposition["id"]!.DeepClone(), ["applicability"] = local && proposition["subject"] is JsonObject ? "subject" : "broad"
            }).ToArray());
        return new JsonObject { ["action"] = action, ["uncertainty"] = "Runtime remains unobserved; only the issued claims were reviewed." };
    }

    private sealed record Step(string Kind, string Scope, string[] Requirements, string[] CloseRequirements,
        TaskValue[] Inputs, string[] OutputNames, PlanningConstructionExtension Extension, string? Group = null);
    private sealed record Result(PlanningSession State, TestRuntime Runtime, IReadOnlyDictionary<string, LLMResponse> Responses);

    private async Task<Result> Run(bool group, string prefix, int clauseCount, int version,
        Action<JsonObject, JsonObject>? alterReview = null, bool expectStop = false, bool boundaryLocal = false, int consumers = 2, bool stopOnAdverse = false,
        bool taskLocal = false, bool mixedPrefix = false)
    {
        var left = prefix + "Left"; var right = prefix + "Right"; var input = prefix + "Input"; var other = prefix + "Other";
        var pairs = new[] { (Name: left, Source: input), (Name: right, Source: other), (Name: prefix + "Third", Source: prefix + "ThirdInput") }.Take(consumers).ToArray();
        var clauses = new List<(string Text, string Kind, string Direction)> { ($"Accept required numeric caller inputs {string.Join(", ", pairs.Select(pair => pair.Source))}.\n\n", "global", "input") };
        var localOriginals = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, source) in boundaryLocal && group ? pairs.Take(1) : pairs)
            for (var index = 0; index < clauseCount; index++)
            {
                if (!boundaryLocal)
                    clauses.Add(($"For public numeric output {name}, compute twice {source} plus one. Clause {index} concerns this numeric output only. " +
                        string.Concat(Enumerable.Range(0, 8).Select(n => $"For case {index}-{n}, preserve the supplied operand and perform multiplication before the final addition. ")) + "\n\n", "requirement", "output"));
                else
                {
                    var subject = taskLocal ? new JsonObject { ["kind"] = "task", ["id"] = name + "Producer" }
                        : group ? new JsonObject { ["kind"] = "group", ["id"] = prefix + "Function" }
                        : new JsonObject { ["kind"] = "scope", ["path"] = "/tasks/" + name + "Scope/body" };
                    var text = taskLocal
                        ? $"The intermediate calculation {name}Producer computes numeric value equal to twice input {source} plus one, using multiplication followed by addition. This clause defines only that computation, independently of its later consumers and public return. Numeric conformance block {index}: "
                        : group
                        ? $"The reusable group {prefix}Function has one required numeric parameter operand and exports value equal to operand multiplied by two. This clause defines that group's parameterized behavior, independently of any caller. Numeric conformance block {name}-{index}: "
                        : $"The executable sequence {name}Scope computes from numeric input {source} and exports value equal to twice that input plus one. This clause defines only this boundary's value; its public return is specified separately. Numeric conformance block {index}: ";
                    text += string.Concat(Enumerable.Range(index * 8 - 8, 8).Select(n =>
                        $"At operand {n}, its exported value must equal {n * 2 + (group ? 0 : 1)} after multiplying the supplied operand by two{(group ? "" : " and adding one")}. ")) + "\n\n";
                    clauses.Add((text, "requirement", "none")); localOriginals.Add(text, subject);
                }
            }
        if (boundaryLocal)
            foreach (var (name, source) in pairs)
                clauses.Add(($"Return required public numeric output {name} equal to twice caller input {source} plus one, using the specified {(taskLocal ? "intermediate calculation" : group ? "parameterized group" : "executable boundary")}.\n\n", "requirement", "output"));
        clauses.Add((mixedPrefix
            ? "First interpret the supplied literal seven into a numeric value exactly once. Keep this inference in the sequential root prefix; subsequent deterministic computations must not consume its result. No other inference or external effects are permitted.\n\n"
            : "Throughout the workflow, use only deterministic value computation; no external effects or runtime inference.\n\n", "global", "none"));
        var state = new PlanningSession { Request = new() { TenantId = "incremental-coverage", SessionId = "proof-" + prefix,
            Prompt = string.Concat(clauses.Select(clause => clause.Text)), MaxModelCalls = 64,
            Options = new JsonObject { ["automatic_correction"] = true } } };
        PlanningSessionCreation.Capture(state);
        if (version == 10) Assert.Equal(10, state.Construction!.Compact!.PresentationVersion);
        else { state.Construction!.Compact!.PresentationVersion = version; state.Request.Options["construction_presentation_version"] = version; }
        // Both arms retain the same captured ceilings; only presentation differs.
        state.Construction.Limits.PhasePolicy!.Navigation.MaxInputTokens = 12_000;
        state.Construction.Limits.PhasePolicy.Draft.MaxInputTokens = 18_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["navigation"]!["maxInputTokens"] = 12_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"] = 18_000;
        state.Construction.Limits.PhasePolicy.Review.MaxInputTokens = 10_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["review"]!["maxInputTokens"] = 10_000;
        var leftRequirements = Enumerable.Range(0, clauseCount).Select(index => "req_" + index).ToArray();
        var rightRequirements = Enumerable.Range(clauseCount, clauseCount).Select(index => "req_" + index).ToArray();
        var steps = new List<Step>();
        TaskValue Input(string name) => new() { Kind = "input", Source = name };
        TaskValue Ref(string id) => new() { Kind = "output", Source = id, Port = "value" };
        PlanTask Value(string id, TaskValue value) => new() { Id = id, Kind = "value", Objective = "Compute the exact declared numeric result.", Outputs = [new("value", value)] };
        if (mixedPrefix) steps.Add(new("transform", "/root", leftRequirements, [], [], [], new()
        {
            RequirementIds = leftRequirements.ToList(), Tasks = [new() { Id = prefix + "Opaque", Kind = "transform", Mode = "interpret",
                Objective = "Return the exact supplied seven as numeric value without using external observations.",
                Inputs = [new("source", Number(7))], ResultType = new() { Kind = "object", Fields = [new() { Name = "value", Type = new() { Kind = "number" } }] } }]
        }));
        if (group)
        {
            var id = prefix + "Function"; var parameter = "operand";
            steps.Add(new("group", "/root", leftRequirements, [], [], [], new() { RequirementIds = leftRequirements.ToList(),
                Groups = [new() { Id = id, Inputs = [new() { Name = parameter, Type = new() { Kind = "number" } }] }] }, id));
            var scope = "/groups/" + id + "/body";
            steps.Add(new("value", scope, leftRequirements, [], [Input(parameter)], [], new() { Scope = scope,
                RequirementIds = leftRequirements.ToList(), Tasks = [Value(prefix + "Multiply", Formula("multiply", Input(parameter), Number(2)))],
                Outputs = [new("value", Ref(prefix + "Multiply"))], CloseScopes = [scope] }));
            foreach (var (name, source) in pairs)
            {
                var suffix = name[prefix.Length..]; var call = prefix + "Call" + suffix; var finish = prefix + "Finish" + suffix;
                steps.Add(new("call", "/root", leftRequirements, [], [Input(source)], [], new() { RequirementIds = leftRequirements.ToList(),
                    Tasks = [new() { Id = call, Kind = "call", Objective = name == left ? "Evaluate the declared multiplication." : "Evaluate the independent declared multiplication.", Group = id,
                        Inputs = [new(parameter, Input(source))] }] }, id));
                steps.Add(new("value", "/root", leftRequirements, [], [Ref(call)], [name], new() { RequirementIds = leftRequirements.ToList(),
                    Tasks = [Value(finish, Formula("add", Ref(call), Number(1)))], Outputs = [new(name, Ref(finish))] }));
            }
        }
        else if (taskLocal) foreach (var (name, source) in pairs)
        {
            var requirements = name == left ? leftRequirements : rightRequirements; var producer = name + "Producer";
            steps.Add(new("value", "/root", requirements, [], [Input(source)], [], new() { RequirementIds = requirements.ToList(),
                Tasks = [Value(producer, Formula("add", Formula("multiply", Input(source), Number(2)), Number(1)))] }));
            steps.Add(new("value", "/root", requirements, [], [Ref(producer)], [name], new() { RequirementIds = requirements.ToList(),
                Tasks = [Value(name + "Return", Ref(producer))], Outputs = [new(name, Ref(name + "Return"))] }));
        }
        else foreach (var (name, source) in pairs)
        {
            var requirements = name == left ? leftRequirements : rightRequirements;
            var producer = name + "Producer"; var consumer = name + "Consumer"; var boundary = name + "Scope";
            var scope = "/tasks/" + boundary + "/body";
            steps.Add(new("sequence", "/root", requirements, [], [], [], new() { RequirementIds = requirements.ToList(),
                Tasks = [new() { Id = boundary, Kind = "sequence", Objective = "Keep this declared arithmetic pipeline in its own executable boundary.", Body = new() }] }));
            steps.Add(new("value", scope, requirements, [], [Input(source)], [], new() { Scope = scope, RequirementIds = requirements.ToList(),
                Tasks = [Value(producer, Formula("multiply", Input(source), Number(2))), Value(consumer, Formula("add", Ref(producer), Number(1)))],
                Outputs = [new("value", Ref(consumer))], CloseScopes = [scope] }));
            steps.Add(new("value", "/root", requirements, [], [Ref(boundary)], [name], new() { RequirementIds = requirements.ToList(),
                Tasks = [Value(name + "Return", Ref(boundary))], Outputs = [new(name, Ref(name + "Return"))] }));
        }
        // Associations are provisional; every Judge still sees all original units.
        steps = steps.Select(step => step with { Requirements = ["req_0"], Extension = Associate(step.Extension) }).ToList();
        PlanningConstructionExtension Associate(PlanningConstructionExtension extension) { extension.RequirementIds = ["req_0"]; return extension; }
        var next = 0;
        var responses = new Dictionary<string, LLMResponse>(StringComparer.Ordinal);
        Exception? responseError = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
            var context = Context(request); JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
            {
                if (request.StructuredOutputSchema!["properties"]!["clauses"] is not null)
                {
                    var source = context["sources"]![0]!; var text = source["text"]!.ToString();
                    var pieces = new List<(string Text, string Kind, string Direction)>();
                    var position = 0;
                    while (position < text.Length)
                    {
                        var end = text.IndexOf("\n\n", position, StringComparison.Ordinal); end = end < 0 ? text.Length : end + 2;
                        var part = text[position..end]; var original = clauses.First(clause => clause.Text.Contains(part, StringComparison.Ordinal));
                        pieces.Add((part, original.Kind, original.Direction)); position = end;
                    }
                    response = new() { ["sourceId"] = source["id"]!.DeepClone(), ["clauses"] = new JsonArray(pieces.Select(piece => (JsonNode)new JsonObject
                    { ["text"] = piece.Text, ["kind"] = piece.Kind, ["interfaces"] = piece.Direction }).ToArray()) };
                }
                else
                {
                    var sources = context["sources"]!.AsArray();
                    string Original(JsonNode source) => clauses.First(clause => clause.Text.Contains(source["text"]!.ToString(), StringComparison.Ordinal)).Text;
                    var declaredInputs = sources.Any(source => Original(source!).Contains("caller inputs", StringComparison.Ordinal)) ? pairs.Select(pair => pair.Source).ToArray() : [];
                    var declaredOutputs = pairs.Select(pair => pair.Name).Where(name => sources.Any(source => Original(source!).Contains("output " + name, StringComparison.Ordinal))).ToArray();
                    response = new() { ["sourceId"] = sources[0]!["id"]!.DeepClone(), ["interfaces"] = new JsonObject
                    {
                        ["inputs"] = Declarations(declaredInputs), ["outputs"] = Declarations(declaredOutputs),
                        ["dispositions"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject
                        {
                            ["sourceId"] = source!["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(), ["kind"] = "interfaces",
                            ["inputs"] = Strings(Original(source).Contains("caller inputs", StringComparison.Ordinal) ? declaredInputs : []),
                            ["outputs"] = Strings(declaredOutputs.Where(name => Original(source).Contains("output " + name, StringComparison.Ordinal))),
                            ["reason"] = "The exact original names this caller/result declaration.", ["evidence"] = new JsonArray(new JsonObject
                                { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() })
                        }).ToArray())
                    }, ["evidenceRequests"] = null, ["clarifications"] = null };
                }
            }
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var allowed = RequirementIds(request.StructuredOutputSchema!, request.StructuredOutputSchema!).ToHashSet(StringComparer.Ordinal);
                var remaining = state.Construction!.Requirements.Where(requirement => requirement.Status != "closed").Select(requirement => requirement.Id).ToArray();
                var closing = remaining.Where(allowed.Contains).Take(4).ToArray();
                var scopeClosure = closing.Length == remaining.Length ? new[] { "/root" } : [];
                var step = next < steps.Count ? steps[next] : new Step("close", "/root", closing, closing, [], [],
                    new() { RequirementIds = closing.ToList(), CloseScopes = scopeClosure.ToList(), Complete = scopeClosure.Length > 0 });
                if (step.Requirements.Any(id => !allowed.Contains(id)))
                {
                    var address = context["nextRequirements"]?.ToString() ?? throw new InvalidOperationException("No offered requirement continuation.");
                    response = new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(),
                        ["step"] = null, ["evidenceRequests"] = new JsonArray(address), ["clarifications"] = null };
                }
                else
                {
                response = new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["step"] = new JsonObject
                {
                    ["scope"] = step.Scope, ["kind"] = step.Kind, ["objective"] = "Construct the exact declared numerical behavior.",
                    ["requirementIds"] = Strings(step.Requirements), ["inputs"] = new JsonArray(step.Inputs.Select(value => (JsonNode)(value.Kind == "input"
                        ? new JsonObject { ["kind"] = "input", ["source"] = value.Source }
                        : new JsonObject { ["kind"] = "output", ["source"] = value.Source, ["port"] = value.Port })).ToArray()),
                    ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = step.Kind == "close" ? new JsonArray() : new JsonArray("value"), ["groupId"] = step.Kind == "group" ? null : step.Group,
                    ["operationIds"] = new JsonArray(),
                    ["closeRequirementIds"] = Strings(step.CloseRequirements), ["closeScopes"] = step.Kind == "close" ? Strings(step.Extension.CloseScopes) : null,
                    ["outputNames"] = Strings(step.OutputNames), ["prerequisites"] = null
                }, ["evidenceRequests"] = null, ["clarifications"] = null };
                if (step.Kind == "close") next++;
                }
            }
            else if (purpose == PlanningConstruction.DraftPurpose)
            {
                var extension = steps[next++].Extension;
                response = new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["requirements"] = null, ["commitments"] = null,
                    ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension), ["discoveryRequests"] = null,
                    ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null };
            }
            else if (purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose)
            {
                var actions = request.StructuredOutputSchema!["properties"]!["action"]!["anyOf"]!.AsArray();
                if (!actions.Any(action => action?["properties"]?["kind"]?["enum"]?[0]?.ToString() == "findings"))
                {
                    output.WriteLine("Review with incomplete grounding domains: " + context.ToJsonString());
                    var evidenceAction = actions.Single(action => action?["properties"]?["kind"]?["enum"]?[0]?.ToString() == "evidenceRequests")!;
                    var addresses = evidenceAction["properties"]!["addresses"]!["items"]!["enum"]!.AsArray();
                    var address = addresses.Select(value => value!.ToString()).FirstOrDefault(value => value == "scope:/root")
                        ?? addresses.Select(value => value!.ToString()).FirstOrDefault(value => value.StartsWith("task:", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("No offered concrete implementation address: " + addresses.ToJsonString());
                    response = new() { ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) },
                        ["uncertainty"] = "Need the exact accepted implementation to assess this closure." };
                }
                else
                {
                    response = Review(context, local: true);
                    // These originals require public caller results. The group's
                    // multiplication or a closed scope alone does not establish
                    // the caller binding, final addition or public return.
                    if (context["reviewPropositions"] is JsonArray propositions)
                        foreach (var acknowledgment in response["action"]!["acknowledgments"]!.AsArray())
                        {
                            var proposition = propositions.Single(proposition => proposition?["id"]?.ToString() == acknowledgment!["id"]!.ToString())!;
                            if (proposition["subject"]?["kind"]?.ToString() == "output") continue;
                            acknowledgment!["applicability"] = "broad";
                            var requirement = context["requirements"]!.AsArray().SingleOrDefault(row => "requirement:" + row!["id"]!.ToString() == proposition["sourceKey"]?.ToString());
                            if (requirement?["sources"] is not JsonArray { Count: > 0 } sources) continue;
                            var originals = sources.Select(source => state.Construction!.Compact!.Sources.Single(item => item.Id == source!["id"]!.ToString()).Text)
                                .Select(text => localOriginals.Where(pair => pair.Key.Contains(text, StringComparison.Ordinal)).Select(pair => pair.Value).ToArray()).ToArray();
                            if (originals.All(matches => matches.Length > 0 && matches.All(subject => JsonNode.DeepEquals(subject, proposition["subject"]))))
                                acknowledgment["applicability"] = "subject";
                        }
                    alterReview?.Invoke(context, response);
                }
            }
            else throw new InvalidOperationException("Unexpected planning dispatch: " + purpose);
            if (purpose == PlanningCompactConstruction.SelectPurpose && request.StructuredOutputSchema!["properties"]!["resumeStep"] is not null)
                response["resumeStep"] = null;
            var transported = purpose == PlanningCompactConstruction.SelectPurpose ||
                purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose
                ? response : PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject());
            if (alterReview is null && PlanningContractValidation.ValidateInstanceFindings(transported, request.StructuredOutputSchema!).Count > 0)
                throw new InvalidOperationException("Response " + purpose + ":" + next + ": " + transported!.ToJsonString() + " schema " + request.StructuredOutputSchema!.ToJsonString());
            var received = new LLMResponse { Json = transported, Text = transported!.ToJsonString() };
            responses.Add(request.ClientRequestId!, received); return received;
            }
            catch (Exception error) { responseError = new InvalidOperationException(purpose + ": " + error.Message, error); throw; }
        } };
        var planner = new HybridWorkflowPlanner();
        for (var count = 0; count < 180 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); count++)
        {
            state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
            Assert.True(responseError is null, responseError?.ToString());
            if (stopOnAdverse && state.Construction!.Candidate is { } candidate && PlanningConstruction.UnresolvedDefects(candidate)) break;
            if (!expectStop) Assert.True(state.Diagnostics.All(diagnostic => !diagnostic.Required),
                string.Join(";", state.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)) + " step=" + JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep) + " requirements=" + string.Join(",", state.Construction.Requirements.Select(r => r.Id + ":" + r.Status)) + " calls=" + runtime.Calls.Count);
        }
        return new(state, runtime, responses);
    }

    private static IEnumerable<string> RequirementIds(JsonNode node, JsonNode root)
    {
        if (node is JsonObject obj)
        {
            if (obj["requirementIds"] is { } requirementSchema)
                foreach (var id in EnumValues(requirementSchema)) yield return id;
            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>())
                foreach (var id in RequirementIds(child, root)) yield return id;
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>()) foreach (var id in RequirementIds(child, root)) yield return id;
        IEnumerable<string> EnumValues(JsonNode value)
        {
            if (value is JsonObject item)
            {
                if (item["$ref"]?.ToString() is { } reference)
                { foreach (var id in EnumValues(root["$defs"]![reference[8..]]!)) yield return id; }
                else if (item["enum"] is JsonArray values)
                { foreach (var id in values) yield return id!.ToString(); }
                else foreach (var child in item.Select(pair => pair.Value).OfType<JsonNode>())
                    foreach (var id in EnumValues(child)) yield return id;
            }
            else if (value is JsonArray values)
                foreach (var child in values.OfType<JsonNode>()) foreach (var id in EnumValues(child)) yield return id;
        }
    }

    private static IEnumerable<LLMRequest> Judges(TestRuntime runtime) => runtime.Calls.Where(request => request.StructuredOutputSchema?["title"]?.ToString() is
        PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static JsonNode? Declarations(IEnumerable<string> names) => JsonSerializer.SerializeToNode(names.Select(name => new TaskInput
        { Name = name, Type = new() { Kind = "number" } }).ToList(), PlanningJsonContext.Default.ListTaskInput);
    private static TaskValue Number(double number) => new() { Kind = "number", Number = number };
    private static TaskValue Formula(string operation, params TaskValue[] values) => new() { Kind = "arithmetic", Text = operation, Items = values.ToList() };
    private static async Task<JsonObject?> Execute(PlanningSession state, JsonObject inputs, bool opaquePrefix = false)
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var inference = new PrefixInference();
        var engine = new WorkflowEngine { LLMClient = opaquePrefix ? inference : new ForbiddenInference(),
            LlmDefaults = new() { Model = "fixture" }, LLMUsageBudget = new(new() { MaxCalls = 1 }) };
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], inputs.DeepClone(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(opaquePrefix ? 1 : 0, inference.Calls);
        return result.Outputs?.AsObject();
    }
    private sealed class PrefixInference : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Contains("seven", request.Prompt, StringComparison.Ordinal);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["value"] = 7 }, Text = "{\"value\":7}" });
        }
    }
    private sealed class ForbiddenInference : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This deterministic executable must not dispatch runtime inference.");
    }
}
