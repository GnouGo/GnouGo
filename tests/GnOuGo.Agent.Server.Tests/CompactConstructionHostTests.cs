using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CompactConstructionHostTests
{
    private const string Objective = "Return exactly Hello as message.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreshCompactBackgroundPipelineRestoresEncryptedEvidenceWithoutApprovalOrEffects()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var effects = 0;
        var catalog = PlanningSessionLifecycleTests.AgentCatalog().OnTool("unused_write", (_, _) =>
        { effects++; throw new InvalidOperationException("Construction must not execute effects."); }).WithTool("unused_write", "An unrelated write operation",
            JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""));
        var model = new Model();
        PlanningSession state;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model))
        {
            await service.StartAsync(Ct);
            try
            {
                state = await service.StartAsync("compact-host", Objective, false, Ct);
                Assert.Equal(2, state.Construction!.Version);
                Assert.Equal(64, state.Request.MaxModelCalls);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                do
                {
                    state = (await service.GetAsync(state.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) break;
                    await Task.Delay(25, deadline.Token);
                } while (true);
                Assert.True(state.Status == PlanningStatus.FinalReview, $"{state.Status}: {string.Join(';', state.Diagnostics)}");
            }
            finally { await service.StopAsync(Ct); }
        }
        Assert.Equal(new[] { "construction_intent", "construction_intent", "construction_select", "construction_draft", "construction_local_judge" }, model.Phases);
        Assert.Equal(5, state.ModelCalls);
        Assert.Equal(0, effects);
        Assert.Null(state.ApprovedHash); Assert.Null(state.SavedAgentId);
        Assert.True(state.Construction!.Complete);
        Assert.Equal(11, state.Construction.Compact!.PresentationVersion);
        Assert.True(Assert.Single(state.Construction.Compact.InterfaceExtractions!).Complete);
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.Single(state.Construction.Checkpoints);
        Assert.Null(state.Construction.Candidate);
        PlanningArtifactApproval.Verify(state);
        Assert.Equal(5, state.RequestMeasurements!.Count);
        Assert.All(state.RequestMeasurements, m =>
        {
            Assert.True(m.EstimatedInputTokens <= m.InputLimit);
            Assert.Equal(m.CompleteRequestBytes, m.Components.Sum(c => c.SerializedBytes));
            Assert.Equal("admitted", m.Admission);
        });
        var stored = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(JsonSerializer.Serialize(state.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement),
            JsonSerializer.Serialize(stored.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement));
        Assert.Equal(state.ComputeArtifactHash(), stored.ComputeArtifactHash());
        Assert.Null(await fixture.Store.LoadAsync("another-tenant", state.Request.SessionId, Ct));
        var receipts = await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct);
        Assert.Equal(5, receipts.Count);
        using var reopened = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model);
        await reopened.StartAsync(Ct);
        try
        {
            var recovered = (await reopened.GetAsync(state.Request.SessionId, Ct))!;
            Assert.Equal(state.Revision, recovered.Revision);
            Assert.Equal(5, model.Phases.Count);
            Assert.Equal(0, effects);
            Assert.Equal(state.ComputeArtifactHash(), recovered.ComputeArtifactHash());
            foreach (var receipt in receipts)
                Assert.Equal(receipt.Value, (await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests", receipt.Key, EfPlanningSessionStore.Author, Ct))!.Value);
        }
        finally { await reopened.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tasks")]
    public async Task CompactJournalRejectsUnboundPhaseBeforeReservationOrDispatch(string? title)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var planning = new PlanningRequest { TenantId = "tenant", SessionId = "compact-title", Options = new() { ["construction_version"] = 2 } };
        var schema = JsonNode.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")!.AsObject();
        if (title is not null) schema["title"] = title;
        var request = PlanningGenerationPolicy.Apply(new() { Prompt = "Bounded request", StructuredOutputSchema = schema }, planning.Generation);
        request.ClientRequestId = planning.SessionId + ":1:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var model = new Model();
        var journal = new PlanningModelJournal(model, fixture, fixture.Records, "tenant", planning.SessionId,
            new LLMUsageBudgetScope(new() { MaxCalls = 64 }), new Price(), planning.Generation, planning: planning);
        await Assert.ThrowsAsync<PlanningConflictException>(() => journal.CallAsync(request, Ct));
        Assert.Empty(model.Phases);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Calls.CountAsync(Ct));
        Assert.Empty(await fixture.Records.ListAsync(PlanningModelJournal.RequestCollection, "tenant", EfPlanningSessionStore.Author, Ct));
    }

    private sealed class Price : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => .001m;
    }

    private sealed class Model : ILLMClient
    {
        internal List<string> Phases { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var phase = request.StructuredOutputSchema!["title"]!.ToString();
            Phases.Add(phase);
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
            JsonObject response;
            switch (phase)
            {
                case "construction_intent":
                    if (request.StructuredOutputSchema["properties"]?["clauses"] is not null)
                    {
                        response = new()
                        {
                            ["sourceId"] = Issued(request, "sourceId"),
                            ["clauses"] = new JsonArray(new JsonObject
                                { ["text"] = Objective, ["kind"] = "requirement", ["interfaces"] = "output" })
                        };
                        break;
                    }
                    response = new()
                    {
                        ["sourceId"] = Issued(request, "sourceId"),
                        ["interfaces"] = new JsonObject
                        {
                            ["inputs"] = new JsonArray(),
                            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "message", ["required"] = true,
                                ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false } }),
                            ["dispositions"] = new JsonArray(context["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
                            {
                                ["sourceId"] = source!["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(),
                                ["kind"] = "interfaces", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray("message"),
                                ["reason"] = "The original span declares this public greeting result.",
                                ["evidence"] = new JsonArray(new JsonObject
                                { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() })
                            }).ToArray())
                        }, ["evidenceRequests"] = null, ["clarifications"] = null
                    };
                    break;
                case "construction_select":
                    var requirement = context["requirements"]![0]!["id"]!.ToString();
                    response = new()
                    {
                        ["baseHash"] = Issued(request, "baseHash"), ["evidenceRequests"] = null, ["clarifications"] = null,
                        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "value", ["objective"] = Objective,
                            ["operationIds"] = new JsonArray(), ["closeScopes"] = null,
                            ["requirementIds"] = new JsonArray(requirement), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
                            ["expectedOutputs"] = new JsonArray("The exact requested greeting"), ["closeRequirementIds"] = new JsonArray(requirement),
                            ["outputNames"] = new JsonArray("message") }
                    };
                    break;
                case "construction_draft":
                    var extension = new PlanningConstructionExtension
                    {
                        Tasks = [new() { Id = "greeting", Kind = "value", Objective = Objective, Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "greeting", Port = "message" })],
                        CloseScopes = ["/root"], RequirementIds = [context["step"]!["requirementIds"]![0]!.ToString()], Complete = true
                    };
                    response = new()
                    {
                        ["baseHash"] = Issued(request, "baseHash"), ["requirements"] = null, ["commitments"] = null,
                        ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null
                    };
                    break;
                case "construction_local_judge":
                    Assert.NotNull(request.StructuredOutputSchema["properties"]?["action"]);
                    Assert.Null(request.StructuredOutputSchema["properties"]?["findings"]);
                    response = new() { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
                        ["uncertainty"] = "Runtime execution remains unverified." };
                    break;
                default: throw new InvalidOperationException("Unexpected construction phase: " + phase);
            }
            if (response["action"] is JsonObject action && action["kind"]?.ToString() == "findings" &&
                context["reviewPropositions"] is JsonArray propositions)
                action["acknowledgments"] = new JsonArray(propositions.Select(proposition => (JsonNode)new JsonObject
                    { ["id"] = proposition!["id"]!.DeepClone(), ["applicability"] = "broad" }).ToArray());
            return Task.FromResult(new LLMResponse
            {
                Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()),
                Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
            });
        }

        private static JsonNode Issued(LLMRequest request, string name)
        {
            var schema = request.StructuredOutputSchema!;
            var value = schema["properties"]![name]!;
            while (value["$ref"]?.ToString() is { } reference) value = schema["$defs"]![reference[8..]]!;
            return value["enum"]![0]!.DeepClone();
        }
    }
}
