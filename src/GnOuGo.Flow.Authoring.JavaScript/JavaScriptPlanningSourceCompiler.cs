using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Acornima.Ast;
using GnOuGo.Flow.Core.Planning;
using Jint;

namespace GnOuGo.Flow.Authoring.JavaScript;

/// <summary>Evaluates a pure authoring SDK; no runtime, provider, or CLR objects enter the engine.</summary>
public sealed class JavaScriptPlanningSourceCompiler : IPlanningSourceCompiler
{
    public string Format => PlanningConstructionStrategies.JavaScriptV1;
    public string Version => "1";
    public const int MaximumSourceBytes = 262_144;
    public const int MaximumGraphBytes = 1_048_576;

    public string Describe(PlanningSourceContext context)
    {
        var docs = new StringBuilder(JavaScriptSdk.Documentation);
        foreach (var capability in context.Preparation.Capabilities)
        {
            // Schema documentation is data, not executable JavaScript. JSON quoting prevents
            // descriptions or catalog identifiers becoming SDK declarations.
            docs.Append("\nCapability ").Append(JsonSerializer.Serialize(capability.Id, PlanningJsonContext.Default.String));
            docs.Append(" input type: ").Append(JsDocType(capability.InputSchema));
            docs.Append("; output type: ").Append(JsDocType(capability.OutputSchema));
        }
        return docs.ToString();
    }

    public PlanningSourceResult Compile(string source, PlanningSourceContext context, CancellationToken ct)
    {
        var locations = new Dictionary<string, PlanningSourceLocation>(StringComparer.Ordinal);
        PlanningSourceResult Failure(string code, string message, string location = "/source") => new(null, [new(code, location, message)], locations);
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
            return Failure("JS_SOURCE_LIMIT", "Authoring source exceeds 256 KiB.");
        try
        {
            var script = new Acornima.Parser().ParseScript(source);
            foreach (var node in Walk(script))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (node is ImportExpression or AwaitExpression or YieldExpression ||
                    node is Identifier { Name: "eval" or "Function" or "AsyncFunction" or "GeneratorFunction" or "Date" })
                    return Failure("JS_AUTHORING_UNSUPPORTED", "Authoring must be synchronous and deterministic; imports, clocks and dynamic compilation are unavailable.",
                        $"/source/{node.Location.Start.Line}:{node.Location.Start.Column}");
                if (node is CallExpression { Callee: MemberExpression { Object: Identifier { Name: "flow" }, Property: Identifier property }, Arguments.Count: > 0 } call &&
                    property.Name is "step" or "call" or "when" or "loop" or "parallel" or "choose" && call.Arguments[0] is StringLiteral key)
                    locations.TryAdd(key.Value, new(node.Location.Start.Line, node.Location.Start.Column));
            }
            using var engine = new Engine(options => options
                .MaxStatements(10_000).TimeoutInterval(TimeSpan.FromSeconds(5)).LimitMemory(50_000_000)
                .LimitRecursion(64).CancellationToken(deadline.Token).DisableStringCompilation());
            engine.Execute(JavaScriptSdk.Runtime);
            // JSON only: SetValue never receives a reflected CLR object or delegate.
            var template = JsonSerializer.Serialize(context.Template, PlanningJsonContext.Default.PlanningWorkflow);
            engine.Execute("flow.initialize(" + template + ");");
            var result = engine.Evaluate("'use strict';\n" + source, "workflow.authoring.js");
            if (!result.IsObject()) return Failure("JS_WORKFLOW_REQUIRED", "The final expression must be flow.workflow({...}).");
            engine.SetValue("__result", result);
            var json = engine.Evaluate("JSON.stringify(__result)").AsString();
            if (Encoding.UTF8.GetByteCount(json) > MaximumGraphBytes)
                return Failure("JS_GRAPH_LIMIT", "The emitted workflow exceeds 1 MiB.");
            var workflow = JsonSerializer.Deserialize(json, AuthoringJsonContext.Default.PlanningWorkflow);
            if (workflow is null || workflow.Key != context.Template.Key)
                return Failure("JS_WORKFLOW_IDENTITY", "The candidate must preserve its assigned workflow key.");
            return new(workflow, [], locations);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Failure("JS_AUTHORING_TIMEOUT", "Authoring exceeded its five-second deadline."); }
        catch (OperationCanceledException) { throw; }
        catch (Acornima.ParseErrorException ex)
        {
            return Failure("JS_SYNTAX", ex.Description, $"/source/{ex.LineNumber}:{ex.Column}");
        }
        catch (Exception ex) when (ex is JintException or JsonException or InvalidOperationException or ArgumentException)
        {
            ct.ThrowIfCancellationRequested();
            return Failure("JS_AUTHORING_FAILED", ex.Message);
        }
    }

    private static IEnumerable<Node> Walk(Node node)
    {
        var pending = new Stack<Node>(); pending.Push(node);
        while (pending.TryPop(out var current))
        {
            yield return current;
            foreach (var child in current.ChildNodes.Reverse()) pending.Push(child);
        }
    }

    private static string JsDocType(JsonNode? schema, int depth = 0)
    {
        if (depth > 8 || schema is not JsonObject obj) return "unknown";
        return obj["type"]?.ToString() switch
        {
            "string" => "string", "number" or "integer" => "number", "boolean" => "boolean", "null" => "null",
            "array" => "Array<" + JsDocType(obj["items"], depth + 1) + ">",
            "object" when obj["properties"] is JsonObject properties => "{" + string.Join(", ", properties.Select(p =>
                JsonSerializer.Serialize(p.Key, PlanningJsonContext.Default.String) + ": " + JsDocType(p.Value, depth + 1))) + "}",
            "object" => "Object<string, unknown>", _ => "unknown"
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PlanningWorkflow))]
internal partial class AuthoringJsonContext : JsonSerializerContext;
