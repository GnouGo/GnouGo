using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CommitmentCoverageTests
{
    [Theory]
    [InlineData("direct", "verified")]
    [InlineData("alias", "verified")]
    [InlineData("field", "verified")]
    [InlineData("group", "verified")]
    [InlineData("fabricated", "violation")]
    [InlineData("caller", "violation")]
    [InlineData("inferred", "unverified")]
    [InlineData("inferred_whole", "unverified")]
    [InlineData("simulated", "violation")]
    [InlineData("uncalled", "violation")]
    [InlineData("conditional", "unverified")]
    [InlineData("repeated", "unverified")]
    public async Task CoverageUsesExactOperationsAndResultLineageNotTaskNamesOrTypes(string variant, string expected)
    {
        var (runtime, state) = await Fixture(); var plan = runtime.Proposal.Plan!;
        var original = plan.Root.Tasks[0];
        if (variant == "alias")
        {
            plan.Root.Tasks.Add(new() { Id = "copy", Kind = "value", Objective = "Return exact producer result", Outputs = [new("location", Output("perform", "location"))] });
            plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = Output("copy", "location") };
        }
        if (variant == "field") plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = new() { Kind = "field", Port = "location", Items = [new() { Kind = "output", Source = "perform" }] } };
        if (variant is "group" or "uncalled")
        {
            plan.Groups.Add(new() { Id = "work", Body = new() { Tasks = [original], Outputs = [new("location", Output("perform", "location"))] } });
            plan.Root.Tasks.Clear();
            if (variant == "group") { plan.Root.Tasks.Add(new() { Id = "invoke", Kind = "call", Objective = "Perform requested work", Group = "work" }); plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = Output("invoke", "location") }; }
            else plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = Text("not executed") };
        }
        if (variant == "fabricated") plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = Text("invented path") };
        if (variant == "caller")
        {
            plan.Inputs.Add(new() { Name = "supplied", Type = new() { Kind = "string" } });
            runtime.Proposal.Requirements!.Inputs = plan.Inputs;
            plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = new() { Kind = "input", Source = "supplied" } };
        }
        if (variant is "inferred" or "inferred_whole" or "simulated")
        {
            if (variant == "simulated") plan.Root.Tasks.Clear();
            plan.Root.Tasks.Add(new() { Id = "pretend", Kind = "transform", Mode = "interpret", Objective = "Perform the requested external work and return its path",
                Inputs = [new("source", variant == "simulated" ? Text("caller instructions only") :
                    variant == "inferred_whole" ? new() { Kind = "output", Source = "perform" } : Output("perform", "location"))],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "location", Type = new() { Kind = "string" } }] } });
            plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = Output("pretend", "location") };
        }
        if (variant == "conditional")
        {
            plan.Root.Tasks = [new() { Id = "optional", Kind = "conditional", Objective = "Conditional work", Condition = new() { Kind = "boolean", Boolean = true },
                Body = new() { Tasks = [original], Outputs = [new("location", Output("perform", "location"))] },
                Otherwise = new() { Outputs = [new("location", Text("absent"))] } }];
            plan.Root.Outputs[0] = plan.Root.Outputs[0] with { Value = Output("optional", "location") };
        }
        if (variant == "repeated") plan.Root.Tasks.Add(new() { Id = "again", Objective = "Repeat same operation", Operation = original.Operation });
        Assert.Empty(new TaskPlanCompiler().Compile(plan, state.Catalog!, state.Request).Diagnostics);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(expected == "violation" ? PlanningStatus.Stopped : PlanningStatus.FinalReview, state.Status);
        Assert.True(state.ValidationResults.Count > 0, string.Join(";", state.Diagnostics));
        Assert.Equal(expected, Assert.Single(state.ValidationResults, r => r.Id == "coverage:deliver:0:result").Outcome);
        Assert.Contains(state.ValidationResults, r => r.Outcome == "unverified" && r.Id.EndsWith(":limits", StringComparison.Ordinal));
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        if (variant is "simulated" or "uncalled") Assert.Contains(state.Diagnostics, d => d.Code == PlanningCommitments.MissingOperation);
    }

    [Fact]
    public async Task MissingCommitmentsStayUnverifiedAfterExplicitHumanAcknowledgmentAndRestart()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.IntentVersion = null;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(3, state.IntentVersion); Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var coverage = Assert.Single(state.ValidationResults, r => r.Id.StartsWith("coverage:", StringComparison.Ordinal));
        Assert.Equal("unverified", coverage.Outcome);
        var hash = state.ComputeArtifactHash();
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { Kind = "approve", ExpectedRevision = state.Revision,
            ArtifactHash = hash, ReviewedRequirementIds = ["message"] }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Approved, state.Status); Assert.Equal(hash, state.ApprovedHash);
        Assert.Contains(state.ValidationResults, r => r.Id == coverage.Id && r.Outcome == "unverified");
        Assert.Contains(state.ValidationResults, r => r.Id == "requirement:message" && r.Outcome == "human_reviewed");
        PlanningArtifactApproval.Verify(PlannerFixture.Clone(state)); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task ApprovalRecomputesCoverageAndCannotTrustCachedSuccess()
    {
        var (runtime, state) = await Fixture(); state = await PlannerFixture.RunAsync(runtime, state);
        state.Plan!.Root.Outputs[0] = state.Plan!.Root.Outputs[0] with { Value = Text("forged") };
        // Recompile a consistent executable artifact: rejection must be coverage, not stale YAML.
        var changed = PlannerFixture.Clone(state); changed.IntentVersion = 2; changed.Status = PlanningStatus.Generating;
        runtime.Proposal = new() { Plan = changed.Plan };
        changed = await PlannerFixture.RunAsync(runtime, changed); changed.IntentVersion = 3;
        changed.ValidationResults = state.ValidationResults;
        var error = Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(changed));
        Assert.Contains("violated business commitment", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmissionKeepsHistoricalHashesAndV15YamlIdentical()
    {
        var (runtime, state) = await Fixture(); state.IntentVersion = 2;
        runtime.Proposal.Requirements!.Outcomes = [new("deliver", "Deliver the producer result")];
        var legacy = await PlannerFixture.RunAsync(runtime, state);
        var oldHash = legacy.ComputeArtifactHash();
        var serialized = JsonSerializer.Serialize(legacy.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        Assert.DoesNotContain("commitments", serialized);
        Assert.Equal(oldHash, PlannerFixture.Clone(legacy).ComputeArtifactHash());
        var (_, fresh) = await Fixture(); var upgraded = await PlannerFixture.RunAsync(runtime, fresh);
        Assert.Equal(legacy.Yaml, upgraded.Yaml); Assert.NotEqual(oldHash, upgraded.ComputeArtifactHash());
        Assert.DoesNotContain(legacy.ValidationResults, r => r.Id.StartsWith("coverage:", StringComparison.Ordinal));
        Assert.Null(upgraded.OutcomeBindings);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("port")]
    [InlineData("output")]
    [InlineData("duplicate")]
    public async Task InvalidObligationsNeverBecomeEvidence(string variant)
    {
        var (runtime, state) = await Fixture(); var outcome = runtime.Proposal.Requirements!.Outcomes[0];
        var original = outcome.Commitments![0];
        var invalid = outcome with { Commitments = variant switch
        {
            "operation" => [new("uninspected")], "port" => [original with { Result = new("made_up", "filePath") }],
            "output" => [original with { Result = new("location", "undeclared") }], _ => [original, original]
        } };
        // Mutate an already transported valid response, so invalid alternative
        // values test host admission rather than the scripted schema encoder.
        runtime.Respond = (request, _) =>
        {
            var response = TestRuntime.Response(request, runtime.Proposal);
            response.Json!["requirements"]!["outcomes"]![0]!["commitments"] =
                JsonSerializer.SerializeToNode(invalid, PlanningJsonContext.Default.PlanningRequirement)!["commitments"]!.DeepClone();
            return response;
        };
        state.Request.MaxModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.NotEqual(PlanningStatus.FinalReview, state.Status); Assert.Null(state.Yaml); Assert.Single(runtime.Calls);
        Assert.Null(state.Requirements); Assert.Null(state.Plan);
        Assert.Contains(state.Diagnostics, d => d.Code == (variant is "operation" or "port" ? "PLANNING_RESPONSE_INVALID" : "REQUIREMENTS_COMMITMENT_INVALID"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryMayCompleteButNeverReplaceCapturedCommitments(bool acceptedPlan)
    {
        var (runtime, state) = await Fixture();
        state.Requirements = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements, PlanningJsonContext.Default.PlanningRequirements)!
            .Deserialize(PlanningJsonContext.Default.PlanningRequirements)!;
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Commitments = null };
        if (acceptedPlan)
        {
            state.Request.Baseline = runtime.Proposal.Plan;
            Assert.False(PlanningCommitments.SameOutcomes(state, runtime.Proposal.Requirements!));
            runtime.Respond = (request, _) =>
            {
                var response = TestRuntime.Response(request, runtime.Proposal);
                // The issued schema already prohibits replacing accepted requirements.
                response.Json!["requirements"] = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements, PlanningJsonContext.Default.PlanningRequirements);
                return response;
            };
        }
        state.Request.MaxModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(acceptedPlan ? PlanningStatus.Stopped : PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls); Assert.Null(state.ApprovedHash);
        if (acceptedPlan)
        {
            Assert.Null(state.Requirements!.Outcomes[0].Commitments);
            Assert.Contains(state.Diagnostics, d => d.Code is "REQUIREMENTS_CHANGED" or "PLANNING_RESPONSE_INVALID");
        }
        else Assert.Single(state.Requirements!.Outcomes[0].Commitments!);
    }

    [Fact]
    public async Task FreshSchemaDeclaresOnlyNonExecutableObligationsAndKeepsRequestsAdmitted()
    {
        var (runtime, state) = await Fixture(); state = await PlannerFixture.RunAsync(runtime, state);
        var request = Assert.Single(runtime.Calls); var schema = request.StructuredOutputSchema!;
        Assert.Contains("commitments", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Non-executable obligations", schema.ToJsonString(), StringComparison.Ordinal); Assert.Null(schema["properties"]!["outcomeBindings"]);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(request.Prompt, schema.AsObject()) <= state.Request.Generation.MaxInputTokensPerRequest);
        Assert.Empty(PlanningCommitments.Validate(state, state.Requirements!, true));
        var clone = PlannerFixture.Clone(state);
        Assert.True(PlanningCommitments.SameOutcomes(clone, runtime.Proposal.Requirements!));
        clone.Requirements!.Outcomes[0].Commitments!.Clear();
        Assert.NotEqual(state.ComputeArtifactHash(), clone.ComputeArtifactHash());
    }

    internal static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture(string port = "location", string output = "filePath")
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("generic", new() { Tools = [new() { Name = "perform", EffectKind = "write",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [port] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray(port), ["additionalProperties"] = false } }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "perform")).Id;
        runtime.Proposal = new()
        {
            Requirements = new() { Summary = "Deliver the producer result", Inputs = [], Outputs = [new() { Name = output, Type = new() { Kind = "string" } }],
                Outcomes = [new("deliver", "Deliver the producer result") { Commitments = [new(operation) { Result = new(port, output) }] }] },
            Plan = new() { Root = new() { Tasks = [new() { Id = "perform", Objective = "Perform requested work", Operation = operation }],
                Outputs = [new(output, Output("perform", port))] } }
        };
        var state = PlannerFixture.Session(); state.IntentVersion = 3; state.Catalog = catalog;
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        return (runtime, state);
    }
    internal static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
    internal static TaskValue Text(string text) => new() { Kind = "string", Text = text };
}
