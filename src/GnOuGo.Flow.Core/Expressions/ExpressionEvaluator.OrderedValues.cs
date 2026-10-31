using Acornima.Ast;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Core.Expressions;

public sealed partial class ExpressionEvaluator
{
    private static bool HasOrderedValueContracts(JsonObject contracts) => contracts.Any(p =>
        p.Value is JsonObject entry && (entry.ContainsKey("order") || entry.ContainsKey("source")));

    // The same closed const/return program, with validation-only aliases. Sources
    // are local names, never executable metadata or access to workflow context.
    private static List<(string Name, string Source, Expression? Value)> OrderedValueChecks(Node syntax, JsonObject contracts)
    {
        static WorkflowRuntimeException Invalid() => new(ErrorCodes.EvalError, "Invalid ordered expression contracts.");
        if (syntax is not CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression
            { Params.Count: 0, Async: false, Body: BlockStatement body } } || body.Body.Count < 2 ||
            body.Body.Last() is not ReturnStatement { Argument: not null }) throw Invalid();
        var declarations = new List<(string Name, Expression Value)>();
        foreach (var statement in body.Body.SkipLast(1))
        {
            if (statement is not VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations.Count: 1 } declaration ||
                declaration.Declarations[0] is not { Id: Identifier name, Init: Expression value } ||
                name.Name is "data" or "m" or "checkedMapping" || declarations.Any(d => d.Name == name.Name)) throw Invalid();
            declarations.Add((name.Name, value));
        }
        var ordered = new SortedDictionary<int, (string Name, string Source)>();
        foreach (var (name, node) in contracts)
        {
            if (node is not JsonObject entry || entry.Any(p => p.Key is not ("order" or "source" or "origin" or "schema" or "instance_path" or "object_only")) ||
                entry["order"] is not JsonValue orderValue || !orderValue.TryGetValue<int>(out var order) ||
                entry["source"] is not JsonValue sourceValue || !sourceValue.TryGetValue<string>(out var source) ||
                entry["origin"] is not JsonValue originValue || !originValue.TryGetValue<string>(out var origin) || string.IsNullOrWhiteSpace(origin) ||
                entry["schema"] is not JsonObject schema || !ordered.TryAdd(order, (name, source))) throw Invalid();
            if (entry.ContainsKey("object_only") && (entry["object_only"] is not JsonValue objectFlag || !objectFlag.TryGetValue<bool>(out var objectOnly) || !objectOnly)) throw Invalid();
            if (entry.TryGetPropertyValue("instance_path", out var pathNode))
            {
                if (pathNode is not JsonValue pathValue || !pathValue.TryGetValue<string>(out var path) ||
                    path.Length > 0 && path[0] != '/') throw Invalid();
                for (var i = 0; i < path.Length; i++)
                    if (path[i] == '~' && (++i == path.Length || path[i] is not ('0' or '1'))) throw Invalid();
            }
            if (JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: false) is { Count: > 0 } errors)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Invalid expression contract schema: " + string.Join("; ", errors));
        }
        var checks = new List<(string Name, string Source, Expression? Value)>();
        var available = new HashSet<string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var (order, check) in ordered)
        {
            if (order != checks.Count) throw Invalid();
            Expression? value = null;
            if (check.Name == check.Source)
            {
                if (next == declarations.Count || declarations[next].Name != check.Name || !available.Add(check.Source)) throw Invalid();
                value = declarations[next++].Value;
            }
            else if (!available.Contains(check.Source) || declarations.Any(d => d.Name == check.Name)) throw Invalid();
            checks.Add((check.Name, check.Source, value));
        }
        if (next != declarations.Count) throw Invalid();
        return checks;
    }

    private JsonNode? EvaluateOrderedValues(string program, JsonNode? context, JsonObject contracts,
        JintSandbox.MappingAllowance? allowance, Action<string, string, string?>? observeCheck)
    {
        if (program.Length > MaxCheckedProgramLength) throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Invalid compiled binding sequence.");
        var syntax = new Acornima.Parser().ParseExpression(program);
        var checks = OrderedValueChecks(syntax, contracts);
        var body = (BlockStatement)((ArrowFunctionExpression)((CallExpression)syntax).Callee).Body;
        allowance ??= new JintSandbox.MappingAllowance(_maxStatements, _timeout, _memoryLimitBytes);
        var locals = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        allowance.Start();
        try
        {
            foreach (var (name, source, expression) in checks)
            {
                var contract = contracts[name]!.AsObject();
                var origin = contract["origin"]!.GetValue<string>();
                try
                {
                    allowance.Check();
                    var value = expression is null ? locals[source] : ReadableValue("(" + program[expression.Start..expression.End] + ")", context, locals, allowance);
                    if (contract.ContainsKey("object_only") && value is not JsonObject)
                        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "set input must be object");
                    var findings = JsonSchemaInstanceValidator.ValidateInstanceFindings(value, contract["schema"], contract["instance_path"]?.GetValue<string>() ?? "");
                    if (findings.Count != 0)
                        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "set output does not satisfy output_schema: " + string.Join("; ", findings.Select(f => f.Message)));
                    if (expression is not null)
                    {
                        locals.Add(source, value);
                        allowance.ImportedBytes += JintSandbox.JsonBytes(value);
                    }
                    allowance.Check();
                }
                catch (WorkflowRuntimeException ex)
                {
                    observeCheck?.Invoke(name, origin, ex.Code);
                    var details = ex.Details?.DeepClone() as JsonObject ?? new JsonObject();
                    details["compiled_node"] = name; details["location"] = origin;
                    throw new WorkflowRuntimeException(ex.Code, ex.Message, ex.Retryable, ex, details);
                }
                observeCheck?.Invoke(name, origin, null);
            }
            var returned = ((ReturnStatement)body.Body.Last()).Argument!;
            var result = ReadableValue("(" + program[returned.Start..returned.End] + ")", context, locals, allowance);
            allowance.Check();
            return result;
        }
        finally { allowance.Stop(); }
    }
}
