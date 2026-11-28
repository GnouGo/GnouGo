using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;

internal static class BlindVisionControl
{
    internal static async Task RunAsync(KeyVaultBenchmarkModel model, BenchmarkCampaign campaign, string workspace, CancellationToken ct)
    {
        if (await campaign.HasUncertainRequestAsync(ct))
            throw BenchmarkCampaign.AdmissionFailure("campaign_uncertain_request_requires_recovery");
        model.RetainedExternalReservationsEur = await campaign.RetainedApplicationReservationsAsync(ct);
        await campaign.RequireRetainedApplicationHoldsAsync(model.RetainedExternalReservationsEur, ct, requireComplete: true);
        KeyVaultBenchmarkModel.RequireReady(await model.ReadinessAsync(ct));
        var store = EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: workspace);
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: inspect
                    type: llm.call
                    input:
                      model: ${data.inputs.model}
                      provider: ${data.inputs.provider}
                      images: ${data.inputs.images}
                      max_tokens: 1600
                      prompt: Inspect this slide for a publication review. Transcribe the code printed in the top right if fully legible, otherwise return null. Report visible layout or legibility problems with region, severity, correction and uncertainty. An empty findings array is valid.
                      structured_output:
                        name: visual_review
                        strict: true
                        schema_inline:
                          type: object
                          additionalProperties: false
                          required: [code, findings]
                          properties:
                            code: { type: [string, 'null'] }
                            findings:
                              type: array
                              items:
                                type: object
                                additionalProperties: false
                                required: [problem, region, severity, correction, uncertainty]
                                properties:
                                  problem: { type: string }
                                  region: { type: string }
                                  severity: { type: string, enum: [minor, major, critical] }
                                  correction: { type: string }
                                  uncertainty: { type: string }
            """);
        var compiled = new WorkflowCompiler().Compile(document);
        var reviews = new JsonArray();
        foreach (var name in new[] { "panel-k", "panel-q" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine("tests", "GnOuGo.Agent.Planning.Benchmark", "DeckCraft", name + ".png"), ct);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var run = "vision-control-" + sha[..16];
            var engine = new WorkflowEngine { ImageArtifacts = store, LLMCapabilities = model,
                LLMClient = new ControlClient(model, run), Limits = new() { RunId = run, TenantId = "benchmark" } };
            var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject {
                ["model"] = model.Model, ["provider"] = model.Provider, ["images"] = new JsonArray((JsonNode)new JsonObject {
                    ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(bytes) }) }, ct);
            var observation = result.StepResults.LastOrDefault()?.Output;
            var review = new JsonObject { ["image"] = name, ["sha256"] = sha, ["request"] = run + ":inspect",
                ["review"] = observation?["json"]?.DeepClone(), ["meta"] = observation?["meta"]?.DeepClone(),
                ["error"] = result.Error?.Code, ["detail"] = result.Error?.Message, ["executed"] = result.Success };
            reviews.Add((JsonNode)review);
            await campaign.SaveAsync("planning-evaluation-vision-controls", run, review, ct);
            if (!result.Success) break;
        }
        var passed = reviews.Count == 2 && reviews[0]?["review"]?["code"]?.ToString() == "N7K-492" &&
            reviews[0]?["meta"]?["images"]?[0]?["sha256"]?.ToString() == reviews[0]?["sha256"]?.ToString() &&
            reviews[1]?["meta"]?["images"]?[0]?["sha256"]?.ToString() == reviews[1]?["sha256"]?.ToString() &&
            reviews[1]?["review"] is JsonObject damaged && damaged.ContainsKey("code") && damaged["code"] is null &&
            damaged["findings"] is JsonArray findings && findings.Any(f => f?["severity"]?.ToString() is "major" or "critical") &&
            reviews[0]?["review"]?["findings"] is JsonArray clean && !clean.Any(f => f?["severity"]?.ToString() is "major" or "critical");
        Console.WriteLine(new JsonObject { ["model"] = model.Model, ["provider"] = model.Provider, ["reviews"] = reviews, ["passed"] = passed }.ToJsonString());
        if (!passed) Environment.ExitCode = 1;
    }

    private sealed class ControlClient(KeyVaultBenchmarkModel model, string id) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            request.ClientRequestId = id + ":inspect";
            request.DisableTransportRetries = true;
            return model.CallExecutionAsync(request, ct);
        }
    }
}
