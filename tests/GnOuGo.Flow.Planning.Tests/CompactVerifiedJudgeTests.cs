using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactVerifiedJudgeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectedScopeStatusDoesNotConfuseAnOpenAncestorWithItsClosedChild(bool nested, bool closeChild)
    {
        var state = await TaskState();
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        if (nested)
        {
            state.Plan!.Root.Tasks.Add(new()
            {
                Id = "container", Kind = "sequence", Objective = "Preserve the caller label within a child scope.",
                Body = new()
                {
                    Tasks = [new() { Id = "child_label", Kind = "value", Objective = "Preserve the caller label.",
                        Outputs = [new("value", new() { Kind = "input", Source = "label" })] }],
                    Outputs = [new("value", new() { Kind = "output", Source = "child_label", Port = "value" })]
                }
            });
            step.Scope = "/tasks/container/body"; step.Kind = closeChild ? "close" : "value";
            step.BaseHash = PlanningConstruction.Hash(state.Plan);
            construction.OpenScopes.Add(step.Scope);
            construction.Candidate = PlanningConstruction.Apply(state, new()
            {
                Scope = step.Scope, RequirementIds = ["work"], CloseScopes = closeChild ? [step.Scope] : [],
                Tasks = closeChild ? [] : [new() { Id = "child_next", Kind = "value", Objective = "Continue the child work.",
                    Outputs = [new("value", new() { Kind = "input", Source = "label" })] }]
            });
        }
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var intermediate in new[] { false, true })
        {
            var (context, _) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate);
            var facts = context["constructionStage"]!["preflight"]!;
            Assert.Equal("passed", facts["status"]!.ToString());
            Assert.Equal(step.Scope, facts["scope"]!.ToString());
            Assert.Equal(!closeChild, facts["scopeOpen"]!.GetValue<bool>());
            Assert.Equal(nested && !closeChild ? 2 : 1, facts["openScopeCount"]!.GetValue<int>());
            Assert.Equal(closeChild ? 1 : 0, context["constructionStage"]!["closedScopeCount"]!.GetValue<int>());
        }
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        if (nested)
        {
            var candidate = construction.Candidate!;
            var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(candidate.Plan, PlanningJsonContext.Default.TaskPlan),
                PlanningJsonContext.Default.TaskPlan)!;
            probe.Root.Outputs.Add(new("result", new() { Kind = "output", Source = "container", Port = "value" }));
            var diagnostics = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request,
                candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics;
            if (closeChild) Assert.Empty(diagnostics);
            else Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "TASK_SCOPE_OPEN");
            Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        }
    }

    [Theory]
    [InlineData("/draft/tasks/0", "/requirements/0", true)]
    [InlineData("/requirements/0", "/constructionStage", false)]
    [InlineData("/draft/tasks/0", "/constructionStage", false)]
    [InlineData("/draft/tasks/1", "/requirements/0", false)]
    [InlineData("/draft/tasks/0/outputs/0", "/requirements/0", false)]
    public async Task ConcreteIssuedLocationAvoidsDuplicateCitationWithoutWeakeningGrounding(string location, string evidence, bool supported)
    {
        var state = await TaskState();
        var accepted = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(1, FindingProperties(request.StructuredOutputSchema!.AsObject())["evidence"]!["minItems"]!.GetValue<int>());
            return Response(findings: new JsonArray(Finding(location, evidence)));
        } };
        state = await Advance(state, runtime);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(supported ? PlanningStatus.Generating : PlanningStatus.Stopped, state.Status);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal(supported ? "CONSTRUCTION_BUSINESS_REVIEW" : "CONSTRUCTION_REVIEW_INCONCLUSIVE", diagnostic.Code);
        Assert.Equal(supported, state.Construction.Candidate!.Diagnostics.Count > 0);
        if (supported) Assert.Equal("/tasks/use_label", diagnostic.Location);
    }

    [Theory]
    [InlineData(3, 256, 128)]
    [InlineData(4, 512, 256)]
    public async Task FindingTextBoundsChangeOnlyForFreshVerifiedReviewsWithoutIncreasingOutputReservation(int presentation, int explanationLimit, int uncertaintyLimit)
    {
        var state = await TaskState();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var (context, review) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        if (presentation == 3) Assert.Null(context["constructionStage"]!["preflight"]);
        else Assert.True(context["constructionStage"]!["preflight"]!["scopeOpen"]!.GetValue<bool>());
        var schema = review.StructuredOutputSchema!.AsObject();
        var properties = FindingProperties(schema);
        Assert.Equal(1, properties["evidence"]!["minItems"]!.GetValue<int>());
        Assert.Equal(explanationLimit, properties["explanation"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(uncertaintyLimit, properties["uncertainty"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(presentation == 4, schema["properties"]!["inconclusive"] is not null);
        var finding = Finding("/draft/tasks/0", "/requirements/0");
        finding["explanation"] = new string('x', explanationLimit);
        finding["uncertainty"] = new string('u', uncertaintyLimit);
        var response = Response(findings: new JsonArray(finding)).Json!.AsObject();
        if (presentation == 3) response.Remove("inconclusive");
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        finding["explanation"] = new string('x', explanationLimit + 1);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        finding["explanation"] = new string('x', explanationLimit);
        finding["uncertainty"] = new string('u', uncertaintyLimit + 1);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, review.Prompt, schema);
        Assert.Equal(2048, issued.MaxTokens);
        Assert.Equal(PlanningModelCalls.ConstructionOutputLimit(state, PlanningConstruction.LocalPurpose), issued.MaxTokens);
    }

    [Fact]
    public async Task RetainedVerifiedTwoCitationRequestKeepsItsOriginalGroundingAndExactBytes()
    {
        var state = await TaskState();
        var accepted = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.True(context["constructionStage"]!["preflight"]!.AsObject().Remove("scopeOpen"));
        context["operations"] = new JsonArray(new JsonObject { ["id"] = "retained_contract", ["description"] = "An original operation contract." });
        var schema = HybridWorkflowPlanner.ConstructionReviewSchema(context, verified: true);
        var properties = FindingProperties(schema);
        properties["evidence"]!["minItems"] = 2;
        properties["explanation"]!["maxLength"] = 256;
        properties["uncertainty"]!["maxLength"] = 128;
        var review = PlanningCompactConstruction.Request("Retained scoped business review.", context, schema, PlanningConstruction.LocalPurpose);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, review.Prompt, schema);
        state.Construction!.PendingReviewContext = context;
        state.Construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            state.Construction.Candidate!.Hash + "\n" + context.ToJsonString());
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued };
        state.ModelCalls = 1;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var finding = Finding("/draft/tasks/0", "/requirements/0", "/operations/0");
        Assert.True(HybridWorkflowPlanner.ConstructionBusinessFindingSupported(context, finding, locationEvidence: true));
        Assert.False(HybridWorkflowPlanner.ConstructionBusinessFindingSupported(context, finding));
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Null(Context(request)["constructionStage"]!["preflight"]!["scopeOpen"]);
            return Response(findings: new JsonArray(finding.DeepClone()));
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Empty(state.Construction.Candidate!.Diagnostics);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("/constructionStage", false)]
    [InlineData("/inputDeclarations", false)]
    [InlineData("/inputDeclarations/inputs/0", true)]
    public async Task OnlyOriginalObligationAndConcreteEvidenceCanRequestLocalCorrection(string implementation, bool supported)
    {
        var state = await State();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (_, _) => Response(findings: new JsonArray(new JsonObject
        {
            ["kind"] = "inconsistency", ["location"] = "/requirements/0",
            ["evidence"] = new JsonArray("/requirements/0", implementation),
            ["explanation"] = "The declaration does not fulfill the cited original obligation.", ["uncertainty"] = "Business interpretation."
        })) };
        state = await Advance(state, runtime);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(supported ? "CONSTRUCTION_BUSINESS_REVIEW" : "CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(supported ? PlanningStatus.Generating : PlanningStatus.Stopped, state.Status);
        Assert.Equal(supported, state.Construction.Candidate!.Diagnostics.Count > 0);
        Assert.NotNull(Assert.Single(state.Construction.Candidate.Reviews)["response"]);
    }

    [Fact]
    public void AggregatedOriginalGlobalsAndExactOperandsRemainValidGrounding()
    {
        var context = new JsonObject
        {
            ["requirements"] = new JsonArray(new JsonObject { ["id"] = "work" }),
            ["globalConstraints"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode)new JsonObject { ["id"] = "source" + i, ["text"] = "Original clause" }).ToArray()),
            ["interfaces"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode)new JsonObject
                { ["binding"] = new JsonObject { ["kind"] = "input", ["source"] = "p" + i }, ["schema"] = new JsonObject { ["type"] = "number" } }).ToArray())
        };
        var finding = new JsonObject { ["kind"] = "inconsistency", ["location"] = "/requirements/0",
            ["evidence"] = new JsonArray("/globalConstraints", "/interfaces"), ["explanation"] = "A concrete business conflict.", ["uncertainty"] = "Advisory." };
        var schema = HybridWorkflowPlanner.ConstructionReviewSchema(context, verified: true);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Response(findings: new JsonArray(finding.DeepClone())).Json!, schema));
        Assert.True(HybridWorkflowPlanner.ConstructionBusinessFindingSupported(context, finding));
    }

    [Fact]
    public async Task ExecutionUncertaintyDoesNotMeanIncompleteBusinessReview()
    {
        var state = await State();
        state = await Advance(state, new() { Respond = (_, _) => Response() });
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("closed", state.Construction.Requirements.Single(r => r.Id == "declare").Status);
        Assert.False(state.Construction.Complete);
        Assert.Null(state.ApprovedHash);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("two_actions")]
    [InlineData("blank_reason")]
    [InlineData("no_obligation")]
    public async Task InvalidOrUnsupportedReportNeverSchedulesReassessment(string defect)
    {
        var state = await State(large: true);
        var runtime = new TestRuntime { Respond = (_, _) =>
        {
            var response = Response(inconclusive: defect == "blank_reason" ? "   " : "Need exact constraints.");
            if (defect == "two_actions") response.Json!["findings"] = new JsonArray();
            if (defect == "no_obligation") response = Response(findings: new JsonArray(new JsonObject
            {
                ["kind"] = "omission", ["location"] = "/requirements/0", ["evidence"] = new JsonArray("/constructionStage", "/inputDeclarations"),
                ["explanation"] = "The validation summary does not mention the desired value.", ["uncertainty"] = "Unknown."
            }));
            return response;
        } };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.All(state.Construction.Candidate!.Reviews, review => Assert.Null(review["reassessment"]));
        Assert.NotNull(Assert.Single(state.Construction.Candidate.Reviews)["response"]);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("present")]
    [InlineData("field")]
    [InlineData("closure")]
    [InlineData("original")]
    public async Task MissingConsumedProducerAddsOnlyItsExactEvidenceToOneReassessment(string use)
    {
        var state = await ProducerState(use);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var producer = PlanningConstruction.TaskNode(new TaskPlanSymbols(state.Plan!).Tasks["accepted_label"].Task);
        const string originalAddress = "original:0:accepted_label";
        if (use == "original")
        {
            var original = producer.DeepClone();
            original["objective"] = "Invalidated earlier implementation.";
            state.Construction!.Evidence = new() { [originalAddress] = original };
        }
        var retainedOriginal = state.Construction!.Evidence?[originalAddress]?.ToJsonString();
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.Contains(context["interfaces"]!.AsArray(), entry => TaskPlanCompiler.Values(JsonSerializer.Deserialize(
                entry!["binding"]!.ToJsonString(), PlanningJsonContext.Default.TaskValue)!).Any(value => value.Source == "accepted_label"));
            Assert.Equal(retainedOriginal, context["evidence"]?[originalAddress]?.ToJsonString());
            Assert.Contains(context["taskEvidence"]!["items"]!.AsArray(), entry => entry!["address"]!.ToString() == "task:unrelated");
            return Response(inconclusive: "The exact accepted producer is needed to assess this result.");
        } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal("task:accepted_label", Assert.Single(state.Construction.Evidence!, item => item.Key != originalAddress).Key);
        Assert.Equal(retainedOriginal, state.Construction.Evidence![originalAddress]?.ToJsonString());
        var support = Assert.Single(Assert.Single(state.Construction.Candidate!.Reviews)["support"]!.AsArray())!;
        Assert.Equal("task:accepted_label", support["address"]!.ToString());
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            var supplied = context["evidence"]!["task:accepted_label"]!;
            foreach (var field in producer.AsObject()) Assert.True(JsonNode.DeepEquals(field.Value, supplied[field.Key]));
            Assert.Equal(PlanningGraphCompiler.Fingerprint(supplied.ToJsonString()), support["hash"]!.ToString());
            Assert.Equal(support.ToJsonString(), Assert.Single(context["reassessment"]!["support"]!.AsArray())!.ToJsonString());
            Assert.Null(context["evidence"]!["task:unrelated"]);
            Assert.Equal("null", request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!["type"]!.ToString());
            return Response();
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Single(checkpoint.Reviews, review => review["reassessment"] is not null);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("page")]
    [InlineData("draft")]
    [InlineData("unrelated")]
    public async Task SuppliedOrUnconsumedProducerDoesNotManufactureReassessmentProgress(string support)
    {
        var state = await ProducerState(support);
        if (support is "task" or "scope" or "page") PlanningConstruction.LoadEvidence(state,
            [support == "task" ? "task:accepted_label" : support == "scope" ? "scope:/root" : "page:tasks:0"]);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var exactEvidence = state.Construction!.Evidence?.ToJsonString();
        var reads = state.Construction.EvidenceRequests.ToArray();
        state = await Advance(state, new() { Respond = (_, _) => Response(inconclusive: "The available implementation cannot complete this review.") });
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(exactEvidence, state.Construction!.Evidence?.ToJsonString());
        Assert.Equal(reads, state.Construction.EvidenceRequests);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.All(state.Construction.Candidate!.Reviews, review => Assert.Null(review["reassessment"]));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedReassessmentReceiptResumesWithoutAnotherDispatchOrAllowance(bool producer)
    {
        var state = producer ? await ProducerState() : await State(large: true);
        var runtime = new TestRuntime { Respond = (_, _) => Response(inconclusive: "Need exact omitted constraints.") };
        state = await Advance(state, runtime);
        LLMResponse? receipt = null;
        var dispatched = 0;
        runtime.Respond = (_, _) =>
        {
            dispatched++;
            receipt = Response(); // Simulated durable runtime receipt, before coordinator consumption.
            throw new OperationCanceledException();
        };
        _ = await Advance(state, runtime);
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt!; // Existing receipt, no provider dispatch.
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, dispatched);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("issued", Assert.Single(checkpoint.Reviews, review => review["reassessment"] is not null)["reassessment"]!.ToString());
        Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InconclusiveWithoutNewExactSupportStopsWithoutCorrection(bool alreadySupplied)
    {
        var state = await State(large: alreadySupplied);
        if (alreadySupplied) PlanningConstruction.LoadEvidence(state, ["input:label"]);
        state = await Advance(state, new() { Respond = (_, _) => Response(inconclusive: "The available evidence cannot complete this review.") });
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("CONSTRUCTION_REVIEW_INCONCLUSIVE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.All(state.Construction.Candidate!.Reviews, entry => Assert.Null(entry["reassessment"]));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("inconclusive")]
    [InlineData("evidence")]
    public async Task OneReassessmentUsesNewExactSupportAndCannotRestartEvidence(string outcome)
    {
        var state = await State(large: true);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (_, _) => Response(inconclusive: "The exact caller constraints are needed.") };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("ready", Assert.Single(state.Construction!.Candidate!.Reviews)["reassessment"]!.ToString());
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.NotNull(context["evidence"]!["input:label"]);
            var support = Assert.Single(context["reassessment"]!["support"]!.AsArray());
            Assert.Equal("input:label", support!["address"]!.ToString());
            Assert.Equal(PlanningGraphCompiler.Fingerprint(context["evidence"]!["input:label"]!.ToJsonString()), support["hash"]!.ToString());
            Assert.Equal("null", request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!["type"]!.ToString());
            return outcome == "complete" ? Response() : outcome == "inconclusive" ? Response(inconclusive: "Still unable to complete.")
                : Response(addresses: ["scope:/root"]);
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        var reviews = outcome == "complete" ? Assert.Single(state.Construction!.Checkpoints).Reviews : state.Construction!.Candidate!.Reviews;
        var issued = Assert.Single(reviews, entry => entry["reassessment"] is not null);
        Assert.Equal("issued", issued["reassessment"]!.ToString());
        Assert.NotNull(issued["requestId"]);
        Assert.NotNull(issued["reassessmentFingerprint"]);
        if (outcome != "complete")
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Empty(state.Construction.Checkpoints);
            Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAndIntermediateShareOneReassessmentAllowance(bool producer)
    {
        var state = producer ? await ProducerState() : await State(large: true);
        state.Construction!.Candidate!.NeedsIntermediateReview = true;
        var runtime = new TestRuntime { Respond = (_, _) => Response(inconclusive: "Need omitted declaration.") };
        state = await Advance(state, runtime);
        runtime.Respond = (_, _) => Response();
        state = await Advance(state, runtime);
        Assert.True(state.Construction!.Candidate!.LocalReviewed);
        Assert.Empty(state.Construction.Checkpoints);
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.IntermediatePurpose, purpose);
            return Response(inconclusive: "Review still incomplete.");
        };
        state = await Advance(state, runtime);
        Assert.Equal(3, state.ModelCalls);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Single(state.Construction!.Candidate!.Reviews, entry => entry["reassessment"] is not null);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("host")]
    [InlineData("size")]
    public async Task ReassessmentMustFitRemainingAdmissionBeforeEvidenceIsRetained(string limit)
    {
        var state = await State(large: true);
        if (limit == "calls") state.Request.MaxModelCalls = 1;
        if (limit == "size")
        {
            state.Requirements!.Inputs![0].Type.Enum = [new string('z', 100000)];
            Stage(state);
        }
        var runtime = new TestRuntime { Respond = (_, _) => Response(inconclusive: "Need exact omitted constraints.") };
        if (limit == "host") runtime.AdmitConstruction = (_, _, _) => throw new WorkflowRuntimeException("LLM_BUDGET_EXCEEDED", "Retained allowance exhausted.");
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(1, state.ModelCalls);
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.All(state.Construction.Candidate!.Reviews, entry => Assert.Null(entry["reassessment"]));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InterruptedIssuedReviewResumesExactRequestAndConsumesReassessmentOnce(bool reassessment, bool producer)
    {
        var state = producer ? await ProducerState() : await State(large: reassessment);
        var runtime = new TestRuntime { Respond = (_, _) => Response(inconclusive: "Need exact omitted constraints.") };
        if (reassessment) state = await Advance(state, runtime);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
        var calls = state.ModelCalls;
        var request = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        if (reassessment) Assert.Equal("issued", Assert.Single(state.Construction!.Candidate!.Reviews)["reassessment"]!.ToString());
        runtime.Respond = (issued, _) =>
        {
            Assert.Equal(request, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Response();
        };
        state = await Advance(state, runtime);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Null(state.PendingCall);
    }

    private static async Task<PlanningSession> TaskState()
    {
        var state = await State();
        state.Plan = new() { Inputs = state.Requirements!.Inputs!, Root = new() { Tasks =
            [new() { Id = "accepted_label", Kind = "value", Objective = "Retain the caller label.",
                Outputs = [new("value", new() { Kind = "input", Source = "label" })] }] } };
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Id = "use_label"; step.Kind = "value"; step.Objective = "Use the retained caller label.";
        step.RequirementIds = ["work"]; step.CloseRequirementIds = [];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], Tasks =
            [new() { Id = "use_label", Kind = "value", Objective = step.Objective,
                Outputs = [new("value", new() { Kind = "output", Source = "accepted_label", Port = "value" })] }] });
        return state;
    }

    private static async Task<PlanningSession> ProducerState(string use = "output")
    {
        var state = await TaskState();
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        var producer = state.Plan!.Root.Tasks.Single();
        state.Plan.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Keep independent accepted work.",
            Outputs = [new("value", new() { Kind = "string", Text = "Independent" })] });
        var extension = construction.Candidate!.Extension;
        if (use == "closure")
        {
            var tasks = state.Plan.Root.Tasks;
            state.Plan.Root.Tasks = [new() { Id = "container", Kind = "sequence", Objective = "Produce the caller label.",
                Body = new() { Tasks = tasks, Outputs = [new("value", new() { Kind = "output", Source = "accepted_label", Port = "value" })] } }];
            step.Scope = "/tasks/container/body"; step.Kind = "close"; step.CloseRequirementIds = ["work"];
            construction.OpenScopes.Add(step.Scope);
            extension = new() { Scope = step.Scope, RequirementIds = ["work"], CloseScopes = [step.Scope] };
        }
        else if (use == "draft")
        {
            state.Plan.Root.Tasks.Remove(producer);
            extension.Tasks.Insert(0, producer);
        }
        else if (use is "present" or "unrelated")
            extension.Tasks[0].Outputs[0] = new("value", use == "present" ? new() { Kind = "present", Source = "accepted_label" }
                : new() { Kind = "input", Source = "label" });
        else if (use == "field")
        {
            producer.Outputs[0] = new("value", new() { Kind = "object", Members =
                [new("payload", new() { Kind = "object", Members = [new("label", new() { Kind = "input", Source = "label" })] })] });
            extension.Tasks[0].Outputs[0] = new("value", new() { Kind = "field", Port = "label", Items =
                [new() { Kind = "field", Port = "payload", Items = [new() { Kind = "output", Source = "accepted_label", Port = "value" }] }] });
        }
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        construction.Candidate = PlanningConstruction.Apply(state, extension);
        return state;
    }

    private static JsonObject FindingProperties(JsonObject schema) => schema["properties"]!["findings"]!["anyOf"]!.AsArray()
        .Single(node => node?["type"]?.ToString() == "array")!["items"]!["properties"]!.AsObject();

    private static JsonObject Finding(string location, params string[] evidence) => new()
    {
        ["kind"] = "inconsistency", ["location"] = location,
        ["evidence"] = new JsonArray(evidence.Select(pointer => (JsonNode?)JsonValue.Create(pointer)).ToArray()),
        ["explanation"] = "The candidate does not fulfill the cited original obligation.", ["uncertainty"] = "Business interpretation."
    };

    private static async Task<PlanningSession> State(bool large = false)
    {
        const string original = "Accept a caller string named label with the declared allowed values. Use it in subsequent work.";
        var state = new PlanningSession { Request = new() { TenantId = "verified-judge", Prompt = original } };
        PlanningSessionCreation.Capture(state);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        construction.CandidateFailureIdentityVersion = 1;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var page in construction.Compact!.Sources) page.Indexed = true;
        var source = new PlanningConstructionSource { Id = "declared-label", Kind = "clause", Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original), Indexed = true, InterfaceKind = "input" };
        construction.Compact.Sources.Add(source);
        construction.Compact.RequirementSources["declare"] = [source.Id];
        construction.Compact.RequirementSources["work"] = [source.Id];
        construction.Compact.InterfaceExtractions = [new() { Id = "extract", SourceIds = [source.Id], Inputs = true, Complete = true,
            Dispositions = [new() { SourceId = source.Id, Version = source.Version, Kind = "interfaces", Inputs = ["label"],
                Reason = "The original names the caller declaration.", Evidence = new() { [source.Id] = source.Version } }] }];
        state.Requirements = new() { Inputs = [new() { Name = "label", Type = new() { Kind = "string", Enum = [large ? new string('z', 4500) : "allowed"] } }],
            Outputs = [], Outcomes = [new("declare", "Accept the declared caller string."), new("work", "Use it in subsequent work.")] };
        construction.Requirements = [new() { Id = "declare" }, new() { Id = "work" }];
        construction.OpenScopes = ["/root"];
        construction.Compact.CurrentStep = new() { Id = "confirm_inputs", Kind = "close", Scope = "/root", Objective = "Confirm the declared caller inputs.",
            RequirementIds = ["declare"], CloseRequirementIds = ["declare"], DiscoveryComplete = true };
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        Stage(state);
        return state;
    }

    private static void Stage(PlanningSession state)
    {
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["declare"] });
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Response(JsonArray? findings = null, string? inconclusive = null, string[]? addresses = null)
    {
        var json = new JsonObject { ["findings"] = inconclusive is null && addresses is null ? findings ?? new JsonArray() : null,
            ["evidenceRequests"] = addresses is null ? null : new JsonArray(addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["inconclusive"] = inconclusive, ["uncertainty"] = "Runtime execution remains unverified." };
        return new() { Json = json, Text = json.ToJsonString() };
    }
}
