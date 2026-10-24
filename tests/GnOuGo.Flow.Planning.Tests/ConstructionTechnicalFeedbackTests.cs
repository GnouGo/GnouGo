using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class ConstructionDefectProgressTests
{
    [Fact]
    public async Task TechnicalRepairThenBusinessReplacementRetainsOriginalValueWithoutGrantingAuthority()
    {
        var state = CoordinatorReady();
        var original = PlanningConstruction.Clone(state.Construction!.Candidate!.Plan).Root.Tasks.Single();
        var consume = state.Catalog!.Capabilities.Single();
        consume.InputSchema["properties"]!["resource"] = new JsonObject { ["type"] = "string" };
        consume.InputSchema["required"]!.AsArray().Add((JsonNode)JsonValue.Create("resource")!);
        consume.ArtifactContract = new(1, [], [new("resource", "/resource", true)]);
        var produce = new PlanningCapability { Id = "produce", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "synthetic",
            Method = "produce", EffectKind = "read", Description = "Create the resource.", InputSchema = new() { ["type"] = "object" },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["handle"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("handle") }, ArtifactContract = new(1, [new("resource", "/handle", "materialize")], []) };
        state.Catalog.Capabilities.Add(produce); state.Discovery.Resolved.Add(produce);
        state.Plan!.Root.Tasks.Add(new() { Id = "producer", Kind = "operation", Operation = produce.Id, Objective = "Create resource" });
        var accepted = PlanningConstruction.Hash(state.Plan);
        var step = state.Construction.Compact!.CurrentStep!;
        step.BaseHash = accepted; step.InputTaskIds = ["producer"];
        step.Inputs = [new() { Kind = "output", Source = "producer", Port = "handle" }];
        original.Inputs.Add(new("resource", new() { Kind = "string", Text = "unproven-resource" }));
        state.Construction.Candidate = null;
        var calls = 0; Exception? fixtureFailure = null;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            try
            {
                calls++;
                var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
                JsonObject response;
                if (calls is 1 or 4)
                {
                    Assert.Equal(calls == 1 ? PlanningConstruction.DraftPurpose : "replan", purpose);
                    if (calls == 4)
                    {
                        var history = Assert.Single(context["historicalTechnicalRejections"]!["entries"]!.AsArray())!;
                        Assert.Equal("/tasks/consumer/inputs/resource", history["diagnostic"]!["value"]!["location"]!.ToString());
                        Assert.Equal("unproven-resource", history["rejectedValue"]!["value"]!["text"]!.ToString());
                        Assert.Equal("historical", history["status"]!.ToString());
                        Assert.DoesNotContain("TASK_ARTIFACT_BINDING", context["diagnostics"]!.ToJsonString(), StringComparison.Ordinal);
                        Assert.All(state.Diagnostics, d => Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", d.Code));
                        Assert.Null(state.Construction!.Candidate!.AuthorizedPaths);
                        Assert.Empty(state.Construction.Checkpoints);
                        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
                        original.Inputs[0] = new("text", new() { Kind = "input", Source = "text" });
                        original.Inputs[1] = new("resource", new() { Kind = "output", Source = "producer", Port = "handle" });
                    }
                    response = new() { ["baseHash"] = accepted, ["extension"] =
                        JsonSerializer.SerializeToNode(new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["req_0"], Tasks = [original] },
                            PlanningJsonContext.Default.PlanningConstructionExtension) };
                }
                else if (calls == 2)
                {
                    Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
                    var slot = context["repair"]!["slots"]!.AsArray().Single(item => item!["location"]!.ToString() == "/tasks/consumer/inputs/resource")!;
                    Assert.DoesNotContain("passed complete original-contract validation", context["operations"]!.ToJsonString(), StringComparison.Ordinal);
                    response = new() { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                    { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "producer", ["port"] = "handle" } }) } };
                }
                else
                {
                    Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
                    Assert.Null(context["historicalTechnicalRejections"]);
                    Assert.Empty(state.Diagnostics);
                    var action = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() };
                    if (calls == 3)
                    {
                        var finding = Finding("/requirements/0"); finding.Remove("evidence");
                        finding["obligation"] = "/requirements/0"; finding["implementation"] = "/draft/tasks/0"; finding["requiredBinding"] = null;
                        action["findings"]!.AsArray().Add((JsonNode)finding);
                    }
                    else
                    {
                        Assert.Equal(5, calls);
                        var prior = Assert.Single(context["defectProgress"]!.AsArray())!;
                        action["resolutions"] = new JsonArray(new JsonObject { ["id"] = prior["id"]!.DeepClone(), ["status"] = "resolved",
                            ["obligation"] = "/requirements/0", ["implementation"] = "/draft/tasks/0",
                            ["explanation"] = "The consumer now uses the original caller text and the declared resource producer." });
                    }
                    response = new() { ["action"] = action, ["uncertainty"] = "Synthetic review, no execution." };
                }
                var wire = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject());
                return new() { Json = wire, Text = wire!.ToJsonString() };
            }
            catch (Exception error) { fixtureFailure = error; throw; }
        } };
        state = await Advance(state);
        Assert.True(fixtureFailure is null, fixtureFailure?.ToString());
        Assert.NotEmpty(state.Diagnostics);
        var diagnostic = state.Diagnostics[0];
        Assert.True(diagnostic.Location == "/tasks/consumer/inputs/resource", diagnostic.Code + ": " + diagnostic.Message);
        Assert.Single(state.Construction!.Candidate!.Reviews);
        state = await Advance(PlannerFixture.Clone(state));
        Assert.Empty(state.Diagnostics); Assert.Null(PlanningConstruction.TechnicalRejectionContext(state));
        state = await Advance(PlannerFixture.Clone(state));
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        state = await Advance(PlannerFixture.Clone(state));
        Assert.Equal(4, calls); Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.ReplanAttempts); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        var retained = state.Construction.Candidate!.Reviews.Single(review => review["technicalRejections"] is not null);
        Assert.Equal(diagnostic.Code, retained["technicalRejections"]!["entries"]![0]!["diagnostic"]!["value"]!["code"]!.ToString());
        Assert.Null(retained["purpose"]); Assert.Null(retained["response"]);
        state = await Advance(PlannerFixture.Clone(state));
        Assert.True(fixtureFailure is null, fixtureFailure?.ToString());
        Assert.Equal(5, calls); Assert.Single(state.Construction!.Checkpoints);
        Assert.Null(state.Construction.Candidate); Assert.Null(state.Construction.Compact!.ReplacementScope);
        state.Construction.Compact.CurrentStep = new() { Id = "later", Kind = "operation", Scope = "/root", RequirementIds = ["req_0"], OperationIds = [consume.Id] };
        var unrelated = new PlanningConstructionCandidate();
        PlanningConstruction.CarryDefectWitnesses(state, null, unrelated);
        Assert.Empty(unrelated.Reviews); Assert.Null(PlanningConstruction.TechnicalRejectionContext(state));

        Task<PlanningSession> Advance(PlanningSession current) => new HybridWorkflowPlanner().AdvanceAsync(current,
            new() { ExpectedRevision = current.Revision }, runtime, PlannerFixture.Ct);
    }

    [Fact]
    public void ExactNamedValueSurvivesReselectionButDoesNotLeakIntoUnrelatedWork()
    {
        var state = CoordinatorReady(); RetainTechnical(state);
        var serialized = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        state = PlannerFixture.Clone(state);
        Assert.Equal(serialized, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        PlanningCompactConstruction.Reselect(state, "Choose another implementation within the same obligation.");
        Assert.NotNull(state.Construction!.Compact!.RejectedSteps[0]["reviews"]![0]!["technicalRejections"]);
        state.Construction.Compact.CurrentStep = new() { Id = "replacement", Kind = "operation", Scope = "/root", RequirementIds = ["req_0"], OperationIds = ["operation@exact"] };
        var next = new PlanningConstructionCandidate { Plan = PlanningConstruction.Clone(state.Plan!) };
        PlanningConstruction.CarryDefectWitnesses(state, null, next);
        Assert.Single(next.Reviews);
        Assert.NotNull(PlanningConstruction.TechnicalRejectionContext(state));
        state.Construction.Compact.ReplacementScope = null;
        state.Construction.Compact.ReplacementRequirements = null;
        var unrelated = new PlanningConstructionCandidate();
        PlanningConstruction.CarryDefectWitnesses(state, null, unrelated);
        Assert.Empty(unrelated.Reviews); Assert.Null(PlanningConstruction.TechnicalRejectionContext(state));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("contract")]
    [InlineData("baseline")]
    public void ChangedBasisIsExplicitlyHistoricalAndStale(string change)
    {
        var state = CoordinatorReady(); RetainTechnical(state);
        if (change == "original") state.Construction!.Compact!.Sources[0].Version = "changed";
        if (change == "contract") state.Discovery.Resolved[0].Version = "changed";
        if (change == "baseline") state.Plan!.Inputs.Add(new() { Name = "extra", Type = new() { Kind = "number" } });
        var history = PlanningConstruction.TechnicalRejectionContext(state)!;
        Assert.Equal("stale_basis", history["entries"]![0]!["status"]!.ToString());
        Assert.Single(state.Diagnostics); Assert.Null(state.Construction!.Candidate!.AuthorizedPaths);
    }

    [Fact]
    public void WholeSnapshotsAndHistoryHaveExplicitBoundsAndDoNotReinterpretUnknownLocations()
    {
        var state = CoordinatorReady();
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Tasks[0].Inputs[0].Value.Text = new string('x', 2000);
        RetainTechnical(state);
        var entry = PlanningConstruction.TechnicalRejectionContext(state)!["entries"]![0]!;
        Assert.True(entry["rejectedValue"]!["omitted"]!.GetValue<bool>());
        Assert.Null(entry["rejectedValue"]!["value"]);
        Assert.True(entry["valueAvailable"]!.GetValue<bool>());
        RetainTechnical(state); Assert.Single(candidate.Reviews.Single()["technicalRejections"]!["entries"]!.AsArray());
        for (var i = 0; i < 6; i++)
        {
            candidate.Diagnostics = [new("DETERMINISTIC_TEST", "/tasks/0/inputs/text", "Original diagnostic " + i + new string('z', 1800))];
            PlanningConstruction.RetainTechnicalRejections(state);
        }
        var history = PlanningConstruction.TechnicalRejectionContext(state)!;
        Assert.InRange(history["entries"]!.AsArray().Count, 1, 3);
        Assert.True(history["omitted"]!.GetValue<int>() > 0);
        Assert.True(Encoding.UTF8.GetByteCount(history.ToJsonString()) <= 6144);
        Assert.All(history["entries"]!.AsArray(), item =>
        {
            Assert.False(item!["valueAvailable"]!.GetValue<bool>());
            Assert.Null(item["rejectedValue"]);
            Assert.True(item["diagnostic"]!["omitted"]!.GetValue<bool>());
            Assert.Equal("target_unverified", item["status"]!.ToString());
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HistoricalVersionsRetainTheirStateAndContextBytes(int version)
    {
        var state = CoordinatorReady();
        state.Construction!.Compact!.PresentationVersion = version;
        state.Construction.Candidate!.Diagnostics = [new("DETERMINISTIC_TEST", "/tasks/consumer/inputs/text", "Original diagnostic")];
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.Context(state, review: true).ToJsonString();
        PlanningConstruction.RetainTechnicalRejections(state);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Null(PlanningConstruction.TechnicalRejectionContext(state));
        Assert.Equal(context, PlanningCompactConstruction.Context(state, review: true).ToJsonString());
        Assert.Contains("The candidate passed complete original-contract validation.", context, StringComparison.Ordinal);
    }

    private static void RetainTechnical(PlanningSession state)
    {
        state.Construction!.Candidate!.Diagnostics = [new("DETERMINISTIC_TEST", "/tasks/consumer/inputs/text", "Original diagnostic")];
        state.Diagnostics = state.Construction.Candidate.Diagnostics.ToList();
        PlanningConstruction.RetainTechnicalRejections(state);
    }
}
