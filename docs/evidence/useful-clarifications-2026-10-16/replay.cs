using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

// Read public encrypted storage only. Replay saved scripts, never the workflow.
const string session = "fd471af6e0594916a202d6e3216b21e0", tenant = "default", author = "GnOuGo.Agent.Server.Planning";
var vault = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
var retained = new List<(string Collection, string Key, string Author, string Value)>();
var states = (await vault.ListAsync("agent-planning-sessions-v10", tenant, author)).Where(r => r.Key.StartsWith(session + ":", StringComparison.Ordinal)).ToArray();
retained.AddRange(states.Select(r => ("agent-planning-sessions-v10", r.Key, author, r.Value)));
var state = states.Select(r => JsonSerializer.Deserialize(r.Value, PlanningJsonContext.Default.PlanningSession)!).MaxBy(s => s.Revision)!;
var report = new JsonObject { ["session"] = session, ["revision"] = state.Revision, ["status"] = state.Status,
    ["planningCalls"] = state.ModelCalls, ["repairs"] = state.ReplanAttempts, ["providerCalls"] = 0, ["mcpCalls"] = 0,
    ["receipts"] = new JsonArray(), ["runs"] = new JsonArray() };
foreach (var record in (await vault.ListAsync("agent-planning-model-requests-v10", tenant, author)).Where(r => r.Key.StartsWith(session + ":", StringComparison.Ordinal)))
{
    var receipt = (await vault.GetAsync("agent-planning-model-receipts-v10", tenant, record.Key, author))!;
    retained.Add(("agent-planning-model-requests-v10", record.Key, author, record.Value));
    retained.Add(("agent-planning-model-receipts-v10", record.Key, author, receipt.Value));
    var request = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.LLMRequest)!;
    var response = JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)!;
    var findings = PlanningContractValidation.ValidateInstanceFindings(response.Json ?? JsonNode.Parse(response.Text), request.StructuredOutputSchema!);
    if (findings.Count > 0) throw new Exception("Issued schema mismatch");
    var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
    report["receipts"]!.AsArray().Add((JsonNode)new JsonObject { ["requestSha256"] = Hash(record.Value), ["receiptSha256"] = Hash(receipt.Value),
        ["schemaValid"] = true, ["structuredOutputStrict"] = request.StructuredOutputStrict, ["issuedRequestBytes"] = Encoding.UTF8.GetByteCount(record.Value),
        ["presentedOperations"] = context["operations"]!.AsArray().Count, ["discoveryAllowed"] = context["budget"]!["discoveryAllowed"]!.DeepClone() });
}
var store = EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: args[0]);
foreach (var id in new[] { "00f7d72b10dce5a6e0dbb368a003fec7", "d182b7abc8384d37be0b6058879272c9" })
{
    var head = (await vault.GetAsync(EncryptedWorkflowRunStore.Collection, tenant, id, "GnOuGo.Flow.Persistence"))!;
    retained.Add((EncryptedWorkflowRunStore.Collection, id, "GnOuGo.Flow.Persistence", head.Value));
    var run = (await store.ReadAsync(tenant, id))!;
    if (run.WorkflowYaml.TrimEnd() != state.Yaml!.TrimEnd()) throw new Exception("Wrong artifact");
    var mapping = run.Invocations.Values.Single(i => i.StepType == "mapping.dynamic" && i.Status == "failed");
    var target = WorkflowParser.Parse(run.WorkflowYaml).Workflows["main"].Steps.Single(s => mapping.Id.EndsWith("/" + s.Id, StringComparison.Ordinal)).OutputSchema!["properties"]!["value"]!.AsObject();
    var scripts = new JsonArray();
    foreach (var receipt in run.Invocations.Values.Where(i => i.StepType == "llm.call").OrderBy(i => i.Id))
    {
        var script = receipt.Output!["json"]!["script"]!.GetValue<string>();
        try { new JintSandbox().ExecuteMapping(script, mapping.ResolvedInput!["sources"], target: target); throw new Exception("Expected retained failure"); }
        catch (WorkflowRuntimeException error)
        {
            scripts.Add((JsonNode)new JsonObject { ["scriptSha256"] = Hash(script), ["code"] = error.Code,
                ["message"] = error.Message, ["details"] = error.Details?.DeepClone() });
        }
    }
    report["runs"]!.AsArray().Add((JsonNode)new JsonObject { ["run"] = id, ["headSha256"] = Hash(head.Value),
        ["status"] = run.Status, ["savedFailure"] = run.Result!.Error!.Message, ["mappingReplay"] = scripts,
        ["externalToolInvocations"] = run.Invocations.Values.Count(i => i.StepType == "mcp.call"),
        ["committedModelReceipts"] = run.Invocations.Values.Count(i => i.StepType == "llm.call" && i.CompletedAt is not null),
        ["cleanupStatus"] = run.Invocations.Values.Single(i => i.IsFinalization).Status });
}
var proposal = JsonSerializer.SerializeToNode(new PlanningProposal { Plan = state.Plan, Requirements = state.Requirements }, PlanningJsonContext.Default.PlanningProposal)!;
Sanitize(proposal);
Directory.CreateDirectory(args[1]);
await File.WriteAllTextAsync(Path.Combine(args[1], "retained-proposal.json"), proposal.ToJsonString(new() { WriteIndented = true }));
await File.WriteAllTextAsync(Path.Combine(args[1], "retained-generation.json"), JsonSerializer.Serialize(state.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions));
foreach (var record in retained)
    if (record.Value != (await vault.GetAsync(record.Collection, tenant, record.Key, record.Author))!.Value) throw new Exception("Historical record changed");
report["historicalRecordsUnchanged"] = true;
report["hostCapabilities"] = "The issued request offered discovery but no inspected operations; retained evidence does not establish availability or permissions for the required external work.";
await File.WriteAllTextAsync(Path.Combine(args[1], "retained-replay.json"), report.ToJsonString(new() { WriteIndented = true }));
Console.WriteLine(report.ToJsonString());
static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static void Sanitize(JsonNode? value)
{
    if (value is JsonObject obj)
        foreach (var (key, child) in obj.ToArray())
            if (child is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[key] = Regex.Replace(text, @"https://[^\s""']+", "https://fixture.invalid/change?id=17");
            else Sanitize(child);
    else if (value is JsonArray array) foreach (var child in array) Sanitize(child);
}
