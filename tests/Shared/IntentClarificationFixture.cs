using System.Text.Json.Nodes;

namespace GnOuGo.Testing;

// Sanitized reproduction: three valid questions rejected because independently exact
// excerpts were formatted as one quoted string. No user content or provider names.
internal static class IntentClarificationFixture
{
    internal const string Prompt = "Review a proposed change. Run available checks. Apply the supplied review instructions.";
    internal static JsonObject Ready() => new() { ["outcome"] = "ready", ["reason"] = "The behavior is explicit.", ["evidence"] = new JsonArray(), ["questions"] = new JsonArray() };
    internal static JsonArray Evidence(string excerpt, string source = "request") => new(new JsonObject { ["sourceId"] = source, ["excerpt"] = excerpt });
    internal static JsonObject Questions(string prompt = Prompt)
    {
        var questions = new JsonArray();
        var descriptions = new[] { "Which review outcome should be published?", "Which checks should inform the review?", "Are the supplied instructions mandatory approval criteria?" };
        for (var index = 0; index < 3; index++) questions.Add(new JsonObject
        {
            ["id"] = "choice_" + index, ["prompt"] = descriptions[index], ["evidence"] = Evidence(prompt),
            ["options"] = new JsonArray(
                new JsonObject { ["value"] = "first", ["description"] = "Use the first proposed behavior.", ["recommended"] = true },
                new JsonObject { ["value"] = "second", ["description"] = "Use the second proposed behavior.", ["recommended"] = false })
        });
        return new() { ["outcome"] = "questions", ["reason"] = "Clarify three observable choices.", ["evidence"] = Evidence(prompt), ["questions"] = questions };
    }
    internal static JsonObject QuotedQuestions()
    {
        var result = Questions();
        result["evidence"] = "« Review a proposed change. » ; « Run available checks. »";
        foreach (var question in result["questions"]!.AsArray()) question!["evidence"] = "« " + Prompt + " »";
        return result;
    }
}
