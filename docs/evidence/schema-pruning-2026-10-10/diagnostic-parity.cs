using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
var branches = JsonNode.Parse("""
[true,false,{}, {"type":"string"},{"type":"number"},{"type":"integer"},{"type":["object","null"]},
 {"const":null},{"enum":[1,true,"a"]},
 {"properties":{"tag":{"const":"a"},"child":{"type":"integer"}},"required":["tag"]},
 {"type":"object","properties":{"tag":{"const":"b"},"child":{"type":"string"}}},
 {"properties":{"tag":{"$ref":"#/$defs/tag"}},"additionalProperties":false},
 {"$ref":"#/$defs/base","properties":{"tag":{"const":"a"}}},
 {"$ref":"#/$defs/missing"}, {"$ref":"#/$defs/cycle","enum":[null,"a"]},
 {"allOf":[{"type":"object"}],"properties":{"tag":{"enum":["a","b"]}}},
 {"if":{"type":"object"},"then":{"required":["tag"]},"else":{"type":"string"}},
 {"type":"string","properties":{"tag":false}}, {"discriminator":{"propertyName":"tag"}},
 {"anyOf":[{"type":"string"},{"type":"object"}],"properties":{"tag":{"const":"a"}}}]
""")!.AsArray();
var values = JsonNode.Parse("""[null,true,1,1.0,1.5,"a","b",[],[1],{}, {"tag":"a"},{"child":false,"tag":"a"},{"tag":"b","child":1},{"tag":null},{"other":"a"}]""")!.AsArray();
var count=0; var timer=System.Diagnostics.Stopwatch.StartNew();
foreach(var op in new[]{"anyOf","oneOf"}) foreach(var a in branches) foreach(var b in branches) foreach(var value in values)
{
 var schema = JsonNode.Parse("""{"$defs":{"tag":{"const":"b"},"base":{"type":"object","properties":{"child":{"type":"integer"}}},"cycle":{"$ref":"#/$defs/cycle","type":["string","null"]}}}""")!.AsObject();
 schema[op]=new JsonArray(a?.DeepClone(),b?.DeepClone());
 var before=JsonSerializer.Serialize(LegacyValidator.ValidateInstanceFindings(value,schema));
 var after=JsonSerializer.Serialize(PlanningContractValidation.ValidateInstanceFindings(value,schema));
 if(before!=after) throw new Exception($"Diagnostic mismatch: {schema} / {value} / {before} / {after}");
 count++;
}
Console.WriteLine(JsonSerializer.Serialize(new { comparisons=count, diagnostic_mismatches=0, elapsed_ms=timer.ElapsedMilliseconds, baseline="6269577e" }));
