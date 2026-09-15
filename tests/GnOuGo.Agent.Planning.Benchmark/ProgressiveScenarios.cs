using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveScenarios
{
    // These are benchmark business inputs, never production dispatch rules.
    internal const string Simple = """
        Create one reusable workflow classifying a single record.
        Required input record is an object with required id:string, amount:number and approved:boolean.
        Optional input threshold is a non-nullable number defaulting to 100 when omitted.
        Return classifiedResult:{id:string,amount:number,category:string}, all members required.
        Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.
        category has exactly the values rejected, high, standard. Preserve the original id and amount.
        This is deterministic, local, in-memory business processing.
        """;
    internal const string Medium = """
        Create a reusable record-batch processor with main, classify_record and summarize_batch workflows.
        main accepts required batchId:string and optional non-nullable threshold:number defaulting to 100.
        Load the batch records once through the available read-only capability, then iterate records in their original order,
        calling classify_record with each record and threshold. Call summarize_batch once with the collected classifications.
        classify_record receives record:{id:string,amount:number,approved:boolean} and threshold:number, all required and non-nullable.
        It returns classifiedResult:{id:string,amount:number,category:string}, with original id and amount and category exactly
        rejected when approved=false, high when approved=true and amount>=threshold, or standard otherwise.
        summarize_batch receives classifiedResults, an array whose items have required id:string,amount:number,category:rejected|high|standard.
        Return summary:{totalCount:integer,highCount:integer,rejectedCount:integer,totalAcceptedAmount:number}.
        totalCount is array length, highCount counts high, rejectedCount counts rejected, and totalAcceptedAmount sums non-rejected amounts.
        Empty records produces four zero totals. All these members are required and non-nullable.
        main has one native set finalizer producing only internal {finished:true}, once on success or runtime failure after execution begins,
        preserving the original error. Classification and aggregation are deterministic local computations.
        """;

    internal static JsonArray Catalog(int stage, JsonObject evidence) => stage switch
    {
        1 => [],
        2 => JsonNode.Parse("""
            [{"name":"ProgressiveFixture","description":"Read-only record batch source for isolated validation.","discovered":true,"tools":[
            {"name":"load_record_batch","description":"Loads one batch by its batchId. Returns the original records in source order. Read-only; creates no resource and changes no external state.",
             "inputSchema":{"type":"object","properties":{"batchId":{"type":"string"}},"required":["batchId"],"additionalProperties":false},
             "outputSchema":{"type":"object","properties":{"records":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"amount":{"type":"number"},"approved":{"type":"boolean"}},"required":["id","amount","approved"],"additionalProperties":true}}},"required":["records"],"additionalProperties":false}}
            ],"prompts":[]}]
            """)!.AsArray(),
        3 => evidence["discovery"]!.DeepClone().AsArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    internal static string Prompt(int stage, JsonObject evidence, string policy) => stage switch
    { 1 => Simple, 2 => Medium, 3 => evidence["codeReviewPrompt"]!.ToString() + "\n" + policy, _ => throw new ArgumentOutOfRangeException(nameof(stage)) };
    internal static string[] Cases(int stage) => stage switch
    {
        1 => ["accepted", "rejected", "boundary", "omitted_default", "invalid_input", "null_threshold"],
        2 => ["mixed", "empty", "boundary", "rejected", "omitted_default", "invalid_input", "null_threshold", "read_failure", "callee_failure"],
        3 => (from pull in new[] { 449, 510 } from kind in new[] { "passing", "mixed", "empty", "boundary", "rejected", "omitted_defaults", "invalid_input", "setup_failure", "execution_failure" } select pull + ":" + kind).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    internal static string FixtureHash(int stage) => stage == 3 ? CodeReviewExecutionFixture.Fingerprint :
        PlanningGraphCompiler.Fingerprint((stage == 1 ? Simple : Medium) + ":" + string.Join('|', Cases(stage)) + ":" + typeof(ProgressiveScenarios).Assembly.ManifestModule.ModuleVersionId);
}
