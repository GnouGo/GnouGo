using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

internal static class CodeReviewEvidence
{
    internal const string Kind = "review.diff";

    internal static AgentTaskResult Verify(AgentTaskDefinition task, AgentTaskResult result)
    {
        if (result.Status != "completed" || task.Verification.All(r => r.Kind != Kind)) return result;
        try
        {
            if (task.Inputs["review"] is not JsonObject review || result.Output is not JsonObject output ||
                !Revision(review["base"], out var basis) || !Revision(review["head"], out var head) ||
                output["base"]?.ToString() != basis || output["head"]?.ToString() != head ||
                task.Verification.Any(r => r.Kind == Kind && r.Subject != head) ||
                review["files"] is not JsonArray patches || output["findings"] is not JsonArray candidates ||
                output["reviewed_files"] is not JsonArray coverage || output["limitations"] is not JsonArray limitations)
                return Failed("The review requires matching base/head, exact patches, findings, observed files and limitations.");
            var files = patches.Deserialize(CopilotCoreJsonContext.Default.IReadOnlyListReviewFilePatch)!
                .ToDictionary(file => file.Path, StringComparer.Ordinal);
            var validated = ReviewValidation.ValidateObserved(files, result.Evidence.Where(e => e.Kind == "file.read").Select(e => e.Subject),
                coverage.Select(p => p!.GetValue<string>()).ToArray(), limitations.Select(p => p!.GetValue<string>()).ToArray(),
                candidates.Deserialize(CopilotCoreJsonContext.Default.IReadOnlyListReviewFindingCandidate)!);
            if (validated.Errors.Count > 0) return Failed(string.Join(" ", validated.Errors));
            if (validated.Findings.Count != candidates.Count) return Failed("The report contains duplicate findings.");
            var facts = JsonSerializer.SerializeToNode(validated, CopilotCoreJsonContext.Default.ObservedReview)!.AsObject();
            facts["base"] = basis;
            facts["head"] = head;
            return result with { Evidence = [.. result.Evidence, new("review:" + head, Kind, head, facts)] };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { return Failed("The provided comparison or structured review is invalid."); }

        AgentTaskResult Failed(string reason) => result with { Status = "failed", Output = null, Message = reason };
    }

    private static bool Revision(JsonNode? node, out string sha)
    {
        sha = node?.ToString() ?? "";
        return sha.Length is 40 or 64 && sha.All(char.IsAsciiHexDigit);
    }
}
