using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
var before = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var after = JsonNode.Parse(File.ReadAllText(args[1]))!.AsArray();
int Estimate(string prompt, JsonNode schema) => (Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schema.ToJsonString()) + 2) / 3 + 256;
var rows = new JsonArray();
for (var i = 0; i < before.Count; i++) {
 var oldPrompt = before[i]!["prompt"]!.ToString(); var newPrompt = after[i]!["prompt"]!.ToString();
 var splitOld = oldPrompt.IndexOf("\n{", StringComparison.Ordinal); var splitNew = newPrompt.IndexOf("\n{", StringComparison.Ordinal);
 var oldSchema = before[i]!["structuredOutputSchema"]!; var newSchema = after[i]!["structuredOutputSchema"]!;
 var fixedPrompt = newPrompt[..splitNew] + oldPrompt[splitOld..];
 rows.Add(new JsonObject { ["call"] = i+1, ["baseline"] = Estimate(oldPrompt, oldSchema), ["presentation_only"] = Estimate(fixedPrompt, oldSchema), ["complete_candidate"] = Estimate(newPrompt, newSchema),
 ["same_context_in_presentation_pair"] = fixedPrompt[splitNew..] == oldPrompt[splitOld..], ["same_schema_in_presentation_pair"] = true });
}
Console.WriteLine(new JsonObject { ["mode"] = "scripted", ["baseline_prompt"] = "generic at 676b5ace; not scenario-specific", ["provider_calls"] = 0,
 ["baseline_estimated_total"] = rows.Sum(r=>r!["baseline"]!.GetValue<int>()), ["presentation_only_estimated_total"] = rows.Sum(r=>r!["presentation_only"]!.GetValue<int>()), ["complete_candidate_estimated_total"] = rows.Sum(r=>r!["complete_candidate"]!.GetValue<int>()),
 ["calls"] = rows, ["reliability_conclusion"] = "inconclusive; fixed responses do not measure model planning correctness" }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
