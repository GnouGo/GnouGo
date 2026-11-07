using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Planning.Examples;

// Independently authored expectations for the short business Judge. These cases
// deliberately remain technically valid; compiler failures are a different gate.
public sealed record BusinessReviewCorpusCase(string Id, string Original, TaskPlan Plan, PlanningCatalog Catalog,
    bool Global, string ExpectedVerdict, string ExpectedObligation = "intent");

public static class BusinessReviewCorpus
{
    public const string OracleVersion = "business-review-corpus-1";
    public static IReadOnlyList<BusinessReviewCorpusCase> Cases()
    {
        var read = Contract("read", "Read the exact requested subject. A pull request and its containing repository are different entities.", "read", "target");
        var write = Contract("write", "Write the supplied content at the target. mode append adds to existing content; mode overwrite replaces it.", "write", "target", "content", "mode");
        write.InputSchema["properties"]!["mode"]!["enum"] = new JsonArray("append", "overwrite");
        var observe = Contract("observe", "Read an observation. flat is visible text only; complete preserves all page, identity and continuation information.", "read", "target");
        observe.OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
        {
            ["flat"] = new JsonObject { ["type"] = "string", ["description"] = "Visible flat text; omits pagination and identities." },
            ["complete"] = new JsonObject { ["type"] = "string", ["description"] = "Serialized complete observation, including page identity and continuation." }
        }, ["required"] = new JsonArray("flat", "complete"), ["additionalProperties"] = false };
        var open = Contract("open", "Open the explicitly named workflow-owned resource at target.", "write", "target");
        var close = Contract("close", "Close the workflow-owned resource at target, including after another action fails.", "write", "target");
        return
        [
            Case("wrong-entity", "Read pull request pr/7, not repository repo/project.", [read],
                Operation("read_subject", "read", ("target", Text("repo/project")))) ,
            Case("wrong-write-mode", "Append the text result to local/report.txt; do not overwrite its existing content.", [write],
                Operation("save", "write", ("target", Text("local/report.txt")), ("content", Text("result")), ("mode", Text("overwrite")))),
            Case("wrong-semantic-port", "Write the complete observation for subject/7, preserving its page identity and continuation, to local/report.txt.", [observe, write],
                Operation("observe_subject", "observe", ("target", Text("subject/7"))),
                Operation("save", "write", ("target", Text("local/report.txt")), ("content", Output("observe_subject", "flat")), ("mode", Text("append")))),
            Case("missing-required-action", "Read subject/7, then write the returned data to local/report.txt.", [read, write],
                Operation("read_subject", "read", ("target", Text("subject/7")))),
            Case("explicitly-forbidden-duplicate", "Read subject/7 exactly once, without a second read.", [read],
                Operation("first_read", "read", ("target", Text("subject/7"))),
                Operation("second_read", "read", ("target", Text("subject/7")))),
            Case("missing-failure-cleanup", "Open the workflow-owned resource local/resource and close it even if later work fails.", [open, close],
                Operation("open_resource", "open", ("target", Text("local/resource")))),
            Case("valid-open-fragment", "Read subject/7, then write its returned data to local/report.txt.", [read, write],
                Operation("read_subject", "read", ("target", Text("subject/7")))) with { Global = false, ExpectedVerdict = "ok" },
            Case("valid-completed-workflow", "Read subject/7 exactly once and append its returned data to local/report.txt.", [read, write],
                Operation("read_subject", "read", ("target", Text("subject/7"))),
                Operation("save", "write", ("target", Text("local/report.txt")), ("content", Output("read_subject", "data")), ("mode", Text("append")))) with { ExpectedVerdict = "ok" }
        ];
    }

    public static PlanningSession Session(BusinessReviewCorpusCase item, PlanningRequest? request = null)
    {
        request ??= new() { TenantId = "business-review-corpus", Prompt = item.Original };
        if (request.Prompt != item.Original) throw new ArgumentException("The frozen corpus request must preserve its exact original obligation.", nameof(request));
        var state = new PlanningSession { Request = request };
        PlanningSessionCreation.Capture(state);
        state.Catalog = item.Catalog;
        state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("intent", item.Original)] };
        state.Plan = new();
        var compact = state.Construction!.Compact!;
        compact.Sources =
        [
            new() { Id = "request:0", Kind = "page", Indexed = true, Text = item.Original, Version = PlanningGraphCompiler.Fingerprint(item.Original) },
            new() { Id = "request:0:clause:0", Kind = "requirement", Inputs = [], Outputs = [], Indexed = true,
                Text = item.Original, Version = PlanningGraphCompiler.Fingerprint(item.Original) }
        ];
        compact.RequirementSources["intent"] = ["request:0:clause:0"];
        state.Construction.Requirements = [new() { Id = "intent" }];
        compact.CurrentStep = new() { Id = "corpus", Scope = "/root", Kind = item.Global ? "close" : "operation", Objective = "Implement the exact original obligation.",
            RequirementIds = ["intent"], CloseRequirementIds = item.Global ? ["intent"] : [],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = new()
        {
            BaseHash = PlanningConstruction.Hash(state.Plan), Hash = PlanningConstruction.Hash(item.Plan), Plan = PlanningConstruction.Clone(item.Plan),
            TaskIds = TaskPlanRevisions.Tasks(item.Plan).Select(task => task.Id).ToList(), OpenScopes = item.Global ? [] : ["/root"],
            Extension = new() { Scope = "/root", Tasks = item.Plan.Root.Tasks, RequirementIds = ["intent"], Complete = item.Global,
                CloseScopes = item.Global ? ["/root"] : [] }
        };
        return state;
    }

    private static BusinessReviewCorpusCase Case(string id, string original, List<PlanningCapability> capabilities, params PlanTask[] tasks) =>
        new(id, original, new() { Root = new() { Tasks = tasks.ToList() } }, new() { Capabilities = capabilities, AllowedStepTypes = ["mcp.call"] }, true, "needs_change");
    private static PlanTask Operation(string id, string operation, params (string Name, TaskValue Value)[] arguments) => new()
    { Id = id, Kind = "operation", Objective = "Perform the declared operation with these actual arguments.", Operation = operation,
        Inputs = arguments.Select(argument => new TaskOutput(argument.Name, argument.Value)).ToList() };
    private static TaskValue Text(string value) => new() { Kind = "string", Text = value };
    private static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
    private static PlanningCapability Contract(string id, string description, string effect, params string[] inputs) => new()
    {
        Id = id, Version = "1", Kind = "tool", StepType = "mcp.call", Server = "synthetic", Method = id, EffectKind = effect,
        Description = description,
        InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(inputs.Select(name => new KeyValuePair<string, JsonNode?>(name,
            new JsonObject { ["type"] = "string" }))), ["required"] = new JsonArray(inputs.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()), ["additionalProperties"] = false },
        OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("data"), ["additionalProperties"] = false }
    };
}
