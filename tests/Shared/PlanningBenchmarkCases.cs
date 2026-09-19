using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Planning.Examples;

/// <summary>Frozen business requests and independent execution oracles. No model-produced fixtures are trusted as expected results.</summary>
public static class PlanningBenchmarkCases
{
    public static readonly string[] Names = ["local", "read_transform", "nullable_defaults", "conditional", "collections", "protected_cleanup", "review_french", "review_distractors"];
    public const string FrenchPrompt = "A partir d\u2019une url d\u2019un pullrequest github [https://github.com/AxaFrance/SmartGuide/pull/510](https://github.com/AxaFrance/SmartGuide/pull/510)\nEt d\u2019un texte qui explique ce qu\u2019il faut reviewer.\nJ\u2019aimerais que le workflow r\u00e9alise une review automatique de la pullrequest dont le retour seront pouss\u00e9 sur GitHub afin de pouvoir valider automatiquement ou non et expliquer en commentaire pourquoi.\nPour travailler; un unique clone du projet et travailler uniquement avec ce r\u00e9pertoire pour \u00eatre simple et propre. Installe toutes les d\u00e9pendances, joue les linter, tests unitaires, tests d\u2019int\u00e9grations si pr\u00e9sent. Le feedback de la review doit valider aussi toutes les v\u00e9rifications demand\u00e9es par l\u2019utilisateur.";
    public static string Prompt(string name) => name switch
    {
        "local" => "Return a numeric output named result equal to 6 times 7. No inputs or external calls.",
        "read_transform" => "Read the number with the available read tool, double its value, and return a numeric output named result. No inputs.",
        "nullable_defaults" => "Accept an optional numeric input named increment with default 2. Read the optional number, treat an explicit null value as zero, add increment, and return result. Do not replace explicit null with an input default.",
        "conditional" => "Accept a required boolean input named enabled. When enabled, read the number and return its value as result. Otherwise return result equal to zero without reading anything.",
        "collections" => "Accept a numeric array input named values. Double every element in a reusable subworkflow. Run the per-item calls in parallel and return the ordered numeric array as result. Preserve all items, including duplicates. No external calls.",
        "protected_cleanup" => "Perform the write once, then always invoke cleanup once, including after failure. Return a numeric output named result equal to 42. No inputs.",
        "review_french" => FrenchPrompt,
        "review_distractors" => "Create an automatic pull-request review workflow with runtime inputs pr_url and review_text. Clone the repository exactly once and use that directory for all work. Install dependencies and run lint, unit and available integration tests without changing tracked source. Review every instruction in review_text. Evaluate and publish the review with evidence for each check; incomplete checks must remain visible. Always clean up the workspace, including on cancellation. Publication must require separate confirmation and a fresh head check. Use only the relevant capabilities among the available tools.",
        _ => throw new ArgumentException("Unknown benchmark case.")
    };

    public static JsonObject Inputs(string name, string variant) => name switch
    {
        "nullable_defaults" => variant == "alternate" ? new() { ["increment"] = 5 } : new(),
        "conditional" => new() { ["enabled"] = variant != "alternate" },
        "collections" => new() { ["values"] = variant == "alternate" ? new JsonArray(3, 3, -2) : new JsonArray(1, 2, 4) },
        "review_french" or "review_distractors" => new() { ["pr_url"] = "https://github.com/AxaFrance/SmartGuide/pull/510", ["review_text"] = "Verify changed behavior and report every dependency, lint, unit and integration check with evidence." },
        _ => new()
    };

