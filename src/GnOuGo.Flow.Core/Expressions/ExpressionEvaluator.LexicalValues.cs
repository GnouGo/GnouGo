using Acornima.Ast;
using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Core.Expressions;

public sealed partial class ExpressionEvaluator
{
    private static bool HasLexicalValueContracts(JsonObject contracts) => contracts.Any(p => p.Value is JsonObject entry && entry.ContainsKey("scope"));

    // A static preorder address of a zero-argument lexical block, never a data
    // path or executable expression. Contracts remain ordered within that block.
    private static Dictionary<int, List<(string Name, string Source, Expression? Value)>> LexicalValueChecks(Node syntax, JsonObject contracts)
    {
        var blocks = new List<CallExpression>();
        void Visit(Node node)
        {
            if (node is CallExpression { Arguments.Count: 0, Callee: ArrowFunctionExpression { Params.Count: 0, Async: false, Body: BlockStatement } } call)
                blocks.Add(call);
            foreach (var child in node.ChildNodes) Visit(child);
        }
        Visit(syntax);
        var groups = new Dictionary<int, JsonObject>();
        foreach (var (name, node) in contracts)
        {
            if (node is not JsonObject entry || entry["scope"] is not JsonValue scopeNode || !scopeNode.TryGetValue<string>(out var scope) ||
                !scope.StartsWith("block", StringComparison.Ordinal) || !int.TryParse(scope.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index >= blocks.Count || scope != "block" + index.ToString(CultureInfo.InvariantCulture))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Expression contract scope must identify a unique lexical block.");
            if (!groups.TryGetValue(index, out var group)) groups[index] = group = new();
            var local = entry.DeepClone().AsObject(); local.Remove("scope"); group.Add(name, local);
        }
        return groups.ToDictionary(g => ((BlockStatement)((ArrowFunctionExpression)blocks[g.Key].Callee).Body).Start,
            g => OrderedValueChecks(blocks[g.Key], g.Value));
    }

    private JsonNode? EvaluateLexicalValues(string program, JsonNode? context, JsonObject contracts,
        JintSandbox.MappingAllowance? allowance, Action<string, string, string?>? observeCheck)
    {
        allowance ??= new JintSandbox.MappingAllowance(_maxStatements, _timeout, _memoryLimitBytes);
        allowance.Start();
        try
        {
            if (!TrySelectMapping(program, null, out var value, allowance,
                new(StringComparer.Ordinal) { ["data"] = context }, contracts, observeCheck))
                throw new WorkflowRuntimeException(ErrorCodes.EvalError, "Unsupported checked lexical expression; unrestricted context import is forbidden.");
            allowance.Check();
            return value;
        }
        finally { allowance.Stop(); }
    }
}
