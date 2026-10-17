using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
var bytes = await File.ReadAllBytesAsync(args[0]);
var run = JsonNode.Parse(bytes)!.AsObject();
var events = run["events"]!.AsArray().OfType<JsonObject>().ToArray();
using var workbook = SpreadsheetDocument.Open(args[1], false);
var part = workbook.WorkbookPart!;
var rows = part.WorksheetParts.Single().Worksheet!.Descendants<Row>().Select(row => row.Elements<Cell>().Select(cell =>
    cell.DataType?.Value == CellValues.SharedString ? part.SharedStringTablePart!.SharedStringTable!.Elements<SharedStringItem>().ElementAt(int.Parse(cell.InnerText)).InnerText : cell.InnerText).ToArray()).ToArray();
var retainedRows = run["workbook_rows"]!.AsArray().Select(row => row!.AsArray().Select(cell => cell!.ToString()).ToArray()).ToArray();
if (!rows.SelectMany(r => r).SequenceEqual(retainedRows.SelectMany(r => r)) || rows.Length != retainedRows.Length) throw new Exception("Workbook changed");
var method = Assembly.Load("GnOuGo.Agent.Server.Tests").GetType("LiveWorkflowOracles")!.GetMethod("VerifyObservedProducts", BindingFlags.NonPublic | BindingFlags.Static)!;
var report = (JsonObject)method.Invoke(null, [events, rows, 10])!;
if (report["visit_count"]!.GetValue<int>() != 10 || report["passed"]!.GetValue<bool>()) throw new Exception("Unexpected retained verdict");
report["record_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
report["workbook_sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(args[1]))).ToLowerInvariant();
report["provider_calls"] = 0; report["mcp_calls"] = 0; report["historical_records_modified"] = false;
await File.WriteAllTextAsync(args[2], report.ToJsonString(new() { WriteIndented = true }));
Console.WriteLine($"visits={report["visit_count"]}; passed={report["passed"]}; rows={rows.Length}; provider_calls=0; mcp_calls=0");

var state = run["session"]!.Deserialize(GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.PlanningSession)!;
var comparison = new JsonArray();
var compile = typeof(GnOuGo.Flow.Planning.PlanningGraphCompiler).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == "Compile" && m.GetParameters().Length == 10);
foreach (var profile in new[] { "compact-bindings-v8", "compact-bindings-v9" })
{
    var request = System.Text.Json.JsonSerializer.SerializeToNode(state.Request, GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.PlanningRequest)!.Deserialize(GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.PlanningRequest)!;
    request.Options["compilation_profile"] = profile;
    var taskCompile = typeof(GnOuGo.Flow.Planning.TaskPlanCompiler).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == "Compile" && m.GetParameters().Length == 3 && m.GetParameters()[2].ParameterType == typeof(GnOuGo.Flow.Core.Planning.PlanningRequest));
    var compiled = (GnOuGo.Flow.Planning.TaskCompilation)taskCompile.Invoke(new GnOuGo.Flow.Planning.TaskPlanCompiler(), [state.Plan!, state.Catalog!, request])!;
    if (compiled.Graph is null || compiled.Diagnostics.Count > 0) throw new Exception("Retained compilation failed");
    var yaml = (string)compile.Invoke(new GnOuGo.Flow.Planning.PlanningGraphCompiler(), [compiled.Graph, state.Catalog!, request.Name, true, true, true, true, true, true, profile.EndsWith("v9")])!;
    var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml);
    var steps = new List<GnOuGo.Flow.Core.Models.StepDef>();
    void Add(List<GnOuGo.Flow.Core.Models.StepDef> list) { foreach(var s in list) { steps.Add(s); Add(s.Steps ?? []); Add(s.Default ?? []); foreach(var c in s.Cases ?? []) Add(c.Steps); } }
    foreach(var w in document.Workflows.Values) { Add(w.Steps); Add(w.Finally ?? []); }
    comparison.Add(new JsonObject { ["profile"] = profile, ["yaml_bytes"] = System.Text.Encoding.UTF8.GetByteCount(yaml), ["max_line"] = yaml.Split('\n').Max(l => l.Length),
        ["steps"] = steps.Count, ["sets"] = steps.Count(s => s.Type == "set"), ["select_helpers"] = System.Text.RegularExpressions.Regex.Matches(yaml, @"m\.select\(").Count,
        ["checked_wrappers"] = System.Text.RegularExpressions.Regex.Matches(yaml, @"checkedMapping\(").Count, ["reproduces_retained_yaml"] = yaml == state.Yaml });
}
await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(args[2])!, "retained-compilation-comparison.json"), comparison.ToJsonString(new() { WriteIndented = true }));
