namespace GnOuGo.AI.Core;

/// <summary>
/// Parses a relevance score (0–10) from LLM text output.
/// </summary>
public static class ScoreParser
{
    /// <summary>
    /// Parses a score from model output. Expects a single number 0–10.
    /// Falls back to extracting the first digit if the output contains extra text.
    /// </summary>
    public static double Parse(string content)
    {
        if (double.TryParse(content, out var score))
            return Math.Clamp(score, 0, 10);

        foreach (var ch in content)
        {
            if (char.IsDigit(ch) && double.TryParse(ch.ToString(), out var digit))
                return digit;
        }

        return 0;
    }
}

