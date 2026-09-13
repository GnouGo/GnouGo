using GnOuGo.Flow.Core.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

public static class PlanningGenerationPolicy
{
    public const int AnswerTargetTokens = 2_048;
    public const int NormalOutputTokens = 8_192;
    public const int EscalatedOutputTokens = 16_384;
    public static int InputTarget(PlanningGenerationOptions options) => checked(options.MaxInputTokensPerRequest * 4 / 5);

    public static string ReasoningFor(PlanningGenerationOptions options, string phase) => phase switch
    {
        var name when name.StartsWith("behavior", StringComparison.Ordinal) => options.ReasoningProfile.Behavior,
        var name when name.StartsWith("semantic_review", StringComparison.Ordinal) => options.ReasoningProfile.SemanticReview,
        _ => options.ReasoningProfile.Routine
    };

    public static void Validate(PlanningGenerationOptions options)
    {
        if (options.MaxInputTokensPerRequest is < 512 or > 128_000 || options.MaxOutputTokens is < 1 or > 65_536)
            throw new ArgumentException("Invalid model input or output limits.");
        if (options.ReasoningProfile is null || new[] { options.ReasoningProfile.Routine, options.ReasoningProfile.Behavior, options.ReasoningProfile.SemanticReview }
            .Any(value => value is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "auto")))
            throw new ArgumentException("Invalid phase reasoning profile.");
    }

    public static LLMRequest Apply(LLMRequest request, PlanningGenerationOptions options)
    {
        Validate(options);
        if (request.OutputBudgetEscalation is { } escalation)
        {
            if (options.MaxOutputTokens != NormalOutputTokens || request.MaxTokens != EscalatedOutputTokens || escalation.Level != 1 ||
                new[] { escalation.ParentPageId, escalation.ParentRequestId, escalation.ParentRequestHash, escalation.ParentReceiptFingerprint,
                    escalation.DecisionId, escalation.CanonicalDecisionId, escalation.EvidenceFingerprint }.Any(string.IsNullOrWhiteSpace) ||
                !request.RequireOutputTokenLimit || !request.DisableTransportRetries)
                throw new PlanningConflictException("The output escalation is not a bounded coordinator-issued allowance.");
        }
        else request.MaxTokens = Math.Min(request.MaxTokens ?? options.MaxOutputTokens, options.MaxOutputTokens);
        request.RequireOutputTokenLimit = true;
        request.DisableTransportRetries = true;
        return request;
    }

    public static string RequestFingerprint(LLMRequest request)
    {
        var json = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        json["clientRequestId"] = null;
        return Fingerprint(json.ToJsonString());
    }

    public static string ReceiptFingerprint(LLMResponse response)
        => Fingerprint(JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse));

    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Both durable host journals validate the exact owned parent request and verified receipt.</summary>
    public static void ValidateOutputEscalation(LLMRequest request, LLMRequest parent, LLMResponse receipt, string sessionId)
    {
        var proof = request.OutputBudgetEscalation ?? throw new PlanningConflictException("Missing output escalation proof.");
        Apply(request, new() { MaxOutputTokens = NormalOutputTokens });
        if (parent.OutputBudgetEscalation is not null || parent.MaxTokens != NormalOutputTokens || receipt.CompletionStatus != "output_limit" ||
            parent.ClientRequestId != proof.ParentRequestId || !proof.ParentRequestId.StartsWith(sessionId + ":", StringComparison.Ordinal) ||
            request.ClientRequestId is { } id && !id.StartsWith(sessionId + ":", StringComparison.Ordinal) ||
            RequestFingerprint(parent) != proof.ParentRequestHash || !proof.ParentRequestId.EndsWith(":" + proof.ParentPageId + ":" + proof.ParentRequestHash, StringComparison.Ordinal) ||
            ReceiptFingerprint(receipt) != proof.ParentReceiptFingerprint ||
            parent.StructuredOutputSchema?["properties"] is not JsonObject fields || fields.Count != 1 || !fields.ContainsKey(proof.DecisionId))
            throw new PlanningConflictException("The output escalation does not reference its exact owned singleton truncation receipt.");
        var original = JsonSerializer.SerializeToNode(parent, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var escalated = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        original["clientRequestId"] = null; escalated["clientRequestId"] = null;
        escalated.Remove("outputBudgetEscalation"); escalated["maxTokens"] = NormalOutputTokens;
        if (!JsonNode.DeepEquals(original, escalated))
            throw new PlanningConflictException("The output escalation changed a generation field other than its output ceiling.");
    }
}