    public sealed class Environment(string name, string variant = "nominal")
    {
        public List<string> Effects { get; } = [];
        public Action<string>? AfterTool { get; set; }
        public void CancelDuringWork(CancellationTokenSource cancellation) => AfterTool = method =>
        {
            if (method is not ("write" or "run_check")) return;
            cancellation.Cancel();
            // Interrupt the mocked operation before returning its response. Merely setting
            // the token after the last completed step races with normal workflow completion.
            cancellation.Token.ThrowIfCancellationRequested();
        };
        public List<string> Violations { get; } = [];
        private readonly Dictionary<string, JsonObject> _checks = new(StringComparer.Ordinal);
        private bool _reviewed, _evaluated;
        private string Directory => "/synthetic/review/workspace" + (variant == "nominal" ? "" : "/" + variant);
        public InMemoryMcpClientFactory Factory()
        {
            var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
            void Add(string method, string description, string effect, string input, string output, JsonObject example, Func<JsonNode?, JsonObject> run)
            {
                server.Tools.Add(new() { Name = method, Description = description, EffectKind = effect, InputSchema = JsonNode.Parse(input), OutputSchema = JsonNode.Parse(output), ExampleResponse = example });
                if (method == "run_check") server.Tools[^1].Meta = JsonNode.Parse("""{"gnougo":{"result":{"detect_errors":false}}}""");
                server.ToolHandlers[method] = args => { lock (Effects) { Effects.Add(method); var result = run(args); AfterTool?.Invoke(method); return new() { Content = result }; } };
            }
            const string empty = """{"type":"object","properties":{},"additionalProperties":false}""";
            const string number = """{"type":"object","properties":{"value":{"type":"number"}},"required":["value"],"additionalProperties":false}""";
            Add("read", "Read a numeric value", "read", empty, number, new() { ["value"] = 21 }, _ => new() { ["value"] = variant == "alternate" ? 7 : 21 });
            Add("read_optional", "Read a nullable numeric value", "read", empty, """{"type":"object","properties":{"value":{"type":["number","null"]}},"required":["value"],"additionalProperties":false}""", new() { ["value"] = null }, _ => new() { ["value"] = variant == "alternate" ? 7 : null });
            Add("write", "Perform the requested protected write", "write", empty, number, new() { ["value"] = 21 }, _ => variant == "failure" ? throw new InvalidOperationException("Injected write failure") : new() { ["value"] = 21 });
            Add("cleanup", "Clean up after the protected write, including failures", "lifecycle", empty, empty, new(), _ => new());
            if (name.StartsWith("review_", StringComparison.Ordinal))
            {
                const string workspace = """{"type":"object","properties":{"directory":{"type":"string"}},"required":["directory"],"additionalProperties":false}""";
                Add("clone_repository", "Clone a pull request repository once into an isolated directory. Cloner un dépôt pour reviewer une pull request.", "execute", """{"type":"object","properties":{"pr_url":{"type":"string"}},"required":["pr_url"],"additionalProperties":false}""", workspace, new() { ["directory"] = Directory }, args => { if (args?["pr_url"]?.ToString() != Inputs(name, variant)["pr_url"]?.ToString()) Violations.Add("wrong_pull_request"); return new() { ["directory"] = Directory }; });
                const string check = """{"type":"object","properties":{"name":{"type":"string"},"status":{"type":"string","enum":["passed","failed","incomplete"]},"evidence":{"type":"string"}},"required":["name","status","evidence"],"additionalProperties":false}""";
                Add("run_check", "In the existing clone, install dependencies or run lint, unit or integration tests; records execution evidence without modifying tracked files.", "execute", """{"type":"object","properties":{"directory":{"type":"string"},"check":{"type":"string","enum":["dependencies","lint","unit","integration"]}},"required":["directory","check"],"additionalProperties":false}""", check, new() { ["name"] = "lint", ["status"] = "passed", ["evidence"] = "exit=0" }, args =>
                {
                    WorkingDirectory(args); var kind = args?["check"]?.ToString() ?? "";
                    var status = variant == "failure" && kind == "unit" ? "failed" : variant == "incomplete" && kind == "integration" ? "incomplete" : "passed";
                    var result = new JsonObject { ["name"] = kind, ["status"] = status, ["evidence"] = "observation:" + kind + ":" + status };
                    if (!_checks.TryAdd(kind, result)) Violations.Add("duplicate_check:" + kind);
                    return (JsonObject)result.DeepClone();
                });
                Add("review_changes", "Review changed code against every user review instruction in the existing clone. Does not run checks or publish.", "read", """{"type":"object","properties":{"directory":{"type":"string"},"review_text":{"type":"string"}},"required":["directory","review_text"],"additionalProperties":false}""", """{"type":"object","properties":{"complete":{"type":"boolean"},"findings":{"type":"array","items":{"type":"string"}}},"required":["complete","findings"],"additionalProperties":false}""", new() { ["complete"] = true, ["findings"] = new JsonArray() }, args => { WorkingDirectory(args); _reviewed = args?["review_text"]?.ToString() == Inputs(name, variant)["review_text"]?.ToString(); return new() { ["complete"] = _reviewed, ["findings"] = new JsonArray() }; });
                Add("evaluate_review", "Evaluate captured check evidence and the code review; create the exact review draft. Failed checks request changes, incomplete verification comments, complete passes approve with zero findings.", "none", workspace, """{"type":"object","properties":{"draftId":{"type":"string"},"event":{"type":"string","enum":["APPROVE","REQUEST_CHANGES","COMMENT"]}},"required":["draftId","event"],"additionalProperties":false}""", new() { ["draftId"] = "draft", ["event"] = "APPROVE" }, args => { WorkingDirectory(args); _evaluated = _reviewed && _checks.Count == 4; if (!_evaluated) Violations.Add("incomplete_evaluation"); return new() { ["draftId"] = "draft", ["event"] = Event() }; });
                Add("publish_review", "Publish only the stored evaluated draft, after separate runtime human confirmation and a fresh PR head check. These checks are owned by the publisher.", "write", """{"type":"object","properties":{"draftId":{"type":"string"}},"required":["draftId"],"additionalProperties":false}""", """{"type":"object","properties":{"published":{"type":"boolean"}},"required":["published"],"additionalProperties":false}""", new() { ["published"] = true }, args => { if (!_evaluated || args?["draftId"]?.ToString() != "draft") Violations.Add("unapproved_draft"); Effects.Add("confirmation");
                    if (variant == "rejected") throw new InvalidOperationException("Publication confirmation rejected");
                    Effects.Add("head_check");
                    if (variant == "head_changed") throw new InvalidOperationException("PR head changed before publication");
                    Effects.Add("event:" + Event()); return new() { ["published"] = true }; });
                Add("remove_workspace", "Remove the single review clone in cleanup, including after failure or cancellation.", "lifecycle", workspace, empty, new(), args => { WorkingDirectory(args); return new(); });
            }
            if (name == "review_distractors")
                for (var i = 0; i < 80; i++) Add("unrelated_" + i, "Unrelated inventory statistics operation " + i, "read", empty, number, new() { ["value"] = 0 }, _ => { Violations.Add("irrelevant_capability"); return new() { ["value"] = 0 }; });
            factory.RegisterServer("benchmark", server); return factory;
        }
        private void WorkingDirectory(JsonNode? args) { if (args?["directory"]?.ToString() != Directory) Violations.Add("wrong_directory"); }
        private string Event() => _checks.Values.Any(c => c["status"]?.ToString() == "failed") ? "REQUEST_CHANGES" : !_evaluated || _checks.Values.Any(c => c["status"]?.ToString() == "incomplete") ? "COMMENT" : "APPROVE";
        public bool Verify(RunResult result)
        {
            if (name == "protected_cleanup") return (variant == "failure" ? !result.Success : result.Success && result.Outputs?["result"]?.ToString() == "42") && Effects.SequenceEqual(new[] { "write", "cleanup" });
            if (name.StartsWith("review_", StringComparison.Ordinal) && variant is "rejected" or "head_changed")
                return !result.Success && Violations.Count == 0 && Effects.Count(e => e == "clone_repository") == 1 && _checks.Count == 4 && Effects.Last() == "remove_workspace"
                    && Effects.Count(e => e == "confirmation") == 1 && !Effects.Any(e => e.StartsWith("event:", StringComparison.Ordinal)) && (variant != "head_changed" || Effects.Contains("head_check"));
            if (!result.Success || Violations.Count > 0) return false;
            if (name.StartsWith("review_", StringComparison.Ordinal))
                return Effects.Count(e => e == "clone_repository") == 1 && Effects.Count(e => e == "run_check") == 4 && _checks.Keys.Order().SequenceEqual(new[] { "dependencies", "integration", "lint", "unit" }) &&
                    Effects.Count(e => e == "publish_review") == 1 && Effects.Last() == "remove_workspace" && Effects.Contains("event:" + (variant == "failure" ? "REQUEST_CHANGES" : variant == "incomplete" ? "COMMENT" : "APPROVE"));
            var expected = name switch { "local" => "42", "read_transform" => variant == "alternate" ? "14" : "42", "nullable_defaults" => variant == "alternate" ? "12" : "2", "conditional" => variant == "alternate" ? "0" : "21", "collections" => variant == "alternate" ? "[6,6,-4]" : "[2,4,8]", _ => "" };
            var methods = name switch { "read_transform" => new[] { "read" }, "nullable_defaults" => ["read_optional"], "conditional" when variant != "alternate" => ["read"], _ => [] };
            return result.Outputs?["result"]?.ToJsonString() == expected && Effects.SequenceEqual(methods);
        }
    }
}
