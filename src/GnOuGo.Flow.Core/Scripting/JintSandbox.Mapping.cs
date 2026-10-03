using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Interop;

namespace GnOuGo.Flow.Core.Scripting;

public sealed partial class JintSandbox
{
    public const int MappingProfileVersion = 1;
    public const string MappingFunction = "checkedMapping";
    private static readonly HashSet<string> MappingHelpers = ["select", "optional", "parse", "text", "texts", "trim", "decode", "number", "has", "test", "scalar"];

    /// <summary>Only source-backed scalar identities may leave this sandbox. JS never holds their numeric approximations.</summary>
    public JsonNode? ExecuteMapping(string expression, JsonNode? source, CancellationToken ct = default, JsonObject? target = null)
    {
        ValidateMapping(expression);
        ct.ThrowIfCancellationRequested();
        var engine = new Engine(options => options.MaxStatements(_maxStatements).TimeoutInterval(_timeout)
            .LimitMemory(_memoryLimit).CancellationToken(ct).Strict());
        var origins = new Dictionary<ObjectInstance, JsonNode?>();
        var containers = new HashSet<ObjectInstance>();
        var absent = new HashSet<ObjectInstance>();
        long importedBytes = 0;
        var helpers = new JsObject(engine);
        foreach (var name in MappingHelpers)
        {
            var method = name;
            helpers.Set(name, new ClrFunction(engine, name, (_, args) => Invoke(method, args)));
        }
        engine.SetValue("source", Import(source));
        engine.SetValue("m", helpers);
        try { return Export(engine.Evaluate(expression), 0, target); }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowRuntimeException and not OutOfMemoryException)
        { ct.ThrowIfCancellationRequested(); throw Unsatisfied("The mapping could not be executed within its restricted profile.", ex); }

