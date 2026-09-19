using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GnOuGo.GithubCopilot.Core;

public static partial class ReviewValidation
{
    public static IReadOnlyList<CopilotReviewBatch> CreateBatches(
        IReadOnlyList<ReviewFilePatch> files,
        int maxBatchCharacters)
    {
        if (maxBatchCharacters < 1_000)
            throw new ArgumentOutOfRangeException(nameof(maxBatchCharacters), "A review batch must allow at least 1000 characters.");

        var batches = new List<CopilotReviewBatch>();
        var current = new List<ReviewFilePatch>();
        var characters = 0;

        foreach (var file in files.Where(static file => !file.IsBinary && !file.IsSubmodule))
        {
            var size = file.Path.Length + file.Patch.Length + 64;
            if (current.Count > 0 && characters + size > maxBatchCharacters)
            {
                batches.Add(new CopilotReviewBatch(batches.Count, current.ToArray(), characters));
                current.Clear();
                characters = 0;
            }

            current.Add(file);
            characters += size;
        }

        if (current.Count > 0)
            batches.Add(new CopilotReviewBatch(batches.Count, current.ToArray(), characters));

        return batches;
    }

    public static ReviewCoverage CalculateCoverage(IReadOnlyList<ReviewFilePatch> files)
    {
        var skipped = files.Where(static file => file.IsBinary || file.IsSubmodule).Select(static file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        var truncated = files.Where(static file => file.Truncated).Select(static file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        return new ReviewCoverage(files.Count, files.Count - skipped.Length, skipped.Length, truncated.Length, skipped, truncated);
    }

    public static bool TryValidate(
        ReviewFindingCandidate candidate,
        IReadOnlyDictionary<string, ReviewFilePatch> files,
        out ReviewFinding? finding,
        out string? rejection)
    {
        finding = null;
        rejection = null;

        if (string.IsNullOrWhiteSpace(candidate.Path))
        {
            rejection = "A review finding must identify a non-empty repository-relative path.";
            return false;
        }

        if (!files.TryGetValue(NormalizePath(candidate.Path), out var file))
        {
            rejection = $"Unknown review path '{candidate.Path}'.";
            return false;
        }

        if (candidate.StartLine <= 0 || candidate.EndLine < candidate.StartLine)
        {
            rejection = $"Invalid line range {candidate.StartLine}-{candidate.EndLine} for '{candidate.Path}'.";
            return false;
        }

        var validLines = ParseDiffLines(file.Patch, candidate.Side);
        if (!Enumerable.Range(candidate.StartLine, candidate.EndLine - candidate.StartLine + 1).All(validLines.Contains))
        {
            rejection = $"Line range {candidate.StartLine}-{candidate.EndLine} is not present on the {candidate.Side} side of '{candidate.Path}'.";
            return false;
        }

        if (!Enum.IsDefined(candidate.Severity) || !Enum.IsDefined(candidate.Side) || !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1
            || string.IsNullOrWhiteSpace(candidate.Explanation)
            || string.IsNullOrWhiteSpace(candidate.Category)
            || string.IsNullOrWhiteSpace(candidate.Evidence))
        {
            rejection = $"Finding for '{candidate.Path}' has invalid confidence, category, or explanation.";
            return false;
        }

        var path = NormalizePath(file.Path);
        finding = new ReviewFinding(
            CreateFingerprint(path, candidate.Side, candidate.StartLine, candidate.EndLine, candidate.Category, candidate.Explanation),
            candidate.Severity,
            candidate.Category.Trim(),
            candidate.Confidence,
            path,
            candidate.Side,
            candidate.StartLine,
            candidate.EndLine,
            candidate.Evidence.Trim(),
            candidate.Explanation.Trim(),
            string.IsNullOrWhiteSpace(candidate.SuggestedPatch) ? null : candidate.SuggestedPatch.Trim());
        return true;
    }

    public static IReadOnlyList<ReviewFinding> Deduplicate(IEnumerable<ReviewFinding> findings)
        => findings
            .GroupBy(static finding => finding.Fingerprint, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static finding => finding.Confidence).First())
            .OrderByDescending(static finding => finding.Severity)
            .ThenBy(static finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(static finding => finding.StartLine)
            .ToArray();

    public static bool IsDuplicateOfExisting(
        ReviewFinding finding,
        IEnumerable<ExistingReviewComment> existingComments)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(existingComments);

        var normalizedExplanation = NormalizeReviewText(finding.Explanation);
        foreach (var comment in existingComments)
        {
            if (comment is null)
                continue;
            if (string.Equals(comment.Fingerprint?.Trim(), finding.Fingerprint, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(comment.Body)
                    && comment.Body.Contains(finding.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (!string.Equals(NormalizePath(comment.Path), finding.Path, StringComparison.Ordinal)
                || comment.Side is not null && comment.Side != finding.Side
                || comment.EndLine is not null && comment.EndLine != finding.EndLine
                || comment.StartLine is not null && comment.StartLine != finding.StartLine)
                continue;

            var normalizedBody = NormalizeReviewText(comment.Body);
            if (normalizedExplanation.Length > 0
                && (string.Equals(normalizedBody, normalizedExplanation, StringComparison.Ordinal)
                    || normalizedBody.Contains(normalizedExplanation, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    public static string CreateFingerprint(
        string path,
        ReviewDiffSide side,
        int startLine,
        int endLine,
        string category,
        string explanation)
    {
        var canonical = string.Join('\n', NormalizePath(path), side.ToString().ToLowerInvariant(), startLine.ToString(CultureInfo.InvariantCulture), endLine.ToString(CultureInfo.InvariantCulture), category.Trim().ToLowerInvariant(), CollapseWhitespace().Replace(explanation.Trim(), " "));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static HashSet<int> ParseDiffLines(string patch, ReviewDiffSide side)
    {
        var lines = new HashSet<int>();
        var oldLine = 0;
        var newLine = 0;

        foreach (var patchLine in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = HunkHeader().Match(patchLine);
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (patchLine.StartsWith("+++", StringComparison.Ordinal) || patchLine.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (patchLine.StartsWith('+'))
            {
                if (side == ReviewDiffSide.Right)
                    lines.Add(newLine);
                newLine++;
            }
            else if (patchLine.StartsWith('-'))
            {
                if (side == ReviewDiffSide.Left)
                    lines.Add(oldLine);
                oldLine++;
            }
            else if (patchLine.StartsWith(' '))
            {
                lines.Add(side == ReviewDiffSide.Right ? newLine : oldLine);
                oldLine++;
                newLine++;
            }
        }

        return lines;
    }

    public static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimStart('/');

    private static string NormalizeReviewText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : CollapseWhitespace().Replace(value.Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
