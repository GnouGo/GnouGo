using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConstructionReviewCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryOriginalWindowMustPassBeforeTheSameFragmentIsAccepted(bool intermediate)
    {
        var state = await State();
        var construction = state.Construction!;
        construction.Candidate!.NeedsIntermediateReview = intermediate;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var candidate = construction.Candidate.Hash;
        var step = JsonSerializer.Serialize(construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var originals = Originals(state);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var seenWindows = new HashSet<string>(StringComparer.Ordinal);
        int? requiredWindows = null;
        string? requiredSet = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            var coverage = context["sourceCoverage"]!.AsObject();
            var windows = coverage["windowCount"]!.GetValue<int>();
            requiredWindows ??= windows;
            requiredSet ??= coverage["setHash"]!.ToString();
            Assert.Equal(requiredWindows.Value, windows);
            Assert.Equal(requiredSet, coverage["setHash"]!.ToString());
            Assert.True(windows > 1);
            Assert.Equal(seenWindows.Count, coverage["windowIndex"]!.GetValue<int>());
            Assert.True(seenWindows.Add(coverage["windowHash"]!.ToString()));
            Assert.Equal(intermediate ? new[] { "local", "intermediate" } : ["local"], Strings(context["reviewCoverage"]));
            var selected = Strings(coverage["sourceIds"]).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(selected);
            foreach (var source in originals)
            {
                var exact = JsonSerializer.Serialize(source.Value);
                if (selected.Contains(source.Key))
                {
                    Assert.Contains(exact, context.ToJsonString(), StringComparison.Ordinal);
                    Assert.True(seenSources.Add(source.Key));
                }
                else Assert.DoesNotContain(exact, context.ToJsonString(), StringComparison.Ordinal);
            }
            Assert.Equal(windows - seenWindows.Count, request.RequiredReviewCalls);
            return Response(Report());
        } };
        for (var count = 0; count < 64 && state.Construction!.Candidate is not null; count++)
        {
            state = await Advance(state, runtime);
            Assert.Empty(state.Diagnostics);
            if (state.Construction!.Candidate is null) break;
            Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
            Assert.Equal(candidate, state.Construction.Candidate.Hash);
            Assert.Equal(step, JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
            Assert.False(state.Construction.Candidate.LocalReviewed);
            Assert.False(state.Construction.Candidate.IntermediateReviewed);
            Assert.False(PlanningConstructionReviewCoverage.Complete(state, state.Construction.Candidate));
            Assert.Empty(state.Construction.Checkpoints);
            Assert.Equal("remaining", Assert.Single(state.Construction.Requirements).Status);
        }
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(candidate, checkpoint.PlanHash);
        Assert.Equal(requiredWindows!.Value, seenWindows.Count);
        Assert.Equal(originals.Keys.Order(StringComparer.Ordinal), seenSources.Order(StringComparer.Ordinal));
        Assert.Equal(requiredWindows.Value, state.ModelCalls);
        Assert.Equal(requiredWindows.Value, CleanReviews(checkpoint.Reviews).Count());
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, PlanningConstruction.LocalPurpose));
        if (intermediate) Assert.True(PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, PlanningConstruction.IntermediatePurpose));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction.DraftCorrections);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("basis")]
    [InlineData("set")]
    [InlineData("candidate")]
    [InlineData("claims")]
    [InlineData("scope")]
    [InlineData("missing_manifest")]
    [InlineData("window")]
    [InlineData("source_ids")]
    [InlineData("raw_findings")]
    [InlineData("raw_uncertainty")]
    public async Task ArtifactApprovalRejectsIncompleteOrMismatchedWindowEvidence(string defect)
    {
        var state = await State(closeScope: true, closeRequirement: true);
        state.Construction!.Candidate!.NeedsIntermediateReview = true;
        state = await ReviewAll(state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(state, Assert.Single(state.Construction!.Checkpoints), PlanningConstruction.LocalPurpose));
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(state, Assert.Single(state.Construction.Checkpoints), PlanningConstruction.IntermediatePurpose));
        PlanningArtifactApproval.Verify(PlannerFixture.Clone(state));
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        var clean = CleanReviews(checkpoint.Reviews).ToArray();
        Assert.True(clean.Length > 1);
        var last = clean[^1];
        switch (defect)
        {
            case "missing": checkpoint.Reviews.Remove(last); break;
            case "duplicate": checkpoint.Reviews[checkpoint.Reviews.IndexOf(last)] = clean[0].DeepClone().AsObject(); break;
            case "basis": last["sourceCoverage"]!["basisHash"] = new string('0', 64); break;
            case "set": last["sourceCoverage"]!["setHash"] = new string('0', 64); break;
            case "candidate": last["candidateHash"] = new string('0', 64); break;
            case "claims": last["closedRequirementIds"] = new JsonArray(); break;
            case "scope": last["reviewCoverage"] = new JsonArray("local"); break;
            case "missing_manifest": checkpoint.Reviews.RemoveAll(review => review["outcome"]?.ToString() == "source_windows"); break;
            case "window": last["sourceCoverage"]!["windowHash"] = new string('0', 64); break;
            case "source_ids": last["sourceCoverage"]!["sourceIds"] = new JsonArray(); break;
            case "raw_findings": last["rawResponse"]!["action"] = new JsonObject { ["kind"] = "inconclusive", ["reason"] = "Review incomplete." }; break;
            case "raw_uncertainty": last["rawResponse"]!["uncertainty"] = "A different retained report."; break;
        }
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(PlannerFixture.Clone(state)));
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task LaterAcceptedWorkKeepsEarlierExactCheckpointCoverageApprovable()
    {
        var state = await ReviewAll(await State());
        var first = Assert.Single(state.Construction!.Checkpoints);
        var firstReviews = new JsonArray(first.Reviews.Select(review => (JsonNode)review.DeepClone()).ToArray()).ToJsonString();
        var construction = state.Construction;
        construction.Compact!.CurrentStep = new() { Id = "final_value", Kind = "value", Scope = "/root",
            Objective = "Retain the completed value and close the work.", RequirementIds = ["work"], CloseRequirementIds = ["work"],
            DiscoveryComplete = true, BaseHash = PlanningConstruction.Hash(state.Plan) };
        construction.CandidateFailureIdentityVersion = 1;
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            CloseScopes = ["/root"], Complete = true, Tasks = [new() { Id = "final_value", Kind = "value",
                Objective = "Retain the completed value.", Outputs = [new("value", new() { Kind = "output", Source = "use_value", Port = "value" })] }] });
        state = await ReviewAll(state);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.Equal(firstReviews, new JsonArray(state.Construction.Checkpoints[0].Reviews.Select(review => (JsonNode)review.DeepClone()).ToArray()).ToJsonString());
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        PlanningArtifactApproval.Verify(PlannerFixture.Clone(state));
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task LaterWindowFindingRequiresReplacementAndFreshCoverageOfEveryOriginal()
    {
        var state = await State();
        var capability = new PlanningCapability
        {
            Id = "preserve_value", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "values", Method = "preserve", EffectKind = "read",
            Description = "Return the exact supplied numeric value.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray("value"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray("value"), ["additionalProperties"] = false }
        };
        state.Catalog!.Capabilities.Add(capability); state.Discovery.Resolved.Add(capability);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = [capability.Id];
        var extension = state.Construction!.Candidate!.Extension;
        extension.Tasks[0].Kind = "operation"; extension.Tasks[0].Operation = capability.Id;
        extension.Tasks[0].Outputs = [];
        extension.Tasks[0].Inputs = [new("value", new() { Kind = "number", Number = 8 })];
        state.Construction.Candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.StageCommitments(state, null);
        var originalCandidate = state.Construction.Candidate.Hash;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var lastOriginal = state.Construction.Compact!.Sources.Last(source => source.Kind == "global");
        var beforeWindows = new HashSet<string>(StringComparer.Ordinal);
        var afterWindows = new HashSet<string>(StringComparer.Ordinal);
        var resolved = false;
        var repaired = false;
        string? previousBasis = null;
        int? nextWindowCount = null;
        Exception? fixtureFailure = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
                var context = Context(request);
                if (purpose == "replan")
                {
                    Assert.False(repaired);
                    Assert.False(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
                    var next = JsonSerializer.Deserialize(JsonSerializer.Serialize(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                        PlanningJsonContext.Default.PlanningConstructionExtension)!;
                    next.Tasks[0].Inputs[0] = new("value", new() { Kind = "number", Number = 7 });
                    var proposed = new JsonObject { ["baseHash"] = accepted,
                        ["extension"] = JsonSerializer.SerializeToNode(next, PlanningJsonContext.Default.PlanningConstructionExtension) };
                    var wire = PlanningCorpus.Transport(proposed, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject())!.AsObject();
                    Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, request.StructuredOutputSchema));
                    repaired = true;
                    return Response(wire);
                }
                Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
                var coverage = context["sourceCoverage"]!;
                var rows = context["globalConstraints"]!.AsArray();
                var index = Array.FindIndex(rows.ToArray(), row => row?["id"]?.ToString() == lastOriginal.Id);
                var report = Report();
                if (!repaired)
                {
                    previousBasis ??= coverage["basisHash"]!.ToString();
                    Assert.True(beforeWindows.Add(coverage["windowHash"]!.ToString()));
                    if (index >= 0)
                    {
                        Assert.True(coverage["windowIndex"]!.GetValue<int>() > 0);
                        report["action"]!["findings"] = new JsonArray(new JsonObject
                        {
                            ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0", ["obligation"] = "/globalConstraints/" + index,
                            ["implementation"] = "/draft/tasks/0", ["explanation"] = "The candidate substitutes 8 for the supplied value 7.",
                            ["uncertainty"] = "No execution was observed.", ["fact"] = null, ["requiredBinding"] = null
                        });
                    }
                }
                else
                {
                    Assert.NotEqual(previousBasis, coverage["basisHash"]!.ToString());
                    nextWindowCount ??= coverage["windowCount"]!.GetValue<int>();
                    Assert.True(afterWindows.Add(coverage["windowHash"]!.ToString()));
                    if (context["defectProgress"] is JsonArray defects)
                    {
                        var defect = Assert.Single(defects)!;
                        Assert.True(index >= 0);
                        Assert.Equal(new[] { lastOriginal.Id }, Strings(defect["obligationSourceIds"]));
                        Assert.Equal("changed", defect["status"]!.ToString());
                        report["action"]!["resolutions"] = new JsonArray(new JsonObject
                        {
                            ["id"] = defect["id"]!.DeepClone(), ["status"] = "resolved", ["obligation"] = "/globalConstraints/" + index,
                            ["implementation"] = "/draft/tasks/0", ["explanation"] = "The candidate now retains the exact supplied value 7."
                        });
                        resolved = true;
                    }
                    else Assert.True(index < 0);
                }
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
                return Response(report);
            }
            catch (Exception error) { fixtureFailure = error; throw; }
        } };
        for (var count = 0; count < 64 && state.Construction.Candidate is not null; count++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.True(fixtureFailure is null, fixtureFailure?.ToString());
            Assert.NotEqual(PlanningStatus.Stopped, state.Status);
            if (state.Diagnostics.Count > 0)
            {
                Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
                Assert.Empty(state.Construction!.Checkpoints);
                Assert.False(PlanningConstructionReviewCoverage.Complete(state, state.Construction.Candidate!));
            }
            if (state.Construction!.Candidate is not null) Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        }
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.True(repaired); Assert.True(resolved);
        Assert.True(beforeWindows.Count > 1);
        Assert.Equal(nextWindowCount!.Value, afterWindows.Count);
        Assert.NotEqual(originalCandidate, checkpoint.PlanHash);
        Assert.Equal(7, state.Plan!.Root.Tasks.Single(task => task.Id == "use_value").Inputs[0].Value.Number);
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, PlanningConstruction.LocalPurpose));
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task LinkedOriginalContinuationsStayWholeInOneReviewWindow()
    {
        var state = await State();
        var linked = state.Construction!.Compact!.Sources.Where(source => source.Kind == "global").Take(2).ToArray();
        linked[1].ContinuationOf = linked[0].Id;
        var appearances = 0;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = Context(request);
            var ids = Strings(context["sourceCoverage"]!["sourceIds"]);
            var rows = context["globalConstraints"]!.AsArray().OfType<JsonObject>().ToArray();
            Assert.DoesNotContain("global:" + linked[1].Id, ids);
            if (ids.Contains("global:" + linked[0].Id, StringComparer.Ordinal))
            {
                foreach (var original in linked)
                {
                    var row = Assert.Single(rows, item => item["id"]?.ToString() == original.Id);
                    Assert.Equal(original.Text, row["text"]!.ToString());
                    Assert.Equal(original.Version, row["version"]!.ToString());
                }
                appearances++;
            }
            else Assert.DoesNotContain(rows, item => linked.Any(source => source.Id == item["id"]?.ToString()));
            return Response(Report());
        } };
        state = await ReviewAll(state, runtime);
        Assert.Equal(1, appearances);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task AutomaticClosureRequiresTheWholeRetainedWindowSet()
    {
        var state = await ReviewAll(await State(closeRequirement: true));
        Assert.Equal("closed", Assert.Single(state.Construction!.Requirements).Status);
        Assert.Contains("/root", state.Construction.OpenScopes);
        Assert.NotNull(PlanningCompactConstruction.PrepareReviewedClosure(PlannerFixture.Clone(state)));
        var checkpoint = Assert.Single(state.Construction.Checkpoints);
        var clean = CleanReviews(checkpoint.Reviews).ToArray();
        Assert.True(clean.Length > 1);
        checkpoint.Reviews.Remove(clean[^1]);
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.Null(state.Construction.Candidate);
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task InterruptedSecondWindowRecoversItsExactRequestWithoutAnotherCharge()
    {
        var state = await State();
        var runtime = new TestRuntime { Respond = (_, _) => Response(Report()) };
        state = await Advance(state, runtime);
        Assert.NotNull(state.Construction!.Candidate);
        var retained = Assert.Single(CleanReviews(state.Construction.Candidate.Reviews)).ToJsonString();
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
        var charged = state.ModelCalls;
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var context = state.Construction!.PendingReviewContext!.ToJsonString();
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(context, Context(request).ToJsonString());
            return Response(Report());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(charged, state.ModelCalls);
        Assert.Null(state.PendingCall);
        var reviews = state.Construction!.Candidate?.Reviews ?? Assert.Single(state.Construction.Checkpoints).Reviews;
        var clean = CleanReviews(reviews).ToArray();
        Assert.Equal(retained, clean[0].ToJsonString());
        Assert.Equal(2, clean.Length);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task RequestedOriginalReopensCoverageAndStaysWithItsIssuedWindow()
    {
        var state = await State();
        var candidate = state.Construction!.Candidate!.Hash;
        var baseline = PlanningConstruction.Hash(state.Plan);
        const string address = "requirement:work";
        string? originalSet = null;
        string[]? requestedFrom = null;
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        int? renewedCount = null;
        var sawJoinedWindow = false;
        Exception? fixtureFailure = null;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            try
            {
                var context = Context(request);
                var coverage = context["sourceCoverage"]!;
                originalSet ??= coverage["setHash"]!.ToString();
                var keys = Strings(coverage["sourceIds"]);
                var report = Report();
                if (requestedFrom is null && coverage["windowIndex"]!.GetValue<int>() == coverage["windowCount"]!.GetValue<int>() - 1)
                {
                    Assert.True(coverage["windowIndex"]!.GetValue<int>() > 0);
                    Assert.DoesNotContain("requirement:work", keys);
                    requestedFrom = keys;
                    report["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) };
                }
                else if (requestedFrom is not null)
                {
                    Assert.NotEqual(originalSet, coverage["setHash"]!.ToString());
                    renewedCount ??= coverage["windowCount"]!.GetValue<int>();
                    Assert.True(repeated.Add(coverage["windowHash"]!.ToString()));
                    if (keys.Contains("requirement:work", StringComparer.Ordinal))
                    {
                        Assert.All(requestedFrom, key => Assert.Contains(key, keys));
                        var row = Assert.Single(context["requirements"]!.AsArray())!;
                        Assert.Equal(state.Requirements!.Outcomes[0].Description, row["original"]!.ToString());
                        sawJoinedWindow = true;
                    }
                }
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
                return Response(report);
            }
            catch (Exception error) { fixtureFailure = error; throw; }
        } };
        for (var count = 0; count < 64 && state.Construction.Candidate is not null; count++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.True(fixtureFailure is null, fixtureFailure?.ToString());
            Assert.Empty(state.Diagnostics);
            if (state.Construction!.Candidate is not null)
            {
                Assert.Equal(candidate, state.Construction.Candidate.Hash);
                Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
                Assert.Empty(state.Construction.Checkpoints);
            }
        }
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.True(sawJoinedWindow);
        Assert.Equal(renewedCount!.Value, repeated.Count);
        Assert.Equal(candidate, checkpoint.PlanHash);
        Assert.True(PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, PlanningConstruction.LocalPurpose));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction.DraftCorrections);
        Assert.DoesNotContain(checkpoint.Reviews, review => review["reassessment"] is not null);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("claims")]
    [InlineData("candidate")]
    public async Task CarriedCleanResolutionCannotCoverAChangedCandidateBasis(string change)
    {
        var state = await State();
        state = await Advance(state, new() { Respond = (_, _) => Response(Report()) });
        var previous = state.Construction!.Candidate!;
        var retained = Assert.Single(CleanReviews(previous.Reviews));
        retained["response"]!["resolutions"] = new JsonArray();
        var before = retained["sourceCoverage"]!["basisHash"]!.ToString();
        if (change == "source")
        {
            var source = state.Construction.Compact!.Sources.First(item => item.Kind == "global");
            source.Text += " Preserve its exact identity.";
            source.Version = PlanningGraphCompiler.Fingerprint(source.Text);
        }
        if (change == "claims") state.Construction.Compact!.CurrentStep!.CloseRequirementIds = ["work"];
        var extension = JsonSerializer.Deserialize(JsonSerializer.Serialize(previous.Extension, PlanningJsonContext.Default.PlanningConstructionExtension),
            PlanningJsonContext.Default.PlanningConstructionExtension)!;
        if (change == "candidate") extension.Tasks[0].Objective += " Retain the exact value.";
        var next = PlanningConstruction.Apply(state, extension);
        PlanningConstruction.CarryDefectWitnesses(state, previous, next);
        state.Construction.Candidate = next;
        Assert.Contains(next.Reviews, review => JsonNode.DeepEquals(review, retained));
        Assert.False(PlanningConstructionReviewCoverage.Complete(state, next));
        var context = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Context;
        Assert.NotEqual(before, context["sourceCoverage"]!["basisHash"]!.ToString());
        Assert.Equal(context["sourceCoverage"]!["windowCount"]!.GetValue<int>(), PlanningConstructionReviewCoverage.RemainingCalls(state));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task ChangedConsumedProducerContractInvalidatesPreviouslyCleanCoverage()
    {
        var state = await State();
        var capability = new PlanningCapability
        {
            Id = "read_value", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "values", Method = "read", EffectKind = "read",
            Description = "Read the exact supplied numeric value.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray("value"), ["additionalProperties"] = false }
        };
        state.Catalog!.Capabilities.Add(capability);
        state.Discovery.Resolved = [capability];
        var producer = Assert.Single(state.Plan!.Root.Tasks);
        producer.Kind = "operation"; producer.Operation = capability.Id; producer.Outputs = [];
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, state.Construction.Candidate!.Extension);
        state = await Advance(state, new() { Respond = (_, _) => Response(Report()) });
        var candidate = state.Construction!.Candidate!;
        var basis = Assert.Single(CleanReviews(candidate.Reviews))["sourceCoverage"]!["basisHash"]!.ToString();
        foreach (var contract in state.Catalog!.Capabilities.Concat(state.Discovery.Resolved).Where(item => item.Id == capability.Id))
            contract.Description = "Read the numeric value after the explicitly required caller preparation.";
        var context = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Context;
        Assert.NotEqual(basis, context["sourceCoverage"]!["basisHash"]!.ToString());
        Assert.Equal(context["sourceCoverage"]!["windowCount"]!.GetValue<int>(), PlanningConstructionReviewCoverage.RemainingCalls(state));
        Assert.False(PlanningConstructionReviewCoverage.Complete(state, candidate));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task AllWindowsShareTheSingleReceivedReportRepairAllowance()
    {
        var state = await State();
        static JsonObject InvalidReport() => new() { ["action"] = null, ["uncertainty"] = "Unobserved execution." };
        var runtime = new TestRuntime { Respond = (_, _) => Response(InvalidReport()) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        var firstWindow = state.Construction!.PendingReviewContext!["sourceCoverage"]!["windowHash"]!.ToString();
        Assert.Single(state.Construction.Candidate!.Reviews, review => review["reassessment"] is not null);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(firstWindow, Context(request)["sourceCoverage"]!["windowHash"]!.ToString());
            return Response(Report());
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        runtime.Respond = (request, _) =>
        {
            Assert.NotEqual(firstWindow, Context(request)["sourceCoverage"]!["windowHash"]!.ToString());
            return Response(InvalidReport());
        };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(3, state.ModelCalls);
        Assert.Single(state.Construction!.Candidate!.Reviews, review => review["reassessment"] is not null);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction.DraftCorrections);
        Assert.Null(state.PendingCall);
    }

    [Fact]
    public async Task AnIndivisibleOriginalStopsBeforeDispatchWithoutTruncationOrCorrection()
    {
        var state = await State(oversized: true);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var original = Originals(state).Values.Single();
        var runtime = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("Oversized required context must not dispatch.") };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(runtime.Calls);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(original, Originals(state).Values.Single());
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code is "MODEL_INPUT_LIMIT" or "CONSTRUCTION_REVIEW_INCONCLUSIVE");
        Assert.Null(state.PendingCall);
    }

    [Fact]
    public async Task RemainingWindowCallsAreReservedBeforeAnyPartialAcceptance()
    {
        var state = await State();
        _ = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        var required = PlanningConstructionReviewCoverage.RemainingCalls(state);
        Assert.True(required > 1);
        state.Request.MaxModelCalls = required - 1;
        var runtime = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("Insufficient mandatory-review allowance must not dispatch.") };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(runtime.Calls);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal("remaining", Assert.Single(state.Construction.Requirements).Status);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction.DraftCorrections);
        Assert.Null(state.PendingCall);
    }

    private static async Task<PlanningSession> ReviewAll(PlanningSession state, TestRuntime? runtime = null)
    {
        runtime ??= new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Contains(purpose, new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose });
            var report = Report();
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
            return Response(report);
        } };
        for (var count = 0; count < 64 && state.Construction!.Candidate is not null; count++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
        }
        Assert.Null(state.Construction!.Candidate);
        return state;
    }

    private static async Task<PlanningSession> State(bool closeScope = false, bool closeRequirement = false, bool oversized = false)
    {
        var clauses = Enumerable.Range(0, oversized ? 1 : 18).Select(index =>
            $"Original clause {index}: retain the exact supplied value and preserve this distinct condition. " +
            string.Concat(Enumerable.Repeat($"Condition {index} remains authoritative. ", oversized ? 4000 : 28)) + "\n").ToArray();
        var state = new PlanningSession { Request = new() { TenantId = "review-coverage", Prompt = string.Concat(clauses), MaxModelCalls = 64 } };
        PlanningSessionCreation.Capture(state);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        var compact = construction.Compact!;
        compact.PresentationVersion = 6; state.Request.Options["construction_presentation_version"] = 6;
        construction.CandidateFailureIdentityVersion = 1;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var page in compact.Sources) page.Indexed = true;
        var pages = compact.Sources.ToArray();
        var originals = oversized ? new[] { (Id: pages[0].Id, Text: state.Request.Prompt) }
            : pages.Select(page => (page.Id, page.Text)).ToArray();
        for (var index = 0; index < originals.Length; index++)
        {
            var original = originals[index];
            var source = new PlanningConstructionSource { Id = original.Id + ":clause:0", Kind = index == 0 ? "requirement" : "global",
                Text = original.Text, Version = PlanningGraphCompiler.Fingerprint(original.Text), Indexed = true, InterfaceKind = "none" };
            compact.Sources.Add(source);
            if (index == 0) compact.RequirementSources["work"] = [source.Id]; else compact.GlobalSourceIds.Add(source.Id);
        }
        compact.InterfaceExtractions = [];
        state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("work", originals[0].Text)] };
        construction.Requirements = [new() { Id = "work" }]; construction.OpenScopes = ["/root"];
        state.Plan = new() { Root = new() { Tasks = [new()
        {
            Id = "accepted_value", Kind = "value", Objective = "Retain the exact value.",
            Outputs = [new("value", new() { Kind = "number", Number = 7 })]
        }] } };
        compact.CurrentStep = new() { Id = "use_value", Kind = "value", Scope = "/root", Objective = "Use the accepted value.",
            RequirementIds = ["work"], CloseRequirementIds = closeRequirement ? ["work"] : [],
            DiscoveryComplete = true, BaseHash = PlanningConstruction.Hash(state.Plan) };
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            CloseScopes = closeScope ? ["/root"] : [], Complete = closeScope, Tasks = [new()
            {
                Id = "use_value", Kind = "value", Objective = "Use the accepted value.",
                Outputs = [new("value", new() { Kind = "output", Source = "accepted_value", Port = "value" })]
            }] });
        return state;
    }

    private static Dictionary<string, string> Originals(PlanningSession state) => state.Construction!.Compact!.Sources
        .Where(source => source.Kind is "requirement" or "global")
        .ToDictionary(source => source.Kind == "requirement" ? "requirement:work" : "global:" + source.Id, source => source.Text, StringComparer.Ordinal);
    private static IEnumerable<JsonObject> CleanReviews(IEnumerable<JsonObject> reviews) => reviews.Where(review =>
        review["outcome"]?.ToString() == "unverified" && review["sourceCoverage"] is JsonObject);
    private static string[] Strings(JsonNode? value) => value!.AsArray().Select(item => item!.ToString()).ToArray();
    private static JsonObject Report() => new() { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
        ["uncertainty"] = "Execution remains unobserved." };
    private static LLMResponse Response(JsonObject report) => new() { Json = report, Text = report.ToJsonString() };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
