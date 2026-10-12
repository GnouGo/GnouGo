using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Persistence;
using Microsoft.ML.Tokenizers;

// Read only: no engine, lease, provider, MCP, cache or storage mutation.
var expectedText = await File.ReadAllTextAsync(args[2]);
var expected = JsonNode.Parse(expectedText)!;
Require(Hash(expectedText) == "745fb5730d8fccd1e88c3c5b2e15a99556f322790a4b08d501a0ca504834bcd0", "Reviewed oracle changed");
var journal = await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: args[0])
    .ReadAsync("benchmark", "compactv1320261012a-amazon-1") ?? throw new Exception("Missing journal");
const string mappingStep = "n_fa2c51243ab17014";
var invocation = journal.Invocations["/workflow/main/step/" + mappingStep];
var original = journal.Invocations[invocation.Id + "/mapping/0"].Output!["json"]!["script"]!.GetValue<string>();
var input = invocation.ResolvedInput!.DeepClone();
var before = Hash(input.ToJsonString());
Require(before == expected["source_input_sha256"]!.GetValue<string>(), "Original observations differ");
var sources = input["sources"]!.AsObject();
var each = input["each"]!;
var inputName = each["input"]!.GetValue<string>();
var pages = sources[inputName]!.AsArray();
var originals = new JsonArray(pages.SelectMany(p => p!["records"]!.AsArray()).Select(r => r!.DeepClone()).ToArray());
var document = WorkflowParser.Parse(await File.ReadAllTextAsync(args[1]));
var target = document.Workflows.Values.SelectMany(w => w.Steps).Single(s => s.Id == mappingStep)
    .OutputSchema!["properties"]!["value"]!["properties"]![each["output"]!.GetValue<string>()]!["items"]!.AsObject();
Require(pages.Count == 54 && originals.Count == 1540, "Source coverage differs");
var sandbox = new JintSandbox();
var old = sandbox.ExecuteMappingItems(original, sources, inputName, target);
Require(old.All(page => page!.AsArray().Count == 0), "Historical script outcome differs");
WorkflowRuntimeException? preflight = null;
try { JintSandbox.ValidateMapping(original); }
catch (WorkflowRuntimeException error) { preflight = error; }
Require(preflight?.Details?["mapping_failure_kind"]?.ToString() == "program", "Lost escape was not diagnosed");

// Preserve the declared compact view. Relevance is decided over offered records;
// the adaptation never guesses heading ancestry or URL path conventions.
const string corrected = "item.records.filter(record=>m.test(record.kind,'^link$')).map(record=>({reference:record.reference,text:record.text,role:record.role}))";
JintSandbox.ValidateMapping(corrected);
var measurements = new JsonArray();
JsonArray? rows = null;
foreach (var phase in new[] { "first_corrected_evaluation", "warm_corrected_evaluation" })
{
    var allocated = GC.GetTotalAllocatedBytes(true);
    var watch = Stopwatch.StartNew();
    rows = sandbox.ExecuteMappingItems(corrected, sources, inputName, target);
    watch.Stop();
    measurements.Add(new JsonObject { ["phase"] = phase, ["elapsed_ms"] = watch.Elapsed.TotalMilliseconds,
        ["allocated_bytes"] = GC.GetTotalAllocatedBytes(true) - allocated });
}
var offered = new JsonArray(rows!.SelectMany(p => p!.AsArray()).Select(r => r!.DeepClone()).ToArray());
var selected = new JsonArray(expected["expected_selection"]!.AsArray().Select(r => r!["reference"]!.DeepClone()).ToArray());
var offeredSelection = sandbox.ExecuteMapping("m.lookup(source.records,source.ids,'reference')",
    new JsonObject { ["records"] = offered.DeepClone(), ["ids"] = selected.DeepClone() })!.AsArray();
var observedSelection = sandbox.ExecuteMapping("m.lookup(source.records,source.ids,'reference')",
    new JsonObject { ["records"] = originals.DeepClone(), ["ids"] = selected.DeepClone() })!.AsArray();
for (var index = 0; index < selected.Count; index++)
{
    var wanted = expected["expected_selection"]![index]!;
    var originalIndex = wanted["index"]!.GetValue<int>();
    Require(JsonNode.DeepEquals(originals[originalIndex], observedSelection[index]), "Reconnection changed original data");
    Require(offeredSelection[index]!["text"]!.ToString() == wanted["text"]!.ToString(), "Offered title changed");
    Require(originals[wanted["heading_index"]!.GetValue<int>()]!["text"]!.ToString() == wanted["text"]!.ToString(), "Independent heading evidence differs");
}
Require(expected["negative_selections"]!.AsArray().All(n => !selected.Any(id => JsonNode.DeepEquals(id, n!["reference"]))), "Negative example selected");

var decision = journal.Invocations["/workflow/main/step/n_b23b3d2c5a6a7e42"].ResolvedInput!;
var prompt = decision["prompt"]!.GetValue<string>();
const string emptyView = "\"offered_candidates\":[]";
Require(prompt.Split(emptyView).Length == 2, "Original decision prompt view differs");
var request = new LLMRequest { Provider = "OpenAi", Model = "gpt-5.5-2026-04-24",
    Prompt = prompt.Replace(emptyView, "\"offered_candidates\":" + offered.ToJsonString(), StringComparison.Ordinal),
    StructuredOutputSchema = decision["structured_output"]!["schema_inline"]!.DeepClone(),
    StructuredOutputStrict = decision["structured_output"]?["strict"]?.GetValue<bool>() ?? true,
    MaxTokens = decision["max_tokens"]?.GetValue<int>() };
var serialized = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
// Exact existing campaign estimate: full Flow request, o200k + 25% + 4096 framing.
var estimatedTokens = checked((int)Math.Ceiling(TiktokenTokenizer.CreateForEncoding("o200k_base").CountTokens(serialized) * 1.25) + 4096);
Require(estimatedTokens <= 96000, "Necessary compact view exceeds the unchanged allowance");
Require(Hash(input.ToJsonString()) == before, "Source observations mutated");
Console.WriteLine(new JsonObject {
    ["provider_calls"] = 0, ["mcp_calls"] = 0, ["storage_writes"] = 0, ["workflow_resumed"] = false,
    ["oracle_sha256"] = Hash(expectedText), ["source_sha256"] = before,
    ["pages_processed"] = rows!.Count, ["original_records"] = originals.Count,
    ["historical_candidates"] = 0, ["preflight"] = preflight!.Details!.DeepClone(),
    ["corrected_script"] = corrected, ["compact_offered_records"] = offered.Count,
    ["independently_selected_titles"] = selected.Count, ["exact_reconnection"] = true,
    ["negative_selections_rejected"] = true, ["request_bytes"] = Encoding.UTF8.GetByteCount(serialized),
    ["request_estimated_input_tokens"] = estimatedTokens, ["unchanged_input_allowance"] = 96000,
    ["measurements"] = measurements,
    ["limitation"] = "Deterministic reviewed selection; no model compliance or missing product-page/workbook evidence is claimed."
}.ToJsonString(new() { WriteIndented = true }));

static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
