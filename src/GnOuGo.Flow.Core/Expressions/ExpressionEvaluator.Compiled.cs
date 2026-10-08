using System.Text.Json.Nodes;
using Acornima.Ast;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Expressions;

public sealed partial class ExpressionEvaluator
{
    // The third checkedMapping argument is compiler-owned validation metadata.
    // Execute the closed const/return recipe in order, retaining exact JSON wiring.
    // Learned mapping programs cannot access this expression entry point.
    private JsonNode? EvaluateCompiledBindings(string program, JsonNode? context, JsonObject contracts)
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
        var allowance = new JintSandbox.MappingAllowance(_maxStatements, _timeout, _memoryLimitBytes);
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
                    var value = EvaluateCore("(" + program[expression.Start..expression.End] + ")", context, locals, allowance);
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
            var result = EvaluateCore("(" + program[returned.Start..returned.End] + ")", context, locals, allowance);
            allowance.Check();
            return result;
        }
        finally { allowance.Stop(); }
    }
}
