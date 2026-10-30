using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessReviewProtocolTests
{
    [Fact]
    public async Task RetainedCompletionClaimIsVisibleButCannotBeSelectedAgainOnUnchangedPlan()
    {
        var (state, runtime) = await Ready();
        state.Construction!.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        runtime.Respond = (_, _) => throw new InvalidOperationException("These technical contributions defer review to a coherent block or finalization.");
        state = await Advance(state, runtime);
        var baseline = PlanningConstruction.Hash(state.Plan);
        state.Construction!.Compact!.CurrentStep = new() { Id = "claim", BaseHash = baseline, Scope = "/root", Kind = "close",
            Objective = "Propose completion of the later obligation using the accepted result.", RequirementIds = ["later"], CloseRequirementIds = ["later"],
            Inputs = [new() { Kind = "output", Source = "compute", Port = "result" }], InputTaskIds = ["compute"], DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"] });
        state = await Advance(Clone(state), runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.True(PlanningCompactConstruction.RequirementCompletionClaimed(state, "later"));
        Assert.False(PlanningCompactConstruction.CanClaimRequirementCompletion(state, "later"));
        Assert.NotEqual("closed", state.Construction.Requirements.Single(requirement => requirement.Id == "later").Status);
        var row = PlanningCompactConstruction.OriginalRequirementRows(state).Single(item => item!["id"]!.ToString() == "later")!;
        Assert.True(row["acceptedProgress"]!["completionClaimPendingReview"]!.GetValue<bool>());
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        var choices = Nodes(schema).OfType<JsonObject>().Select(node => node["properties"]?["closeRequirementIds"])
            .OfType<JsonObject>().SelectMany(Values).ToArray();
        Assert.Contains("work", choices);
        Assert.DoesNotContain("later", choices);

        IEnumerable<string> Values(JsonNode node)
        {
            if (node is JsonObject obj && obj["$ref"] is JsonValue reference)
            {
                Assert.True(PlanningConstruction.TryPointer(schema, reference.ToString()[1..], out var resolved));
                return Values(resolved!);
            }
            return Nodes(node).OfType<JsonObject>().SelectMany(obj => obj["enum"] as JsonArray ?? [])
                .OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? text : null).OfType<string>();
        }
        static IEnumerable<JsonNode> Nodes(JsonNode node)
        {
            yield return node;
            var children = node is JsonObject obj ? obj.Select(pair => pair.Value) : node is JsonArray array ? array : [];
            foreach (var child in children.OfType<JsonNode>()) foreach (var nested in Nodes(child)) yield return nested;
        }
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("stale")]
    public async Task PendingRepairDoesNotGrantAuthorityOverReviewedOrChangedCheckpointEvidence(string change)
    {
        var (state, runtime) = await Ready();
        state.Construction!.Compact!.CurrentStep!.CloseRequirementIds = [];
        state = await Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        if (change == "reviewed") checkpoint.Fragment["businessReviewPending"] = false;
        else checkpoint.Fragment["tasks"]![0]!["objective"] = "A different retained implementation.";
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
        state.Construction.Compact!.CurrentStep = new() { Id = "complete", Kind = "close", Scope = "/root", RequirementIds = ["work", "later"],
            CloseRequirementIds = ["work", "later"], CloseScopes = ["/root"], OutputNames = ["result"],
            Objective = "Return the computed result.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work", "later"],
            Outputs = [new("result", new() { Kind = "output", Source = "compute", Port = "result" })], CloseScopes = ["/root"], Complete = true });
        runtime.Respond = (_, _) => new() { Json = Change() };
        state = await Advance(state, runtime);
        Assert.Null(state.Construction!.Candidate!.AuthorizedPaths);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "BUSINESS_REVIEW_REQUIRED");
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintainedRealFindingRepairsEarlierPendingCheckpointAndPreservesIndependentWork(bool downstream)
    {
        var (state, runtime) = await Ready();
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Tasks[0].Outputs[0].Value.Items[1].Number = 3;
        if (downstream)
        {
            candidate.Plan.Root.Tasks.Add(new() { Id = "consumer", Kind = "value", Objective = "Use the computed result unchanged.",
                Outputs = [new("result", new() { Kind = "output", Source = "compute", Port = "result" })] });
            candidate.TaskIds.Add("consumer");
        }
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        state.Construction.Compact!.CurrentStep!.CloseRequirementIds = [];
        state = await Advance(state, runtime); // Technical acceptance deliberately precedes business review.
        state.Construction!.Compact!.CurrentStep = new() { Id = "independent", Kind = "value", Scope = "/root", RequirementIds = ["later"],
            Objective = "Build the independent numeric result.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"], Tasks = [new()
        { Id = "independent", Kind = "value", Objective = "Keep the independent constant.", Outputs = [new("value", new() { Kind = "number", Number = 42 })] }] });
        state = await Advance(state, runtime);
        var independent = state.Construction!.Checkpoints[1];
        var independentHash = independent.PlanHash; var independentBytes = independent.Fragment["tasks"]!.ToJsonString();
        state.Construction.Compact!.CurrentStep = new() { Id = "complete", Kind = "close", Scope = "/root", RequirementIds = ["work", "later"],
            CloseRequirementIds = ["work", "later"], CloseScopes = ["/root"], OutputNames = ["result"],
            Objective = "Return the doubled input.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work", "later"],
            Outputs = [new("result", new() { Kind = "output", Source = downstream ? "consumer" : "compute", Port = "result" })], CloseScopes = ["/root"], Complete = true });
        var consumerBytes = downstream ? PlanningConstruction.TaskNode(state.Construction.Candidate.Plan.Root.Tasks[1]).ToJsonString() : null;
        var reviews = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == "replan")
            {
                var context = PlanningRepairPatch.RequestContext(request);
                Assert.NotNull(context["defectProgress"]);
                var slot = context["repair"]!["slots"]!.AsArray().Single(slot => slot!["location"]!.ToString() == "/tasks/compute/outputs/result")!;
                return new() { Json = PlanningCorpus.Transport(new JsonObject { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = new JsonObject { ["kind"] = "arithmetic", ["text"] = "multiply",
                    ["items"] = new JsonArray(new JsonObject { ["kind"] = "input", ["source"] = "x" }, new JsonObject { ["kind"] = "number", ["number"] = 2 }) } }) } },
                    request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
            }
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            reviews++;
            var report = reviews == 1 ? Change() : Report("ok");
            if (reviews == 1 && downstream) report["decision"]!["targets"]!.AsArray().Add((JsonNode?)JsonValue.Create("consumer"));
            return new() { Json = report };
        };
        state = await Advance(state, runtime);
        Assert.Contains("/tasks/compute/outputs/result", state.Construction!.Candidate!.AuthorizedPaths!);
        state = await Advance(Clone(state), runtime);
        Assert.Equal(1, state.ReplanAttempts); Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Construction!.Candidate!.Plan.Root.Tasks[0].Outputs[0].Value.Items[1].Number);
        if (downstream)
        {
            Assert.Equal(consumerBytes, PlanningConstruction.TaskNode(state.Construction.Candidate.Plan.Root.Tasks[1]).ToJsonString());
            Assert.False(state.Construction.Candidate.BusinessReviewed);
        }
        state = await Advance(Clone(state), runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, reviews); Assert.Equal(3, state.ModelCalls);
        Assert.Contains("compute", state.Construction!.Checkpoints[0].InvalidatedTaskIds);
        Assert.Empty(state.Construction.Checkpoints[1].InvalidatedTaskIds);
        Assert.Equal(independentHash, state.Construction.Checkpoints[1].PlanHash);
        Assert.Equal(independentBytes, state.Construction.Checkpoints[1].Fragment["tasks"]!.ToJsonString());
        PlanningArtifactApproval.Verify(state);
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["x"] = 7 }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(14, result.Outputs!["result"]!.GetValue<int>());
    }

    [Fact]
    public async Task VerdictSchemaExcludesEmptyActionsAndUnsupportedContextAddresses()
    {
        var (state, _) = await Ready();
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var schema = request.StructuredOutputSchema!;
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(Report("ok"), schema));
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(Change(), schema));
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(Context("task:compute"), schema));
        foreach (var invalid in new JsonNode[]
        {
            new JsonObject(), new JsonObject { ["decision"] = null },
            new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "Missing input", ["obligations"] = new JsonArray(), ["targets"] = new JsonArray("compute") } },
            Context("task:compute please inspect its argument"), Context("task:missing"),
            Change(target: "task:compute"), Change(target: "missing"),
            new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "Fine", ["contextRequests"] = new JsonArray("task:compute") } }
        }) Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(invalid, schema));
        Assert.DoesNotContain("acknowledgments", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sourceCoverage", context.ToJsonString(), StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, request.Prompt, schema.AsObject()),
            PlanningJsonContext.Default.LLMRequest)) < 13_197);
    }

    [Fact]
    public async Task FindingsOfferOnlyCanonicalOriginalAndImplementationIds()
    {
        var (state, runtime) = await Ready();
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var branches = request.StructuredOutputSchema!["properties"]!["decision"]!["anyOf"]!.AsArray();
        var change = Assert.Single(branches, branch => branch?["properties"]?["verdict"]?["enum"]?[0]?.ToString() == "needs_change")!;
        Assert.Equal(new[] { "later", "work" }, change["properties"]!["obligations"]!["items"]!["enum"]!.AsArray().Select(id => id!.ToString()));
        Assert.Contains("compute", change["properties"]!["targets"]!["items"]!["enum"]!.AsArray().Select(id => id!.ToString()));
        Assert.DoesNotContain("task:compute", change["properties"]!["targets"]!["items"]!["enum"]!.AsArray().Select(id => id!.ToString()));
        runtime.Respond = (issued, _) =>
        {
            Assert.Empty(PlanningModelCalls.ValidateResponseFindings(Change(), issued.StructuredOutputSchema!));
            return new() { Json = Change() };
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW" && diagnostic.Location == "/tasks/compute");
        Assert.Equal(0, state.ReplanAttempts);
        Assert.NotNull(state.Construction!.Candidate);
        foreach (var collection in new[] { "requirements", "globalConstraints" }) context.Remove(collection);
        var noOriginal = HybridWorkflowPlanner.ConstructionReviewSchema(context);
        Assert.DoesNotContain(noOriginal["properties"]!["decision"]!["anyOf"]!.AsArray(), branch =>
            branch?["properties"]?["verdict"]?["enum"]?[0]?.ToString() == "needs_change");
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(Report("ok"), noOriginal));
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(Context("task:compute"), noOriginal));
    }

    [Fact]
    public async Task ProjectionKeepsActualArgumentsAndOriginalMeaningWithoutSchemaTreesOrLabels()
    {
        var (state, _) = await Ready();
        var originalDescription = "Accept a pull request identity, not its repository identity. Append to the local report; never overwrite it.";
        var capability = new PlanningCapability
        {
            Id = "record_subject", Version = "one", StepType = "mcp.call", Server = "records", Method = "record", EffectKind = "write",
            Description = originalDescription,
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            {
                ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "The pull request identity." },
                ["unused"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(new string('z', 40_000)) }
            }, ["required"] = new JsonArray("subject"), ["additionalProperties"] = false },
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            {
                ["flat"] = new JsonObject { ["type"] = "string", ["description"] = "Only visible flat text." },
                ["complete"] = new JsonObject { ["type"] = new JsonArray("object", "null"), ["description"] = "The complete observation, including its page identity and continuation." }
            }, ["additionalProperties"] = false }
        };
        state.Catalog!.Capabilities.Add(capability); state.Catalog.AllowedStepTypes.Add("mcp.call"); state.Discovery.Resolved.Add(capability);
        var plan = state.Construction!.Candidate!.Plan;
        plan.Root.Tasks.Insert(0, new() { Id = "observe", Kind = "operation", Operation = capability.Id,
            Objective = "This label claims the repository is good enough.", Inputs = [new("subject", new() { Kind = "string", Text = "pr/7" })] });
        plan.Root.Tasks.Add(new() { Id = "consume", Kind = "value", Objective = "Retain the complete observation.", Outputs = [new("observation", new() { Kind = "output", Source = "observe", Port = "complete" })] });
        state.Construction.Candidate.TaskIds.AddRange(["observe", "consume"]);
        state.Construction.Candidate.Hash = PlanningConstruction.Hash(plan);
        var originalContract = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        var projection = PlanningBusinessProjection.Build(state, global: false);
        var operation = projection["actions"]!.AsArray().Single(action => action!["id"]!.ToString() == "observe")!;
        Assert.Equal(originalDescription, operation["meaning"]!.ToString());
        Assert.Equal("pr/7", operation["arguments"]![0]!["value"]!["text"]!.ToString());
        Assert.Equal("write", operation["effect"]!.ToString());
        Assert.DoesNotContain("label claims", projection.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 100), projection.ToJsonString(), StringComparison.Ordinal);
        var output = operation["ports"]!.AsArray().Single(port => port!["direction"]!.ToString() == "output")!;
        Assert.Equal("complete", output["name"]!.ToString());
        Assert.Equal("The complete observation, including its page identity and continuation.", output["description"]!.ToString());
        Assert.Equal(new[] { "object", "null" }, output["type"]!.AsArray().Select(type => type!.ToString()));
        Assert.Equal(originalContract, JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
        Assert.StartsWith("contract:", operation["contract"]!.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("port-page:", operation["outputDirectory"]!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedBoundaryReviewIncludesPreviouslyAcceptedPendingBehavior(bool ancestorCleanup)
    {
        var (state, _) = await Ready();
        var candidate = state.Construction!.Candidate!;
        var computation = candidate.Plan.Root.Tasks.Single();
        state.Plan!.Root.Tasks = [new() { Id = "sequence", Kind = "sequence", Objective = "Compute the bounded result.", Body = new() { Tasks = [computation] } }];
        if (ancestorCleanup)
        {
            state.Plan.Root.Tasks.Insert(0, new() { Id = "audit_source", Kind = "value", Objective = "Retain the caller value for finalization.",
                Outputs = [new("value", new() { Kind = "input", Source = "x" })] });
            state.Plan.Root.Always = [new() { Id = "audit_finalization", Kind = "value", Objective = "Record the supplied value even on failure.",
                Outputs = [new("value", new() { Kind = "output", Source = "audit_source", Port = "value" })] }];
        }
        state.Construction.OpenScopes.Add("/tasks/sequence/body");
        var scope = "/tasks/sequence/body";
        var step = state.Construction.Compact!.CurrentStep!;
        step.Scope = scope; step.Kind = "close"; step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.CloseRequirementIds = [];
        state.Construction.Checkpoints.Add(new() { Sequence = 1, TaskIds = ["compute"], RequirementIds = ["work"],
            Step = new() { Scope = scope }, Fragment = new JsonObject { ["businessReviewPending"] = true } });
        candidate = PlanningConstruction.Apply(state, new() { Scope = scope, CloseScopes = [scope], RequirementIds = ["work"] });
        state.Construction.Candidate = candidate;
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var projection = PlanningBusinessProjection.Build(state, global: false);
        Assert.Contains(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "compute");
        Assert.Contains(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "sequence");
        Assert.Equal(new[] { "work", "later" }, projection["requirements"]!.AsArray().Select(original => original!["id"]!.ToString()));
        Assert.False(projection["scopes"]!.AsArray().Single(row => row!["id"]!.ToString() == scope)!["open"]!.GetValue<bool>());
        var root = Assert.Single(projection["scopes"]!.AsArray(), row => row!["id"]!.ToString() == "/root")!;
        Assert.True(root["open"]!.GetValue<bool>());
        Assert.Empty(projection["claims"]!["completedRequirements"]!.AsArray());
        Assert.False(projection["claims"]!["workflowComplete"]!.GetValue<bool>());
        if (ancestorCleanup)
        {
            Assert.Equal("audit_finalization", Assert.Single(root["cleanup"]!.AsArray())!.ToString());
            Assert.Contains(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "audit_finalization");
            Assert.Contains(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "audit_source");
        }
        else Assert.Empty(root["cleanup"]!.AsArray());
    }

    [Fact]
    public async Task ContextRetrievalDoesNotSpendSameEvidenceInterpretationReconsideration()
    {
        var (state, runtime) = await Ready();
        var acceptedHash = PlanningConstruction.Hash(state.Plan);
        var candidateHash = state.Construction!.Candidate!.Hash;
        var judges = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == "replan")
            {
                var evidence = new JsonObject { ["evidenceRequests"] = new JsonArray(runtime.EvidenceId(request, "task:compute")) };
                if (request.StructuredOutputSchema?["properties"]?["baseHash"]?["enum"]?[0] is { } baseline)
                    evidence["baseHash"] = baseline.DeepClone();
                return new() { Json = PlanningCorpus.Transport(evidence, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
            }
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = PromptContext(request);
            judges++;
            var response = judges switch { 1 => Context(runtime.EvidenceId(request, "task:compute")), 2 => Change(), 3 => Report("ok"), _ => throw new InvalidOperationException("Unexpected extra business review.") };
            if (judges == 3)
            {
                Assert.Equal("needs_change", context["interpretationReconsideration"]!["priorReport"]!["decision"]!["verdict"]!.ToString());
                Assert.Equal(candidateHash, state.Construction!.Candidate!.Hash);
            }
            Assert.Empty(PlanningModelCalls.ValidateResponseFindings(response, request.StructuredOutputSchema!));
            return new() { Json = response };
        };
        state = await Advance(state, runtime); // The fragment needs exact context.
        Assert.NotNull(state.Construction!.Evidence!["task:compute"]);
        Assert.DoesNotContain(state.Construction.Candidate!.Reviews, row => row["outcome"]?.ToString() == "reconsideration_issued");
        state = Clone(state);
        state = await Advance(state, runtime); // Received but mistaken business interpretation.
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        Assert.Equal(acceptedHash, PlanningConstruction.Hash(state.Plan));
        state = await Advance(state, runtime); // Existing correction evidence-only action contests it.
        Assert.True(HybridWorkflowPlanner.HasPendingBusinessReconsideration(state),
            string.Join("; ", state.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Equal(candidateHash, state.Construction!.Candidate!.Hash);
        Assert.Equal(0, state.ReplanAttempts);
        state = Clone(state);
        state = await Advance(state, runtime); // Same plan/evidence, interpreted correctly.
        Assert.Equal(3, judges);
        Assert.Equal(4, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(candidateHash, PlanningConstruction.Hash(state.Plan));
        var checkpoint = Assert.Single(state.Construction.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        var ok = checkpoint.Reviews.Single(row => row["outcome"]?.ToString() == "ok");
        Assert.Equal("ok", ok["rawResponse"]!["decision"]!["verdict"]!.ToString());
        Assert.Contains(checkpoint.Reviews, row => row["outcome"]?.ToString() == "needs_context");
        Assert.Contains(checkpoint.Reviews, row => row["outcome"]?.ToString() == "needs_change");
        Assert.Single(checkpoint.Reviews, row => row["outcome"]?.ToString() == "reconsideration_issued");
    }

    [Theory]
    [InlineData("claims")]
    [InlineData("source")]
    [InlineData("response")]
    [InlineData("request")]
    public async Task AcceptedReviewCannotAuthorizeChangedFactsOrInventedReceipt(string change)
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (_, _) => new() { Json = Report("ok") };
        state = await Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        var row = checkpoint.Reviews.Single(review => review["outcome"]?.ToString() == "ok");
        if (change == "claims") checkpoint.Step!.Objective = "Different claimed business action";
        if (change == "source") state.Construction.Compact!.Sources[0].Text += "changed";
        if (change == "response") row["rawResponse"]!["decision"]!["reason"] = "A fabricated report";
        if (change == "request") row["requestHash"] = new string('a', 64);
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeveralSmallFragmentsCanShareOneFinalBusinessReviewBeforeApproval(bool reconsiderAcceptedFinding)
    {
        var (state, runtime) = await Ready();
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
        state.Construction!.Compact!.CurrentStep!.CloseRequirementIds = [];
        var globalCalls = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == "replan")
            {
                Assert.True(reconsiderAcceptedFinding);
                Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
                Assert.NotNull(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]);
                return new() { Json = PlanningCorpus.Transport(new JsonObject { ["evidenceRequests"] = new JsonArray("task:compute") },
                    request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()) };
            }
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            globalCalls++;
            var context = PromptContext(request);
            Assert.Equal("completed_workflow", context["review"]!.ToString());
            Assert.Equal(new[] { "compute", "finish" }, context["actions"]!.AsArray().Select(action => action!["id"]!.ToString()));
            Assert.Equal(new[] { "work", "later" }, context["requirements"]!.AsArray().Select(original => original!["id"]!.ToString()));
            Assert.Equal("finish", context["publicOutputs"]![0]!["value"]!["source"]!.ToString());
            return new() { Json = reconsiderAcceptedFinding && globalCalls == 1 ? Change() : Report("ok") };
        };
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.Single(state.Construction!.Checkpoints);
        state.Construction.Compact!.CurrentStep = new() { Id = "finish-step", Kind = "value", Scope = "/root", RequirementIds = ["later"],
            Objective = "Add the final independent calculation.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"], Tasks = [new()
        {
            Id = "finish", Kind = "value", Objective = "Add one to the doubled value.", Outputs = [new("result", new() { Kind = "arithmetic", Text = "add", Items =
                [new() { Kind = "output", Source = "compute", Port = "result" }, new() { Kind = "number", Number = 1 }] })]
        }] });
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        state.Construction.Compact!.CurrentStep = new() { Id = "close-root", Kind = "close", Scope = "/root", RequirementIds = ["work", "later"],
            CloseRequirementIds = ["work", "later"], CloseScopes = ["/root"], OutputNames = ["result"],
            Objective = "Return the constructed result and complete the workflow.", BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work", "later"],
            Outputs = [new("result", new() { Kind = "output", Source = "finish", Port = "result" })], CloseScopes = ["/root"], Complete = true });
        state = await Advance(state, runtime);
        if (reconsiderAcceptedFinding)
        {
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
            var unchanged = state.Construction!.Candidate!.Hash;
            state = await Advance(Clone(state), runtime);
            Assert.True(HybridWorkflowPlanner.HasPendingBusinessReconsideration(state));
            state = await Advance(Clone(state), runtime);
            Assert.Equal(unchanged, PlanningConstruction.Hash(state.Plan));
            Assert.Equal(0, state.ReplanAttempts);
        }
        Assert.Equal(reconsiderAcceptedFinding ? 3 : 1, runtime.Calls.Count);
        Assert.Equal(reconsiderAcceptedFinding ? 3 : 1, state.ModelCalls);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.NotNull(state.Graph); Assert.NotNull(state.Yaml);
        Assert.True(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        PlanningArtifactApproval.Verify(state);
        var global = state.Construction!.Checkpoints[^1].Reviews.Single(review => review["outcome"]?.ToString() == "ok");
        global["purpose"] = PlanningConstruction.LocalPurpose;
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    internal static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        const string first = "Double the supplied number. ";
        const string later = "The later independent result will be added next.";
        var state = new PlanningSession { Request = new() { TenantId = "business-review", Prompt = first + later,
            MaxModelCalls = 64, MaxReplanAttempts = 4, Generation = new("medium", 32_000, 8_192),
            Options = new() { ["automatic_correction"] = true } } };
        PlanningSessionCreation.Capture(state);
        var runtime = new TestRuntime();
        state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        state.Requirements = new() { Inputs = [new() { Name = "x", Type = new() { Kind = "number" } }], Outputs = [],
            Outcomes = [new("work", first), new("later", later)] };
        var compact = state.Construction!.Compact!;
        compact.Sources.Add(new() { Id = "request:0", Kind = "page", Indexed = true, Inputs = [], Outputs = [], Text = first + later, Version = Fingerprint(first + later) });
        compact.Sources.Add(new() { Id = "request:0:clause:0", Kind = "requirement", Indexed = true, Inputs = [], Outputs = [], Text = first, Version = Fingerprint(first) });
        compact.Sources.Add(new() { Id = "request:0:clause:1", Kind = "requirement", Indexed = true, Inputs = [], Outputs = [], Text = later, Version = Fingerprint(later) });
        compact.RequirementSources["work"] = ["request:0:clause:0"]; compact.RequirementSources["later"] = ["request:0:clause:1"];
        state.Construction.Requirements = [new() { Id = "work" }, new() { Id = "later" }];
        state.Plan = new() { Inputs = state.Requirements.Inputs.ToList() };
        compact.CurrentStep = new() { Id = "calculation", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "value",
            Objective = "Compute the actual doubled number.", RequirementIds = ["work"], CloseRequirementIds = ["work"], DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], Tasks = [new()
        {
            Id = "compute", Kind = "value", Objective = "Double the caller number.", Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply",
                Items = [new() { Kind = "input", Source = "x" }, new() { Kind = "number", Number = 2 }] })]
        }] });
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction.Candidate.Plan, state.Catalog, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        return (state, runtime);
    }

    private static string Fingerprint(string value) => PlanningGraphCompiler.Fingerprint(value);
    internal static PlanningSession Clone(PlanningSession state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    internal static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
    internal static JsonObject Report(string verdict) => new() { ["decision"] = new JsonObject { ["verdict"] = verdict, ["reason"] = "The original obligation is satisfied by the actual multiplication." } };
    private static JsonObject Change(string target = "compute") => new() { ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The multiplication should be checked against the original number.",
        ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray(target) } };
    private static JsonObject Context(string address) => new() { ["decision"] = new JsonObject { ["verdict"] = "needs_context", ["reason"] = "Read the exact computation.", ["contextRequests"] = new JsonArray(address) } };
    private static JsonObject PromptContext(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!.AsObject();
}
