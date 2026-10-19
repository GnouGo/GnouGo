using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CorrectionClarificationHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("initial", 2, 3, 0)]
    [InlineData("technical", 2, 3, 1)]
    [InlineData("question", 2, 4, 2)]
    [InlineData("failed_then_question", 3, 5, 3)]
    [InlineData("failed_then_question", 2, 3, 2)]
    public async Task BackgroundOwnerResumesAnswersWithinTheOriginalAllowance(string scenario, int repairs, int calls, int attempts)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var model = new Model(scenario);
        var settings = new GnOuGo.Agent.Server.Configuration.TypedWorkflowPlanningSettings { MaxReplanAttempts = repairs };
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        var state = await RetainedVersionOneConstruction.SeedAsync(fixture, agents, model, "clarification-loop",
            "Return the supplied subject. Ask which subject only if that business preference is missing.", Ct, repairs);
        using (var host = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, model, settings))
        {
            await host.StartAsync(Ct);
            try
            {
                Assert.Equal("1", state.Request.Options["clarification_policy"]!.ToString());
                state = await Wait(host, state.Request.SessionId);
                if (state.Status == PlanningStatus.Clarification)
                {
                    var accepted = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
                    var beforeCalls = state.ModelCalls;
                    var command = new PlanningCommand { Kind = "answer", ExpectedRevision = state.Revision, Answers = [new("subject", "first")] };
                    var answered = await host.SubmitAsync(state.Request.SessionId, command, Ct);
                    Assert.Equal(accepted, JsonSerializer.Serialize(answered.Requirements, PlanningJsonContext.Default.PlanningRequirements));
                    Assert.Equal(beforeCalls, answered.ModelCalls);
                    await Assert.ThrowsAsync<PlanningConflictException>(() => host.SubmitAsync(state.Request.SessionId, command, Ct));
                    state = await Wait(host, state.Request.SessionId);
                }
                var stopped = scenario == "failed_then_question" && repairs == 2;
                Assert.True(state.Status == (stopped ? PlanningStatus.Stopped : PlanningStatus.FinalReview), $"{state.Status}; calls={state.ModelCalls}; repairs={state.ReplanAttempts}; {state.Request.Options}; " + string.Join(';', state.Diagnostics));
                Assert.Equal(calls, state.ModelCalls); Assert.Equal(attempts, state.ReplanAttempts);
                Assert.Equal(calls, state.Usage!.Calls); Assert.Equal(calls * 15, state.Usage.TotalTokens);
                Assert.Null(state.ApprovedHash); Assert.Null(state.SavedAgentId);
                if (stopped) Assert.Contains(state.Diagnostics, d => d.Code == "CLARIFICATION_CONTINUATION_UNAVAILABLE");
                else PlanningArtifactApproval.Verify(state);
                for (var i = 0; i < 3; i++) Assert.Equal(state.Revision, (await host.GetAsync(state.Request.SessionId, Ct))!.Revision);
                if (Environment.GetEnvironmentVariable("GNOU_GO_CLARIFICATION_REVIEW_EVIDENCE") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, $"interaction-{scenario}-{repairs}.json"), new JsonObject
                    {
                        ["scenario"] = scenario, ["repairAllowance"] = repairs, ["calls"] = state.ModelCalls,
                        ["corrections"] = state.ReplanAttempts, ["status"] = state.Status, ["totalTokens"] = state.Usage.TotalTokens,
                        ["approved"] = false, ["providerCalls"] = 0,
                        ["questionIds"] = new JsonArray((state.AnswerHistory ?? []).SelectMany(b => b.Questions).Select(q => (JsonNode?)JsonValue.Create(q.Id)).ToArray()),
                        ["progress"] = state.Request.Options["automatic_correction_progress"]?.DeepClone(),
                        ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
                        ["requests"] = new JsonArray(model.Requests.Select(r => (JsonNode?)new JsonObject
                        { ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)),
                            ["estimate"] = (System.Text.Encoding.UTF8.GetByteCount(r.Prompt) + System.Text.Encoding.UTF8.GetByteCount(r.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256 }).ToArray())
                    }.ToJsonString(new() { WriteIndented = true }), Ct);
                }
            }
            finally { await host.StopAsync(Ct); }
        }
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, model, settings);
        await restarted.StartAsync(Ct);
        try
        {
            Assert.Equal(state.Revision, (await restarted.GetAsync(state.Request.SessionId, Ct))!.Revision);
            Assert.Equal(calls, model.Calls);
            Assert.Equal(calls, (await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct)).Count);
            Assert.Null(await fixture.Store.LoadAsync("other", state.Request.SessionId, Ct));
        }
        finally { await restarted.StopAsync(Ct); }
    }

    private static async Task<PlanningSession> Wait(PlanningSessionService service, string id)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var state = (await service.GetAsync(id, deadline.Token))!;
            if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) return state;
            try { await Task.Delay(25, deadline.Token); }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            { Assert.Fail($"Planning did not quiesce: {state.Status}, revision={state.Revision}, calls={state.ModelCalls}, pending={state.PendingCall?.Id}, options={state.Request.Options}, findings={string.Join(';', state.Diagnostics)}"); }
        }
    }

    [Fact]
    public async Task CheckpointedAnswerResumesOnANewOwnerWithoutRedispatchingCommittedResponses()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var model = new Model("question"); var agents = PlanningSessionLifecycleTests.AgentCatalog();
        var state = await RetainedVersionOneConstruction.SeedAsync(fixture, agents, model, "restart-answer",
            "Return the supplied subject; ask which subject when unspecified.", Ct);
        using (var first = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, model))
        {
            await first.StartAsync(Ct);
            state = await Wait(first, state.Request.SessionId); Assert.Equal(PlanningStatus.Clarification, state.Status);
            await first.StopAsync(Ct);
            state = await first.SubmitAsync(state.Request.SessionId, new() { Kind = "answer", ExpectedRevision = state.Revision,
                Answers = [new("subject", "first")] }, Ct);
            Assert.Equal(2, model.Calls); Assert.Equal(1, state.ReplanAttempts);
            Assert.Single((await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!.AnswerHistory!);
        }
        var receipts = await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct);
        using var next = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, model);
        await next.StartAsync(Ct);
        try
        {
            state = await Wait(next, state.Request.SessionId); Assert.Equal(PlanningStatus.FinalReview, state.Status);
            Assert.Equal(4, model.Calls); Assert.Equal(2, state.ReplanAttempts); Assert.Equal(4, state.Usage!.Calls);
            foreach (var receipt in receipts)
                Assert.Equal(receipt.Value, (await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests", receipt.Key, EfPlanningSessionStore.Author, Ct))!.Value);
        }
        finally { await next.StopAsync(Ct); }
    }

    private sealed class Model(string scenario) : ILLMClient
    {
        internal int Calls;
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var call = ++Calls;
            Requests.Add(request);
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
            if (request.StructuredOutputSchema!["properties"]?["extension"] is not null)
            {
                Assert.Contains("cannot call tools", request.Prompt);
                Assert.Contains("Preserve accepted requirements and interfaces", request.Prompt);
            }
            JsonNode response;
            var asking = scenario == "initial" && call == 1 || scenario == "question" && call == 2 || scenario == "failed_then_question" && call == 3;
            if (asking)
                response = JsonSerializer.SerializeToNode(new PlanningProposal { Clarifications = [new("subject", "Which supplied subject should be described?",
                    [new("first", "The first subject"), new("second", "The second subject")], "first")] }, PlanningJsonContext.Default.PlanningProposal)!;
            else if (request.StructuredOutputSchema!["properties"]?["patch"] is not null)
            {
                var slot = Assert.Single(context["repair"]!["slots"]!.AsArray())!;
                response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
                    ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                    { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = scenario == "failed_then_question" && call == 2
                        ? new JsonObject { ["kind"] = "string", ["text"] = "Unresolved ${subject}" }
                        : new JsonObject { ["kind"] = "input", ["source"] = "subject" } }) } };
            }
            else
            {
                var plan = PlanningCorpus.LiteralResult(); plan.Inputs = [new() { Name = "subject" }];
                plan.Root.Outputs = [new("result", call == 1 ? new() { Kind = "string", Text = "${subject}" } : new() { Kind = "input", Source = "subject" })];
                response = JsonSerializer.SerializeToNode(new PlanningProposal { Plan = plan, Requirements = new()
                { Summary = "Describe the supplied subject", Inputs = plan.Inputs, Outcomes = [new("result", "Describe the requested subject")], Outputs = [new() { Name = "result" }] } }, PlanningJsonContext.Default.PlanningProposal)!;
            }
            if (request.StructuredOutputSchema!["properties"]?["extension"] is not null || request.StructuredOutputSchema["properties"]?["findings"] is not null)
                response = PlanningCorpus.OneDraftResponse(request, response.Deserialize(PlanningJsonContext.Default.PlanningProposal)!)!;
            else response = PlanningCorpus.Transport(response, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject())!;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema));
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
