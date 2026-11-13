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
        state.Plan = plan;
        state.Requirements = runtime.Proposal.Requirements;
        state.ValidationResults = PlanningCommitments.Assess(state);
        Assert.NotEmpty(state.ValidationResults);
        Assert.Equal(expected, Assert.Single(state.ValidationResults, r => r.Id == "coverage:deliver:0:result").Outcome);
        Assert.Contains(state.ValidationResults, r => r.Outcome == "unverified" && r.Id.EndsWith(":limits", StringComparison.Ordinal));
        Assert.Empty(runtime.Calls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        if (variant is "simulated" or "uncalled") Assert.Contains(state.ValidationResults.SelectMany(result => result.Diagnostics), d => d.Code == PlanningCommitments.MissingOperation);
    }


    [Fact]
    public async Task CoverageRecomputesLineageAndCannotTrustCachedSuccess()
    {
        var (runtime, state) = await Fixture(); state.Plan = runtime.Proposal.Plan; state.Requirements = runtime.Proposal.Requirements;
        state.ValidationResults = PlanningCommitments.Assess(state);
        Assert.Equal("verified", Assert.Single(state.ValidationResults, row => row.Id == "coverage:deliver:0:result").Outcome);
        state.Graph = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Graph!;
        PlanningConfirmationGuards.Apply(state.Graph, state.Catalog!);
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog!);
        var hash = state.ComputeArtifactHash(); state.Plan!.Root.Outputs[0] = state.Plan.Root.Outputs[0] with { Value = Text("forged") };
        Assert.NotEqual(hash, state.ComputeArtifactHash());
        Assert.Equal("verified", Assert.Single(state.ValidationResults, row => row.Id == "coverage:deliver:0:result").Outcome);
        Assert.Equal("violation", Assert.Single(PlanningCommitments.Assess(state), row => row.Id == "coverage:deliver:0:result").Outcome);
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("port")]
    [InlineData("output")]
    [InlineData("duplicate")]
    public async Task InvalidObligationsNeverBecomeEvidence(string variant)
    {
        var (runtime, state) = await Fixture(); var requirements = runtime.Proposal.Requirements!;
        var outcome = requirements.Outcomes[0]; var original = outcome.Commitments![0];
        requirements.Outcomes[0] = outcome with { Commitments = variant switch
        {
            "operation" => [new("uninspected")], "port" => [original with { Result = new("made_up", "filePath") }],
            "output" => [original with { Result = new("location", "undeclared") }], _ => [original, original]
        } };
        Assert.Contains(PlanningCommitments.Validate(state, requirements, true), diagnostic => diagnostic.Code == "REQUIREMENTS_COMMITMENT_INVALID");
        Assert.Empty(runtime.Calls); Assert.Null(state.Plan); Assert.Null(state.Requirements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitmentCompletionNeverReplacesAnAcceptedPlan(bool acceptedPlan)
    {
        var (runtime, state) = await Fixture();
        state.Requirements = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements, PlanningJsonContext.Default.PlanningRequirements)!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)!;
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Commitments = null };
        if (acceptedPlan) state.Request.Baseline = runtime.Proposal.Plan;
        Assert.Equal(!acceptedPlan, PlanningCommitments.SameOutcomes(state, runtime.Proposal.Requirements!));
        Assert.Null(state.Requirements.Outcomes[0].Commitments); Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task CommitmentIdentityIsSeparateFromExecutableTaskPlanIdentity()
    {
        var (runtime, state) = await Fixture(); state.Plan = runtime.Proposal.Plan; state.Requirements = runtime.Proposal.Requirements;
        Assert.Empty(PlanningCommitments.Validate(state, state.Requirements!, true));
        state.Graph = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Graph!;
        PlanningConfirmationGuards.Apply(state.Graph, state.Catalog!);
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog!);
        var clone = PlannerFixture.Clone(state); var planHash = PlanningConstruction.Hash(state.Plan);
        Assert.True(PlanningCommitments.SameOutcomes(clone, runtime.Proposal.Requirements!));
        clone.Requirements!.Outcomes[0].Commitments!.Clear();
        Assert.NotEqual(state.ComputeArtifactHash(), clone.ComputeArtifactHash());
        Assert.Equal(planHash, PlanningConstruction.Hash(clone.Plan)); Assert.Empty(runtime.Calls);
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
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        return (runtime, state);
    }
    internal static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
    internal static TaskValue Text(string text) => new() { Kind = "string", Text = text };
}
