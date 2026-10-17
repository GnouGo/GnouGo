using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;
var script = JsonNode.Parse(File.ReadAllText("retained-script.json"))!["script"]!.GetValue<string>();
var source = JsonNode.Parse("""{"afterCookiePages":[{"records":[{"kind":"control","selector":"#search-observed","text":"Search","role":"searchbox","group":"#panel"}]}]}""")!.AsObject();
var schema = JsonNode.Parse("""{"type":"object","properties":{"observedSearchControls":{"type":"array","items":{"type":"object","properties":{"selector":{"type":"string"},"text":{"type":["string","null"]},"role":{"type":["string","null"]},"group":{"type":["string","null"]}},"required":["selector","text","role","group"],"additionalProperties":false}},"observedConsentControls":{"type":"array","items":{"type":"object"}},"captchaOrBlockerIndicators":{"type":"array","items":{"type":"string"}}},"required":["observedSearchControls","observedConsentControls","captchaOrBlockerIndicators"],"additionalProperties":false}""")!.AsObject();
try { new JintSandbox().ExecuteMappingItems(script, source, "afterCookiePages", schema, CancellationToken.None); throw new InvalidOperationException("The retained invalid script unexpectedly succeeded."); }
catch (WorkflowRuntimeException e) { Console.WriteLine(new JsonObject { ["retained_script_rejected"] = true, ["code"] = e.Code, ["message"] = e.Message }.ToJsonString()); }
var corrected = script.Replace("source.records", "source.afterCookiePages.records", StringComparison.Ordinal);
var output = new JintSandbox().ExecuteMappingItems(corrected, source, "afterCookiePages", schema, CancellationToken.None);
Console.WriteLine(new JsonObject { ["diagnostic_correct_path_only"] = true, ["output"] = output, ["model_calls"] = 0,
    ["saved_workflow_modified"] = false, ["historical_invocation_replayed"] = false }.ToJsonString());
