using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CommitmentCorrectionHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("coverage")]
    [InlineData("uncertain")]
    [InlineData("initial")]
    [InlineData("initial_repeat")]
    [InlineData("initial_uncertain")]
    [InlineData("nullable")]
    [InlineData("initial_followup")]
    [InlineData("initial_followup_uncertain")]
    public async Task BackgroundCoverageCorrectionIsDurableTenantOwnedAndNeverExecutes(string variant)
    {
        var uncertain = variant.EndsWith("uncertain", StringComparison.Ordinal);
        var repeated = variant == "initial_repeat";
        var drafts = variant.StartsWith("initial_followup", StringComparison.Ordinal) ? 3 : 2;
        var calls = drafts + (uncertain || repeated ? 0 : 1);
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var effects = 0;
        var catalog = PlanningSessionLifecycleTests.AgentCatalog().OnTool("produce_observed", (_, _) =>
        { effects++; throw new InvalidOperationException("Planning must not execute this operation"); }).WithTool("produce_observed", "Produce the requested fixture report",
            JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"},"optionalLocation":{"type":["string","null"]}},"required":["location","optionalLocation"],"additionalProperties":false}"""));
        var model = new Model(variant); PlanningSession state;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model))
        {
            await service.StartAsync(Ct);
            try
            {
                state = await service.StartAsync("committed-report", "Produce the fixture report through produce_observed and return its location.", false, Ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                do
                {
                    state = (await service.GetAsync(state.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(state.Status) || PlanningStatus.IsTerminal(state.Status)) break;
                    await Task.Delay(25, deadline.Token);
                } while (true);
                Assert.True(state.Status == (uncertain || repeated ? PlanningStatus.Stopped : PlanningStatus.FinalReview),
                    $"{state.Status} after {state.ModelCalls} calls and {state.ReplanAttempts} corrections: {string.Join(';', state.Diagnostics)}");
                Assert.Equal(3, state.IntentVersion); Assert.Equal(calls, state.ModelCalls); Assert.Equal(drafts - 1, state.ReplanAttempts);
                Assert.Equal(calls, model.Calls); Assert.Equal(0, effects); Assert.Null(state.ApprovedHash); Assert.Null(state.SavedAgentId);
                Assert.Equal("explicit-assertions-v1", state.Request.Options["planning_validation_profile"]!.ToString());
                Assert.Equal(1, state.Construction!.Version);
                Assert.Null(state.Request.Options["automatic_correction_progress"]);
                if (uncertain) { Assert.NotNull(state.PendingCall); Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_DISPATCH_UNVERIFIABLE"); }
                else if (repeated)
                {
                    Assert.Null(state.Requirements); Assert.Null(state.Plan); Assert.Null(state.Yaml);
                    Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
                }
                else
                {
                    Assert.True(state.Construction.Complete);
                    var checkpoint = Assert.Single(state.Construction.Checkpoints);
                    Assert.Equal("unverified", Assert.Single(checkpoint.Reviews)["outcome"]!.ToString());
                    PlanningArtifactApproval.Verify(state);
                    Assert.Contains(state.ValidationResults, r => r.Id == "coverage:report:0:result" && r.Outcome == "verified");
                    Assert.Contains(state.ValidationResults, r => r.Outcome == "unverified");
                }
            }
            finally { await service.StopAsync(Ct); }
        }
        var stored = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(state.ModelCalls, stored.ModelCalls);
        Assert.Equal(state.ReplanAttempts, stored.ReplanAttempts);
        Assert.Equal(state.PendingCall?.Id, stored.PendingCall?.Id);
        Assert.Equal(JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements), JsonSerializer.Serialize(stored.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        var receipts = await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct);
        Assert.Equal(calls - (uncertain ? 1 : 0), receipts.Count);
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), catalog, model);
        await restarted.StartAsync(Ct);
        try
        {
            for (var i = 0; i < 3; i++) Assert.Equal(state.Revision, (await restarted.GetAsync(state.Request.SessionId, Ct))!.Revision);
            Assert.Equal(calls, model.Calls); Assert.Equal(0, effects);
            Assert.Null(await fixture.Store.LoadAsync("other-tenant", state.Request.SessionId, Ct));
            foreach (var receipt in receipts)
                Assert.Equal(receipt.Value, (await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests", receipt.Key, EfPlanningSessionStore.Author, Ct))!.Value);
        }
        finally { await restarted.StopAsync(Ct); }
    }

    private sealed class Model(string variant) : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (request.StructuredOutputSchema!["properties"]?["findings"] is not null)
                return Task.FromResult(new LLMResponse { Json = PlanningCorpus.OneDraftResponse(request, new()),
                    Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
            var followup = variant.StartsWith("initial_followup", StringComparison.Ordinal);
            if (Calls == 2 && variant is "coverage" or "uncertain")
            {
                Assert.NotNull(request.StructuredOutputSchema!["properties"]!["extension"]);
                Assert.False(PlanningRepairPatch.Issued(request.StructuredOutputSchema.AsObject()));
            }
            if (variant.EndsWith("uncertain", StringComparison.Ordinal) && Calls == (followup ? 3 : 2)) throw new InvalidOperationException("Simulated unknown provider completion");
            if (PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()))
            {
                var slot = Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!;
                Assert.Equal("/root/outputs/reportPath", slot["location"]!.ToString());
                var patch = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
                    ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                    { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace",
                        ["value"] = JsonSerializer.SerializeToNode(new TaskValue { Kind = "output", Source = "produce", Port = "location" }, PlanningJsonContext.Default.TaskValue) }) } };
                patch = PlanningCorpus.Transport(patch, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject())!.AsObject();
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(patch, request.StructuredOutputSchema));
                return Task.FromResult(new LLMResponse { Json = patch, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
            }
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
            var operation = context["operations"]!.AsArray().Single(o => o!["outputs"]!.AsArray().Any(p => p!["name"]!.ToString() == "location"))!["id"]!.ToString();
            var plan = new TaskPlan { Root = new() { Outputs = [new("reportPath", variant is "coverage" or "uncertain" && Calls == 1 ? new() { Kind = "string", Text = "unperformed" } :
                new() { Kind = "output", Source = "produce", Port = variant == "nullable" || followup ? "optionalLocation" : "location" })] } };
            if (Calls > 1 || variant.StartsWith("initial", StringComparison.Ordinal) || variant == "nullable") plan.Root.Tasks.Add(new() { Id = "produce", Objective = "Produce the requested report", Operation = operation });
            var proposal = new PlanningProposal { Plan = plan, Requirements = new() { Summary = "Produce the fixture report", Inputs = [],
                Outputs = [new() { Name = "reportPath", Type = new() { Kind = "string" } }],
                Outcomes = [new("report", "Produce the fixture report and return its location") { Commitments = [new(operation) { Result = new("location", "reportPath") }] }] } };
            var response = PlanningCorpus.OneDraftResponse(request, proposal)!;
            if (variant.StartsWith("initial", StringComparison.Ordinal))
            {
                if (Calls == 1 || variant == "initial_repeat") response["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["output"] = "workspace/report.xlsx";
                if (Calls == 2) Assert.Contains("REQUIREMENTS_COMMITMENT_INVALID", request.Prompt);
            }
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema));
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
