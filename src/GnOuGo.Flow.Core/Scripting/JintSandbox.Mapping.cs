using System.Net;
using System.Diagnostics;
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
    public const int MappingProfileVersion = 4;
    public const string MappingFunction = "checkedMapping";
    private static readonly HashSet<string> MappingHelpers = ["select", "optional", "parse", "text", "texts", "trim", "decode", "percentDecode", "resolveUri", "number", "has", "test", "scalar"];

    /// <summary>Only source-backed scalar identities may leave this sandbox. JS never holds their numeric approximations.</summary>
    public JsonNode? ExecuteMapping(string expression, JsonNode? source, CancellationToken ct = default, JsonObject? target = null)
        => ExecuteMappingCore(expression, source, ct, target, null);

    /// <summary>One engine evaluation and one constraint window for all independent items.</summary>
    public JsonArray ExecuteMappingItems(string expression, JsonObject sources, string input, JsonObject itemTarget, CancellationToken ct = default)
        => (JsonArray)ExecuteMappingCore(expression, sources, ct, itemTarget, input)!;

    // This state belongs to one logical mapping, including cached candidates and specializations.
    // Jint resets constraints at every Evaluate; this constraint deliberately retains cumulative work.
    internal sealed class MappingAllowance(int statements, TimeSpan timeout, long memory) : Constraint
    {
        private int _statements;
        private readonly Stopwatch _elapsed = new();
        private long _allocationStart;
        private long _allocated;
        internal long ImportedBytes;
        internal void Start() { _allocationStart = GC.GetAllocatedBytesForCurrentThread(); _elapsed.Start(); }
        internal void Stop() { _elapsed.Stop(); _allocated += GC.GetAllocatedBytesForCurrentThread() - _allocationStart; }
        public override void Reset() { }
        public override void Check()
        {
            if (++_statements > statements || _elapsed.Elapsed > timeout ||
                _allocated + GC.GetAllocatedBytesForCurrentThread() - _allocationStart > memory || ImportedBytes > memory)
                throw ResourceLimit();
        }
    }

    internal MappingAllowance CreateMappingAllowance() => new(_maxStatements, _timeout, _memoryLimit);

    internal void ExecuteMappingItems(string expression, JsonObject sources, string input, JsonObject itemTarget,
        IReadOnlyList<int> indices, MappingAllowance allowance, Action<int, JsonNode?> success,
        Action<int, WorkflowRuntimeException> failure, CancellationToken ct)
        => ExecuteMappingCore(expression, sources, ct, itemTarget, input, indices, allowance, success, failure);

    private static WorkflowRuntimeException ResourceLimit() => new("CONTRACT_UNSATISFIED",
        "The mapping exhausted its cumulative sandbox allowance.", details: new JsonObject { ["mapping_resource_limit"] = true });

    private JsonNode? ExecuteMappingCore(string expression, JsonNode? source, CancellationToken ct, JsonObject? target, string? collectionInput,
        IReadOnlyList<int>? indices = null, MappingAllowance? allowance = null,
        Action<int, JsonNode?>? success = null, Action<int, WorkflowRuntimeException>? failure = null)
    {
        ValidateMapping(expression, learned: false);
        ct.ThrowIfCancellationRequested();
        var engine = new Engine(options =>
        {
            options.MaxStatements(_maxStatements).TimeoutInterval(_timeout).LimitMemory(_memoryLimit).CancellationToken(ct).Strict();
            if (allowance is not null) options.Constraint(allowance);
        });
        var origins = new Dictionary<ObjectInstance, JsonNode?>();
        var containers = new HashSet<ObjectInstance>();
        var absent = new HashSet<ObjectInstance>();
        var patterns = new Dictionary<string, Regex>(StringComparer.Ordinal);
        long importedBytes = allowance?.ImportedBytes ?? 0;
        var helpers = new JsObject(engine);
        foreach (var name in MappingHelpers)
        {
            var method = name;
            helpers.Set(name, new ClrFunction(engine, name, (_, args) => Invoke(method, args)));
        }
        var items = collectionInput is null ? null : (source as JsonObject)?[collectionInput] as JsonArray
            ?? (collectionInput is null ? null : throw Unsatisfied("Independent extraction requires an observed array."));
        var results = new JsonArray();
        var itemIndex = -1;
        engine.SetValue("source", items is null ? Import(source) : JsValue.Undefined);
        engine.SetValue("m", helpers);
        if (items is not null)
        {
            // These host functions are inaccessible to learned expressions: their
            // identifiers are not admitted by ValidateMapping. Engine evaluation
            // is entered once, so time, statements and allocation limits never reset.
            engine.SetValue("__mappingLoad", new ClrFunction(engine, "__mappingLoad", (_, args) =>
            {
                return Load((int)args[0].AsNumber());
            }));
            engine.SetValue("__mappingSave", new ClrFunction(engine, "__mappingSave", (_, args) =>
            {
                var value = Export(args[0], 0, target);
                var errors = JsonSchemaContractValidator.ValidateInstance(value, target!);
                if (errors.Count > 0) throw Unsatisfied("The mapped item does not satisfy its target: " + string.Join("; ", errors));
                importedBytes += value is null ? 4 : System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString());
                if (importedBytes > _memoryLimit) throw Unsatisfied("The assembled mapping result exceeds its memory allowance.");
                results.Add(value); return JsValue.Undefined;
            }));
        }
        JsValue Load(int index)
        {
            itemIndex = index;
            origins.Clear(); containers.Clear(); absent.Clear();
            var current = new JsObject(engine); containers.Add(current);
            var context = new JsObject(engine); containers.Add(context);
            foreach (var field in source!.AsObject())
            {
                var value = Import(field.Key == collectionInput ? items![itemIndex] : field.Value);
                current.CreateDataProperty(field.Key, value);
                if (field.Key == collectionInput) engine.SetValue("item", value);
                else context.CreateDataProperty(field.Key, value);
            }
            engine.SetValue("context", context);
            return current;
        }
        try
        {
            if (indices is not null)
            {
                allowance!.Start();
                try
                {
                    foreach (var index in indices)
                    {
                        ct.ThrowIfCancellationRequested();
                        allowance.Check();
                        try
                        {
                            engine.SetValue("source", Load(index));
                            var value = Export(engine.Evaluate(expression), 0, target);
                            var findings = JsonSchemaContractValidator.ValidateInstance(value, target!);
                            if (findings.Count != 0) throw Unsatisfied("The mapped item does not satisfy its target: " + findings[0]);
                            importedBytes += value is null ? 4 : System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString());
                            if (importedBytes > _memoryLimit) throw ResourceLimit();
                            success!(index, value);
                        }
                        catch (WorkflowRuntimeException ex) when (ex.Code == "CONTRACT_UNSATISFIED" && ex.Details?["mapping_resource_limit"]?.GetValue<bool>() != true)
                        { failure!(index, ex); }
                        catch (Jint.Runtime.JavaScriptException ex)
                        { failure!(index, Unsatisfied("The mapping could not select the required observed value.", ex)); }
                        catch (System.Text.Json.JsonException ex)
                        { failure!(index, Unsatisfied("The observed value is not valid JSON.", ex)); }
                        allowance.ImportedBytes = importedBytes;
                        allowance.Check();
                    }
                    return null;
                }
                finally { allowance.ImportedBytes = importedBytes; allowance.Stop(); }
            }
            if (items is null) return Export(engine.Evaluate(expression), 0, target);
            engine.Evaluate("(()=>{for(let i=0;i<" + items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ";i++){source=__mappingLoad(i);__mappingSave(" + expression + ");}})()");
            return results;
        }
        catch (WorkflowRuntimeException ex) when (itemIndex >= 0)
        { throw new WorkflowRuntimeException(ex.Code, ex.Message, inner: ex, details: WithIndex(ex.Details, itemIndex)); }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowRuntimeException and not OutOfMemoryException)
        { ct.ThrowIfCancellationRequested(); throw new WorkflowRuntimeException("CONTRACT_UNSATISFIED", "The mapping could not be executed within its restricted profile.", inner: ex,
            details: allowance is null ? (itemIndex < 0 ? null : new JsonObject { ["source_index"] = itemIndex }) : new JsonObject { ["mapping_resource_limit"] = true, ["source_index"] = itemIndex }); }

        JsValue Import(JsonNode? value, int depth = 0)
        {
            ct.ThrowIfCancellationRequested();
            importedBytes += 128 + (value is JsonValue text && text.TryGetValue<string>(out var content) ? (long)content.Length * 4 : 0);
            if (allowance is not null && (depth > 64 || importedBytes > _memoryLimit)) throw ResourceLimit();
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
        JsonNode? Export(JsValue value, int depth, JsonObject? contract, string path = "$")
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 64) throw Unsatisfied("Mapping result nesting exceeded its limit.");
            if (value.IsObject() && absent.Contains(value.AsObject()))
            {
                if (contract is null || !contract.TryGetPropertyValue("default", out var fallback) ||
                    JsonSchemaContractValidator.ValidateInstance(fallback, contract).Count != 0)
                    throw Unsatisfied("Mapping result " + path + " has no observation or valid authoritative default.");
                return fallback?.DeepClone();
            }
            if (value.IsObject() && origins.TryGetValue(value.AsObject(), out var original)) return original?.DeepClone();
            if (value.IsArray())
            {
                var array = new JsonArray();
                foreach (var item in value.AsArray()) array.Add(Export(item, depth + 1, contract?["items"] as JsonObject, path + "/" + array.Count));
                return array;
            }
            if (value.IsObject())
            {
                var obj = new JsonObject();
                foreach (var field in value.AsObject().GetOwnProperties())
                    if (!field.Value.Value.IsUndefined()) obj.Add(field.Key.ToString(), Export(field.Value.Value, depth + 1, contract?["properties"]?[field.Key.ToString()] as JsonObject,
                        path + "/" + field.Key.ToString().Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
                return obj;
            }
            throw Unsatisfied("Mapping result " + path + " contains a missing or invented scalar. Return observed data; defaults belong to the target contract.");
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
            var text = value.AsString();
            if (!patterns.TryGetValue(text, out var pattern))
            {
                // Reuse compilation within this evaluation; repeated items still share every sandbox limit.
                try
                {
                    pattern = new Regex(text, RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking,
                        TimeSpan.FromMilliseconds(Math.Min(1000, _timeout.TotalMilliseconds)));
                }
                catch (Exception ex) when (allowance is not null && ex is ArgumentException or NotSupportedException)
                { throw Unsatisfied("The mapping program contains an unsupported extraction pattern.", ex); }
                patterns.Add(text, pattern);
            }
            return pattern;
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
                case "percentDecode":
                    if (args.Length != 1) throw Unsatisfied("percentDecode requires one observed string.");
                    var encoded = Text(args[0]);
                    for (var i = 0; i < encoded.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (encoded[i] != '%') continue;
                        if (i + 2 >= encoded.Length || !char.IsAsciiHexDigit(encoded[i + 1]) || !char.IsAsciiHexDigit(encoded[i + 2]))
                            throw Unsatisfied("The observed string contains an invalid percent escape.");
                        i += 2;
                    }
                    return Import(JsonValue.Create(Uri.UnescapeDataString(encoded)));
                case "resolveUri":
                    if (args.Length != 2 || !Uri.TryCreate(Text(args[1]), UriKind.Absolute, out var baseUri) ||
                        !Uri.TryCreate(baseUri, Text(args[0]), out var resolved))
                        throw Unsatisfied("resolveUri requires an observed reference and an observed absolute base URI.");
                    return Import(JsonValue.Create(resolved.AbsoluteUri));
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
                    var pattern = Pattern(args[1]); var observed = Text(args[0]);
                    JsValue Capture(Match match) => group < match.Groups.Count && match.Groups[(int)group].Success
                        ? Import(JsonValue.Create(match.Groups[(int)group].Value)) : throw Unsatisfied("The required capture group was absent.");
                    if (name == "text") { var match = pattern.Match(observed); return match.Success ? Capture(match) : JsValue.Undefined; }
                    var captures = new List<JsValue>();
                    foreach (Match match in pattern.Matches(observed))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (captures.Count >= 10000) throw Unsatisfied("Extraction exceeded the collection limit.");
                        captures.Add(Capture(match));
                    }
                    return Array(captures);
                default: throw Unsatisfied("Unsupported extraction helper.");
            }
        }
    }

    private static JsonObject WithIndex(JsonNode? details, int index)
    {
        var result = details?.DeepClone().AsObject() ?? new JsonObject(); result["source_index"] = index; return result;
    }

    public static void ValidateMapping(string expression) => ValidateMapping(expression, learned: true);

    public static void ValidateMapping(string expression, bool learned)
    {
        if (expression.Length > 65536) throw Unsatisfied("Mapping script exceeds its size limit.");
        try { Check(new Acornima.Parser().ParseExpression(expression), new(StringComparer.Ordinal) { "source", "item", "context", "m", "Object", "Array" }, 0); }
        catch (Acornima.ParseErrorException ex) { throw Unsatisfied("Mapping JavaScript is invalid.", ex); }
        static bool Control(Node node, int depth = 0) => depth < 64 && (node is UnaryExpression { Operator: Acornima.Operator.LogicalNot } negate && Control(negate.Argument, depth + 1) ||
            node is Literal or UnaryExpression { Operator: Acornima.Operator.TypeOf } or BinaryExpression ||
            node is CallExpression { Callee: MemberExpression { Object: Identifier { Name: "m" }, Property: Identifier { Name: "has" or "test" or "scalar" } } } ||
            node is CallExpression { Callee: MemberExpression { Object: Identifier { Name: "Array" }, Property: Identifier { Name: "isArray" } } });

        void Check(Node node, HashSet<string> bound, int depth)
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
                    if (learned && method.Name == "filter" && invocation.Arguments.FirstOrDefault() is ArrowFunctionExpression filter && !Control(filter.Body))
                        throw Unsatisfied("Array filters require explicit observation predicates, such as m.test(value, patternString).");
                    Check(call, bound, depth + 1);
                    foreach (var argument in invocation.Arguments) Check(argument, bound, depth + 1);
                    return;
                case CallExpression { Callee: ArrowFunctionExpression lambda } callLambda:
                    Check(lambda, bound, depth + 1);
                    foreach (var argument in callLambda.Arguments) Check(argument, bound, depth + 1);
                    return;
                case BinaryExpression binary when binary.Operator is Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality or Acornima.Operator.LogicalOr or Acornima.Operator.LogicalAnd:
                    if (learned && (!Control(binary.Left) || !Control(binary.Right)))
                        throw Unsatisfied("Observed scalar tokens cannot be compared or used as booleans. Use m.test for observed text, m.has for presence, or Array.isArray for containers.");
                    Check(binary.Left, bound, depth + 1); Check(binary.Right, bound, depth + 1); return;
                case UnaryExpression { Operator: Acornima.Operator.TypeOf } type: Check(type.Argument, bound, depth + 1); return;
                case ConditionalExpression conditional:
                    if (learned && !Control(conditional.Test)) throw Unsatisfied("Use an explicit observation predicate for a conditional mapping.");
                    Check(conditional.Test, bound, depth + 1); Check(conditional.Consequent, bound, depth + 1); Check(conditional.Alternate, bound, depth + 1); return;
                case UnaryExpression { Operator: Acornima.Operator.LogicalNot } unary: Check(unary.Argument, bound, depth + 1); return;
                default: throw Unsatisfied("Mapping JavaScript contains an unsupported operation.");
            }
        }
    }

    internal static WorkflowRuntimeException Unsatisfied(string message, Exception? inner = null) => new("CONTRACT_UNSATISFIED", message, inner: inner);
}
