using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Browser.Mcp;
using GnOuGo.Flow.Core.Runtime;

internal static class RetainedCaptureReview
{
    internal static async Task RunAsync(KeyVaultBenchmarkModel? model, BenchmarkCampaign campaign, string workspace,
        string form, string execution, string sourceStep, CancellationToken ct)
    {
        var report = await campaign.LoadAsync("planning-evaluation-runs", form, ct);
        var run = await LiveWorkflowEvaluation.ExecutionStore(workspace).ReadAsync("benchmark", execution, ct);
        if (report?["scenario"]?.ToString() != "amazon" || report["form_attempt"]?.ToString() != form ||
            report["execution_run_id"]?.ToString() != execution || report["campaign"]?.ToString() != campaign.Id ||
            run?.RunId != execution || !run.Invocations.TryGetValue(sourceStep, out var source) ||
            source.StepType != "llm.call" || source.ExternalCompletionObserved || source.ResolvedInput is not JsonObject input)
            throw new InvalidDataException("The retained form and its unresolved model invocation must match.");

        var original = input["prompt"]!.GetValue<string>();
        const string marker = "Business data (JSON):";
        int boundary = original.IndexOf(marker, StringComparison.Ordinal);
        if (boundary < 0) throw new InvalidDataException("Missing retained data boundary.");
        var header = original[..(boundary + marker.Length)];
        var data = JsonNode.Parse(original[header.Length..])!.AsObject();
        var snapshot = data["snapshot"]!.AsObject();
        var parsed = snapshot.Deserialize(RetainedCaptureJson.Default.BrowserObservationSnapshot)!;
        if (parsed.CaptureTruncated || parsed.ManifestTruncated)
            throw new InvalidDataException("The aggregate capture is incomplete.");
        var review = BrowserObservationTextReview.Create(parsed.Id, parsed.Pages, parsed.RecordCount)
            ?? throw new InvalidDataException("No complete bounded text review is available.");
        snapshot.Remove("pages");
        snapshot.Remove("linkPrefix");
        snapshot["textReview"] = JsonSerializer.SerializeToNode(review, RetainedCaptureJson.Default.BrowserObservationTextReview);
        var prompt = header + "\n" + data.ToJsonString();
        if (Encoding.UTF8.GetByteCount(prompt) > 96 * 1024) throw new InvalidDataException("Projected input exceeds 96 KiB.");
        var request = execution + ":capture-review:" + Hash(prompt)[..16];
        var receipt = new JsonObject {
            ["form"] = form, ["execution"] = execution, ["source_step"] = sourceStep, ["source_status"] = source.Status,
            ["request_id"] = request, ["snapshot"] = parsed.Id, ["pages"] = parsed.Pages.Count, ["records"] = parsed.RecordCount,
            ["original_bytes"] = Encoding.UTF8.GetByteCount(original), ["original_sha256"] = Hash(original),
            ["projected_bytes"] = Encoding.UTF8.GetByteCount(prompt), ["projected_sha256"] = Hash(prompt), ["workflow_modified"] = false
        };
        if (model is null)
        {
            receipt["inspection_only"] = true;
            Console.WriteLine(receipt.ToJsonString());
            return;
        }
        var saved = await campaign.LoadAsync("planning-evaluation-capture-reviews", request, ct);
        if (saved is not null) { Console.WriteLine(saved.ToJsonString()); return; }
        if (await campaign.HasUncertainRequestAsync(ct)) throw BenchmarkCampaign.AdmissionFailure("campaign_uncertain_request_requires_recovery");
        model.RetainedExternalReservationsEur = await campaign.RetainedApplicationReservationsAsync(ct);
        await campaign.RequireRetainedApplicationHoldsAsync(model.RetainedExternalReservationsEur, ct, requireComplete: true);
        KeyVaultBenchmarkModel.RequireReady(await model.ReadinessAsync(ct));
        var result = await model.CallExecutionAsync(new LLMRequest {
            Provider = model.Provider, Model = model.Model, ClientRequestId = request, Prompt = prompt, MaxTokens = 1024,
            DisableTransportRetries = true, RequireOutputTokenLimit = true,
            StructuredOutputSchema = input["structured_output"]!["schema_inline"]!.DeepClone(),
            StructuredOutputStrict = input["structured_output"]!["strict"]!.GetValue<bool>()
        }, ct);
        var decision = result.Json ?? JsonNode.Parse(result.Text);
        if (decision?["clear"] is not JsonValue clear || !clear.TryGetValue<bool>(out _) || string.IsNullOrWhiteSpace(decision["reason"]?.GetValue<string>()))
            throw new InvalidDataException("The model response is missing the original decision fields.");
        receipt["provider"] = model.Provider;
        receipt["model"] = model.Model;
        receipt["decision"] = decision.DeepClone();
        receipt["usage"] = result.Usage?.DeepClone();
        await campaign.SaveAsync("planning-evaluation-capture-reviews", request, receipt, ct);
        Console.WriteLine(receipt.ToJsonString());
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserObservationSnapshot))]
[JsonSerializable(typeof(BrowserObservationTextReview))]
internal partial class RetainedCaptureJson : JsonSerializerContext;