        JsValue Import(JsonNode? value, int depth = 0)
        {
            ct.ThrowIfCancellationRequested();
            importedBytes += 128 + (value is JsonValue text && text.TryGetValue<string>(out var content) ? (long)content.Length * 4 : 0);
            if (depth > 64 || importedBytes > _memoryLimit) throw Unsatisfied("Observed data exceeds the mapping sandbox's nesting or memory allowance.");
            if (value is JsonArray array) { var imported = Array(array.Select(v => Import(v, depth + 1))); containers.Add(imported.AsObject()); return imported; }
            var result = new JsObject(engine);
            if (value is JsonObject obj)
            { containers.Add(result); foreach (var field in obj) result.CreateDataProperty(field.Key, Import(field.Value, depth + 1)); }
            else origins.Add(result, value?.DeepClone());
            return result;
        }
        JsValue Array(IEnumerable<JsValue> values)
        {
            var array = engine.Intrinsics.Array.Construct(System.Array.Empty<JsValue>());
            foreach (var value in values) { ct.ThrowIfCancellationRequested(); array.Push(value); }
            return array;
        }
        JsonNode? Export(JsValue value, int depth, JsonObject? contract)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 64) throw Unsatisfied("Mapping result nesting exceeded its limit.");
            if (value.IsObject() && absent.Contains(value.AsObject()))
            {
                if (contract is null || !contract.TryGetPropertyValue("default", out var fallback) ||
                    JsonSchemaContractValidator.ValidateInstance(fallback, contract).Count != 0)
                    throw Unsatisfied("An absent observation has no valid authoritative default.");
                return fallback?.DeepClone();
            }
            if (value.IsObject() && origins.TryGetValue(value.AsObject(), out var original)) return original?.DeepClone();
            if (value.IsArray())
            {
                var array = new JsonArray();
                foreach (var item in value.AsArray()) array.Add(Export(item, depth + 1, contract?["items"] as JsonObject));
                return array;
            }
            if (value.IsObject())
            {
                var obj = new JsonObject();
                foreach (var field in value.AsObject().GetOwnProperties())
                    if (!field.Value.Value.IsUndefined()) obj.Add(field.Key.ToString(), Export(field.Value.Value, depth + 1, contract?["properties"]?[field.Key.ToString()] as JsonObject));
                return obj;
            }
            throw Unsatisfied("A mapping result contains a missing or invented scalar. Return observed data; defaults belong to the target contract.");
        }
        JsonNode? Observed(JsValue value) => value.IsObject() && origins.TryGetValue(value.AsObject(), out var data)
            ? data : throw Unsatisfied("Extraction helpers require an observed scalar, not a literal or computed replacement.");
        string Text(JsValue value) => Observed(value) is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text : throw Unsatisfied("Text extraction requires an observed string.");
        bool Own(JsValue value, string key, out JsValue found)
        {
            if (value.IsObject())
                foreach (var property in value.AsObject().GetOwnProperties())
                    if (property.Key.ToString() == key) { found = property.Value.Value; return true; }
            found = JsValue.Undefined; return false;
        }
        JsValue Select(JsValue value, JsValue paths, bool optional = false)
        {
            if (!paths.IsArray()) throw Unsatisfied("Selection paths must be arrays.");
            foreach (var path in paths.AsArray())
            {
                if (!path.IsArray()) throw Unsatisfied("Selection paths must contain property arrays.");
                var current = value; var present = true;
                foreach (var part in path.AsArray())
                {
                    if (!part.IsString()) throw Unsatisfied("Selection paths require literal property names.");
                    if (optional && (!current.IsObject() || !containers.Contains(current.AsObject())))
                        throw Unsatisfied("A default cannot replace an invalid or null observation.");
                    if (!Own(current, part.AsString(), out current)) { present = false; break; }
                }
                if (present) return current;
            }
            if (!optional) throw Unsatisfied("No declared selection path was present.");
            var missing = new JsObject(engine); absent.Add(missing); return missing;
        }
        Regex Pattern(JsValue value)
        {
            if (!value.IsString() || value.AsString().Length > 2048) throw Unsatisfied("Extraction needs a bounded literal pattern.");
            return new Regex(value.AsString(), RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(Math.Min(1000, _timeout.TotalMilliseconds)));
        }
        JsValue Invoke(string name, JsValue[] args)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Length == 0) throw Unsatisfied("Mapping helper arguments are missing.");
            switch (name)
            {
                case "optional":
                    if (args.Length != 2) throw Unsatisfied("optional requires an observed container and literal path.");
                    if (!args[0].IsObject() || !containers.Contains(args[0].AsObject())) throw Unsatisfied("optional requires an observed container, not a constructed or null observation.");
                    return Select(args[0], Array([args[1]]), optional: true);
                case "select":
                    if (args.Length != 3 || !args[2].IsBoolean()) throw Unsatisfied("select requires source, paths and a per-item boolean.");
                    if (!args[2].AsBoolean()) return Select(args[0], args[1]);
                    if (!args[0].IsArray()) throw Unsatisfied("Per-item selection requires an observed array.");
                    return Array(args[0].AsArray().Select(v => Select(v, args[1])));
                case "parse":
                    if (args.Length != 1) throw Unsatisfied("parse requires one observed JSON string.");
                    return Import(JsonNode.Parse(Text(args[0])));
                case "trim": return Import(JsonValue.Create(Text(args[0]).Trim()));
                case "decode": return Import(JsonValue.Create(WebUtility.HtmlDecode(Text(args[0]))));
                case "number":
                    var number = JsonNode.Parse(Text(args[0]).Trim());
                    if (number?.GetValueKind() != System.Text.Json.JsonValueKind.Number)
                        throw Unsatisfied("The observed text is not a JSON number.");
                    return Import(number);
                case "scalar": return args[0].IsObject() && origins.ContainsKey(args[0].AsObject()) ? JsBoolean.True : JsBoolean.False;
                case "has":
                    if (args.Length != 2 || !args[1].IsString()) throw Unsatisfied("has requires an object and literal property name.");
                    return Own(args[0], args[1].AsString(), out _) ? JsBoolean.True : JsBoolean.False;
                case "test":
                    if (args.Length != 2) throw Unsatisfied("test requires observed text and a pattern.");
                    return Pattern(args[1]).IsMatch(Text(args[0])) ? JsBoolean.True : JsBoolean.False;
                case "text": case "texts":
                    if (args.Length is < 2 or > 3) throw Unsatisfied("Text extraction requires observed text, pattern and optional group index.");
                    var group = args.Length == 3 && args[2].IsNumber() ? args[2].AsNumber() : 1;
                    if (group < 0 || group > 100 || group != Math.Truncate(group)) throw Unsatisfied("Capture group must be a bounded integer.");
                    var matches = Pattern(args[1]).Matches(Text(args[0]));
                    JsValue Capture(Match match) => group < match.Groups.Count && match.Groups[(int)group].Success
                        ? Import(JsonValue.Create(match.Groups[(int)group].Value)) : throw Unsatisfied("The required capture group was absent.");
                    if (name == "text") return matches.Count == 0 ? JsValue.Undefined : Capture(matches[0]);
                    if (matches.Count > 10000) throw Unsatisfied("Extraction exceeded the collection limit.");
                    return Array(matches.Select(Capture));
                default: throw Unsatisfied("Unsupported extraction helper.");
            }
        }
    }

    public static void ValidateMapping(string expression)
    {
        if (expression.Length > 65536) throw Unsatisfied("Mapping script exceeds its size limit.");
        try { Check(new Acornima.Parser().ParseExpression(expression), new(StringComparer.Ordinal) { "source", "m", "Object", "Array" }, 0); }
        catch (Acornima.ParseErrorException ex) { throw Unsatisfied("Mapping JavaScript is invalid.", ex); }
        static void Check(Node node, HashSet<string> bound, int depth)
        {
            if (depth > 64) throw Unsatisfied("Mapping script nesting exceeded its limit.");
            switch (node)
            {
                case Identifier id when bound.Contains(id.Name): return;
                case RegExpLiteral:
                    throw Unsatisfied("Extraction patterns must be quoted JavaScript strings, not /regex/ literals. Pass a pattern string to m.text, m.texts or m.test.");
                case Literal literal when literal.Value is null or string or bool or double: return;
                case ObjectExpression obj:
                    foreach (var property in obj.Properties)
                    {
                        if (property is not Property { Computed: false, Method: false } p || p.Kind != PropertyKind.Init)
                            throw Unsatisfied("Mapping objects require ordinary named data properties.");
                        if ((p.Key is Identifier propertyName ? propertyName.Name : (p.Key as Literal)?.Value?.ToString()) is "__proto__" or "constructor" or "prototype")
                            throw Unsatisfied("Prototype properties are forbidden.");
                        Check(p.Value, bound, depth + 1);
                    }
                    return;
                case ArrayExpression array:
                    foreach (var item in array.Elements) if (item is not null) Check(item, bound, depth + 1); else throw Unsatisfied("Sparse arrays are unsupported.");
                    return;
                case MemberExpression member:
                    if (member.Computed && member.Property is not Literal) throw Unsatisfied("Computed property names are unsupported.");
                    var key = member.Property is Identifier name ? name.Name : (member.Property as Literal)?.Value?.ToString();
                    if (key is "__proto__" or "prototype" or "constructor") throw Unsatisfied("Prototype access is forbidden.");
                    Check(member.Object, bound, depth + 1); return;
                case ArrowFunctionExpression { Async: false } arrow when arrow.Body is not BlockStatement:
                    var scope = new HashSet<string>(bound, StringComparer.Ordinal);
                    foreach (var parameter in arrow.Params)
                        if (parameter is Identifier variable && variable.Name is not ("source" or "m")) scope.Add(variable.Name);
                        else throw Unsatisfied("Mapping callbacks require plain local parameters.");
                    Check(arrow.Body, scope, depth + 1); return;
                case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier method } call } invocation:
                    var permitted = call.Object is Identifier { Name: "m" } ? MappingHelpers.Contains(method.Name) :
                        call.Object is Identifier { Name: "Object" } ? method.Name is "entries" or "fromEntries" :
                        call.Object is Identifier { Name: "Array" } ? method.Name == "isArray" : method.Name is "map" or "filter" or "slice" or "flatMap";
                    if (!permitted)
                        throw Unsatisfied("Mapping calls are limited to data extraction helpers and bounded array operations.");
                    Check(call, bound, depth + 1);
                    foreach (var argument in invocation.Arguments) Check(argument, bound, depth + 1);
                    return;
                case CallExpression { Callee: ArrowFunctionExpression lambda } callLambda:
                    Check(lambda, bound, depth + 1);
                    foreach (var argument in callLambda.Arguments) Check(argument, bound, depth + 1);
                    return;
                case BinaryExpression binary when binary.Operator is Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality or Acornima.Operator.LogicalOr or Acornima.Operator.LogicalAnd:
                    Check(binary.Left, bound, depth + 1); Check(binary.Right, bound, depth + 1); return;
                case UnaryExpression { Operator: Acornima.Operator.TypeOf } type: Check(type.Argument, bound, depth + 1); return;
                case ConditionalExpression conditional:
                    Check(conditional.Test, bound, depth + 1); Check(conditional.Consequent, bound, depth + 1); Check(conditional.Alternate, bound, depth + 1); return;
                case UnaryExpression { Operator: Acornima.Operator.LogicalNot } unary: Check(unary.Argument, bound, depth + 1); return;
                default: throw Unsatisfied("Mapping JavaScript contains an unsupported operation.");
            }
        }
    }

    internal static WorkflowRuntimeException Unsatisfied(string message, Exception? inner = null) => new("CONTRACT_UNSATISFIED", message, inner: inner);
}
