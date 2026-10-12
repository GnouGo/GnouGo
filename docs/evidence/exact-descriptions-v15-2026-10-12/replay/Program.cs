using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using System.Diagnostics;

// Public encrypted-storage reads only. No workflow engine, provider or MCP.
if (args.Length is not (2 or 4)) throw new ArgumentException("workspace expected-description.json [v14.yaml v15.yaml]");
var expected = JsonNode.Parse(File.ReadAllText(args[1]))!;
var runId = expected["run"]!.GetValue<string>();
var journal = await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: args[0]).ReadAsync("benchmark", runId) ?? throw new Exception("Missing run");
var decision = journal.Invocations.Values.Single(i => i.Id.Contains("/iteration/2/", StringComparison.Ordinal) && i.StepType == "llm.call" && !i.Id.Contains("/mapping/", StringComparison.Ordinal));
var mapping = journal.Invocations.Values.Single(i => i.Id.Contains("/iteration/2/", StringComparison.Ordinal) && i.StepType == "mapping.dynamic");
var prompt = decision.ResolvedInput!["prompt"]!.GetValue<string>();
Require(Hash(prompt) == expected["decision_prompt_sha256"]!.GetValue<string>(), "Decision changed");
Require(decision.ExternalCompletionObserved && decision.Output!["json"]!["descriptionId"] is null, "Historical decision differs");
var offered = JsonNode.Parse(prompt[(prompt.LastIndexOf("Business data (JSON):", StringComparison.Ordinal) + "Business data (JSON):".Length)..])!["offeredFactCandidates"]!.AsArray();
var input = mapping.ResolvedInput!;
var before = Hash(input.ToJsonString());
var pages = input["sources"]![input["each"]!["input"]!.ToString()]!.AsArray();
var originals = new JsonArray(pages.SelectMany(p => p!["records"]!.AsArray()).Select(r => r!.DeepClone()).ToArray());
Require(pages.Count == 15 && originals.Count == 585 && offered.Count == 44, "Coverage changed");
var results = new JsonArray();
foreach (var block in expected["acceptable_complete_blocks"]!.AsArray())
{
    var id = block!["reference"]!.GetValue<string>();
    var ids = new JsonArray(id);
    JsonArray Lookup(JsonArray records) => new JintSandbox().ExecuteMapping("m.lookup(source.records,source.ids,'reference')", new JsonObject { ["records"] = records.DeepClone(), ["ids"] = ids.DeepClone() })!.AsArray();
    var proposed = Lookup(offered).Single()!;
    var original = Lookup(originals).Single()!;
    var text = original["text"]!.GetValue<string>();
    Require(text == proposed["observedText"]!.GetValue<string>() && Hash(text) == block["text_sha256"]!.GetValue<string>(), "Text changed");
    results.Add(new JsonObject { ["reference"] = id, ["text_sha256"] = Hash(text), ["exact_offered_original_match"] = true });
}
Require(Hash(input.ToJsonString()) == before, "Observations mutated");
var expressionMeasurements = new JsonArray();
if (args.Length == 4)
{
    var outputHash = Hash(mapping.Output!.ToJsonString());
    foreach (var warm in new[] { false, true })
    {
        string[]? previous = null;
        foreach (var profile in new[] { (Name: "v14", File: args[2]), (Name: "v15", File: args[3]) })
        {
            var workflow = WorkflowParser.Parse(File.ReadAllText(profile.File)).Workflows["w_2c49185f0a169198"];
            var preparation = workflow.Steps.Single(s => s.ExpressionContracts?.ContainsKey("/data/values") == true &&
                s.Input?["data"]?["values"]?.ToString().Contains("offeredFactCandidates", StringComparison.Ordinal) == true);
            var program = ExpressionSegments.Read(preparation.Input!["data"]!["values"]!.GetValue<string>()).Single().Expression;
            var context = new JsonObject { ["steps"] = new JsonObject { ["n_3c64eb58e6504189"] = mapping.Output.DeepClone() },
                ["retainedObservation"] = input["sources"]!.DeepClone() };
            var allowance = new JintSandbox.MappingAllowance(10_000, TimeSpan.FromSeconds(5), 50_000_000);
            var checks = new List<string>(); var start = Stopwatch.GetTimestamp(); var allocated = GC.GetAllocatedBytesForCurrentThread();
            var value = new ExpressionEvaluator().EvaluateCheckedExpression(program, context,
                preparation.ExpressionContracts!["/data/values"]!.AsObject(), allowance,
                (_, origin, code) => checks.Add(origin + ":" + (code ?? "passed")));
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(JsonNode.DeepEquals(value!["offeredFactCandidates"], offered), "Prepared candidates changed");
            if (previous is not null) Require(previous.SequenceEqual(checks), "Validation order changed");
            previous = checks.ToArray();
            var counters = allowance.Snapshot();
            Require(counters["allocated_bytes"]!.GetValue<long>() < 50_000_000 && counters["materialized_bytes"]!.GetValue<long>() < 50_000_000, "Allowance exceeded");
            counters["profile"] = profile.Name; counters["warm"] = warm; counters["total_allocated_bytes"] = allocated;
            counters["evaluation_ms"] = elapsed; counters["checks"] = new JsonArray(checks.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
            counters["exact_candidates"] = true; expressionMeasurements.Add(counters);
        }
    }
    Require(Hash(mapping.Output.ToJsonString()) == outputHash && Hash(input.ToJsonString()) == before, "Retained evidence mutated");
}
Console.WriteLine(new JsonObject { ["historical_verdict_preserved"] = true, ["historical_description_acceptable"] = false,
    ["original_pages"] = pages.Count, ["original_records"] = originals.Count, ["offered_candidates"] = offered.Count,
    ["retained_source_bytes"] = Encoding.UTF8.GetByteCount(input["sources"]!.ToJsonString()),
    ["retained_candidate_output_bytes"] = Encoding.UTF8.GetByteCount(mapping.Output!.ToJsonString()),
    ["acceptable_reconnections"] = results, ["source_sha256"] = before, ["provider_calls"] = 0, ["mcp_calls"] = 0,
    ["historical_writes"] = 0, ["workflow_resumed"] = false, ["expression_measurements"] = expressionMeasurements,
    ["limitation"] = "Deterministic independently reviewed IDs; no claim of future model compliance or changed historical workbook." }.ToJsonString(new() { WriteIndented = true }));
static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
