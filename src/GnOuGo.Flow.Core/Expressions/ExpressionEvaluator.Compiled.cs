using System.Text.Json.Nodes;
using Acornima.Ast;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Expressions;

public sealed partial class ExpressionEvaluator
{
    internal static string ContractExpression(JsonNode? value)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Expression contracts must identify one complete input expression.");
        var segments = ExpressionSegments.Read(text);
        if (segments.Count != 1 || !string.IsNullOrWhiteSpace(text[..segments[0].Start]) ||
            !string.IsNullOrWhiteSpace(text[(segments[0].Start + segments[0].Length)..]))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Expression contracts must identify one complete input expression.");
        return segments[0].Expression;
    }

    internal static void ValidateExpressionContracts(JsonNode? input, JsonObject? metadata)
    {
        if (metadata is null) return;
        foreach (var (pointer, entry) in metadata)
        {
            var selected = input;
            if (pointer != "")
            {
                if (!pointer.StartsWith('/')) Invalid("A contract key must be an input JSON pointer.");
                foreach (var escaped in pointer[1..].Split('/'))
                {
                    for (var i = 0; i < escaped.Length; i++)
                        if (escaped[i] == '~' && (++i == escaped.Length || escaped[i] is not ('0' or '1'))) Invalid("Invalid contract JSON pointer escape.");
                    var part = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    if (selected is JsonObject obj && obj.TryGetPropertyValue(part, out var member)) selected = member;
                    else if (selected is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count && part == index.ToString(System.Globalization.CultureInfo.InvariantCulture)) selected = array[index];
                    else Invalid("Expression contract pointer does not identify an input value: " + pointer);
                }
            }
            var program = ContractExpression(selected);
            if (entry is not JsonObject contracts) { Invalid("Expression contracts must be literal objects."); continue; }
            var syntax = new Acornima.Parser().ParseExpression(program);
            if (contracts.Count == 0 && syntax is not CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Body: BlockStatement } }) continue;
            var declarations = CompiledDeclarations(syntax, contracts);
            foreach (var (name, _) in declarations)
            {
                var contract = contracts[name]!.AsObject();
                if (contract.Count != 2 || !contract.ContainsKey("schema") || contract["schema"] is not (null or JsonObject) ||
                    contract["origin"] is not JsonValue origin || !origin.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
                    Invalid("Each expression contract requires literal origin and schema fields.");
                if (contract["schema"] is JsonObject schema && JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: false) is { Count: > 0 } errors)
                    Invalid("Invalid expression contract schema: " + string.Join("; ", errors));
            }
        }
        static void Invalid(string message) => throw new WorkflowRuntimeException(ErrorCodes.InputValidation, message);
    }

    private static List<(string Name, Expression Value)> CompiledDeclarations(Node syntax, JsonObject contracts)
    {
        if (syntax is not CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Async: false, Body: BlockStatement body } } ||
            body.Body.Count < 2 || body.Body.Last() is not ReturnStatement { Argument: not null })
            throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Invalid compiled binding sequence.");
        var result = new List<(string Name, Expression Value)>();
        foreach (var statement in body.Body.SkipLast(1))
        {
            if (statement is not VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations.Count: 1 } declaration ||
                declaration.Declarations[0] is not { Id: Identifier name, Init: Expression value } ||
                name.Name is "data" or "m" or "checkedMapping" || result.Any(d => d.Name == name.Name) || contracts[name.Name] is not JsonObject)
                throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Compiled bindings require unique checked constants.");
            result.Add((name.Name, value));
        }
        if (contracts.Count != result.Count) throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Compiled binding contracts do not match their values.");
        return result;
    }

    internal JsonNode? EvaluateCheckedExpression(string program, JsonNode? context, JsonObject contracts, JintSandbox.MappingAllowance? allowance = null)
    {
        if (contracts.Count != 0) return EvaluateCompiledBindings(program, context, contracts, readable: true, allowance);
        allowance ??= new JintSandbox.MappingAllowance(_maxStatements, _timeout, _memoryLimitBytes);
        allowance.Start();
        try { return ReadableValue(program, context, new(StringComparer.Ordinal), allowance); }
        finally { allowance.Stop(); }
    }

    private JsonNode? ReadableValue(string program, JsonNode? context, Dictionary<string, JsonNode?> locals, JintSandbox.MappingAllowance allowance)
    {
        var bindings = new Dictionary<string, JsonNode?>(locals, StringComparer.Ordinal) { ["data"] = context };
        if (!TrySelectMapping(program, null, out var value, allowance, bindings))
            throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Unsupported checked expression; unrestricted context import is forbidden.");
        return value;
    }

    // The third checkedMapping argument is compiler-owned validation metadata.
    // Execute the closed const/return recipe in order, retaining exact JSON wiring.
    // Learned mapping programs cannot access this expression entry point.
    private JsonNode? EvaluateCompiledBindings(string program, JsonNode? context, JsonObject contracts, bool readable = false, JintSandbox.MappingAllowance? allowance = null)
    {
        if (program.Length > 65536 || new Acornima.Parser().ParseExpression(program) is not
            CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Async: false, Body: BlockStatement body } } ||
            body.Body.Count < 2 || body.Body.Last() is not ReturnStatement { Argument: not null })
            throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Invalid compiled binding sequence.");
        var declarations = new List<(string Name, Expression Value)>();
        foreach (var statement in body.Body.SkipLast(1))
        {
            if (statement is not VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations.Count: 1 } declaration ||
                declaration.Declarations[0] is not { Id: Identifier name, Init: Expression value } ||
                name.Name is "data" or "checkedMapping" || declarations.Any(d => d.Name == name.Name) || contracts[name.Name] is not JsonObject)
                throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Compiled bindings require unique checked constants.");
            declarations.Add((name.Name, value));
        }
        if (contracts.Count != declarations.Count) throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Compiled binding contracts do not match their values.");
        // This is an ordinary set expression, not a learned mapping invocation.
        // Share its configured expression allowance; nested projections still
        // enforce their own existing, stricter selection limits.
        allowance ??= new JintSandbox.MappingAllowance(_maxStatements, _timeout, _memoryLimitBytes);
        var locals = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        allowance.Start();
        try
        {
            foreach (var (name, expression) in declarations)
            {
                var contract = contracts[name]!.AsObject();
                try
                {
                    allowance.Check();
                    var expressionText = "(" + program[expression.Start..expression.End] + ")";
                    var value = readable ? ReadableValue(expressionText, context, locals, allowance) : EvaluateCore(expressionText, context, locals, allowance);
                    if (value is not JsonObject) throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "set input must be object");
                    if (contract["schema"] is JsonObject schema && JsonSchemaContractValidator.ValidateInstance(value, schema) is { Count: > 0 } errors)
                        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "set output does not satisfy output_schema: " + string.Join("; ", errors));
                    locals.Add(name, value);
                    allowance.ImportedBytes += System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString());
                    allowance.Check();
                }
                catch (WorkflowRuntimeException ex)
                {
                    var details = ex.Details?.DeepClone() as JsonObject ?? new JsonObject();
                    details["compiled_node"] = name; details["location"] = contract["origin"]?.DeepClone();
                    throw new WorkflowRuntimeException(ex.Code, ex.Message, ex.Retryable, ex, details);
                }
            }
            var returned = ((ReturnStatement)body.Body.Last()).Argument!;
            var returnText = "(" + program[returned.Start..returned.End] + ")";
            var result = readable ? ReadableValue(returnText, context, locals, allowance) : EvaluateCore(returnText, context, locals, allowance);
            allowance.Check();
            return result;
        }
        finally { allowance.Stop(); }
    }
}
