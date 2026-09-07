using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Independent technical checks run together, before a bounded repair.</summary>
public static class PlanningExecutableValidation
{
    public static IReadOnlyList<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningPreparation preparation)
    {
        var errors = PlanningDataflow.OperationInputFindings(graph, preparation).ToList();
        Script(graph.Functions, "/functions");
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var path = "/workflows/" + wi;
            Script(workflow.Functions, path + "/functions");
            foreach (var (node, location) in PlanningGraphValidation.Located(workflow.Steps, path + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, path + "/finally")))
            {
                if (node.Expr is not null && node.Type != "switch") errors.Add(new("NATIVE_FIELD_UNSUPPORTED", location + "/expr", "Only switch uses expr. Compute set outputs in input values; do not put a transformation in an ignored field."));
                errors.AddRange(PlanningDecisionRouting.ConditionFindings(node, preparation, location));
                Values(node.Input, location + "/input");
                if (node.Expr is not null) Values(node.Expr, location + "/expr");
                if (node.If is not null) Values(node.If, location + "/if");
                for (var i = 0; i < node.Cases.Count; i++) if (node.Cases[i].When is { } when) Values(when, location + "/cases/" + i + "/when");
                for (var i = 0; i < node.OnError.Count; i++)
                {
                    if (node.OnError[i].If is { } condition) Values(condition, location + "/onError/" + i + "/if");
                    if (node.OnError[i].SetOutput is { } value) Values(value, location + "/onError/" + i + "/setOutput");
                }
                try
                {
                    var preview = Preview(node.Input);
                    // set resolves its entire input value at runtime and can assert the result schema.
                    if (node.Type == "set" && node.Input.Kind is "expression" or "compute" or "input" or "output" or "loop_item" or "loop_index") continue;
                    var input = preview as JsonObject ?? throw new InvalidOperationException("This step requires an object input. For set, put computations in input values or supply an object-producing expression.");
                    var capability = preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
                    if (capability is not null)
                    {
                        foreach (var (key, value) in capability.FixedInput) input[key] ??= value?.DeepClone();
                        if (node.Type == "mcp.call") { input["server"] ??= capability.Server; input["method"] ??= capability.Method; input["kind"] ??= capability.Kind; }
                    }
                    if (BuiltInStepContracts.Get(node.Type) is { } contract)
                        errors.AddRange(PlanningContractValidation.ValidateStepInput(input, contract).Select(d => new PlanningDiagnostic("NATIVE_INPUT_INVALID", location + "/" + d.Field.Replace('.', '/'), d.Message)));
                    if (node.Type == "human.input")
                    {
                        var doc = new WorkflowDocument { Skill = new() { Description = "Validate expression contract", Inputs = new(), Outputs = new() }, Workflows = new() { ["main"] = new() { Steps = [new() { Id = "question", Type = node.Type, Input = input }] } } };
                        errors.AddRange(new WorkflowValidator().Validate(doc).Select(d => new PlanningDiagnostic(d.Code, location + "/" + d.Field?.Replace('.', '/'), d.Message)));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { errors.Add(new("NATIVE_INPUT_INVALID", location + "/input", ex.Message)); }
            }
            for (var i = 0; i < workflow.Outputs.Count; i++) Values(workflow.Outputs[i].Value, path + "/outputs/" + i + "/value");
        }
        errors.AddRange(PlanningGraphValidation.Validate(graph, preparation));
        errors.AddRange(PlanningGraphCompiler.ValidateValues(graph, preparation));
        return errors.DistinctBy(d => (d.Code, d.Location, d.Message)).ToArray();

        void Script(string? script, string location)
        {
            if (script is null) return;
            try { new Acornima.Parser().ParseScript(script); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(new("FUNCTION_SYNTAX_INVALID", location, ex.Message)); }
        }
        void Values(PlanningValue value, string location)
        {
            if (value.Kind == "compute")
                try { PlanningComputations.Validate(value); }
                catch (Exception ex) when (ex is InvalidOperationException or Acornima.ParseErrorException) { errors.Add(new("COMPUTATION_BINDING_INVALID", location, ex.Message)); }
            if (value.Kind == "expression")
            {
                try
                {
                    var expr = value.Text ?? throw new InvalidOperationException("An expression needs text.");
                    if (expr.StartsWith("${", StringComparison.Ordinal))
                    {
                        var parts = ExpressionSegments.Read(expr);
                        if (parts.Count != 1 || parts[0].Length != expr.Length) throw new InvalidOperationException("Use one expression value or a bound template.");
                        expr = parts[0].Expression;
                    }
                    ExpressionEvaluator.Validate(expr);
                    CheckAliases(new Acornima.Parser().ParseExpression(expr), new HashSet<string>(StringComparer.Ordinal));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(new("EXPR_PARSE", location + "/text", ex.Message)); }
            }
            if (value.Kind == "template")
            {
                var text = value.Text ?? "";
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in value.Members)
                {
                    var placeholder = "{{" + member.Name + "}}";
                    if (!names.Add(member.Name) || !text.Contains(placeholder, StringComparison.Ordinal)) errors.Add(new("TEMPLATE_BINDING_INVALID", location, "Each template member needs one unique name and a {{name}} placeholder in this template text. Missing or duplicated: " + placeholder));
                    text = text.Replace(placeholder, "", StringComparison.Ordinal);
                }
                if (text.Contains("{{", StringComparison.Ordinal)) errors.Add(new("TEMPLATE_BINDING_INVALID", location, "The template contains an undeclared placeholder."));
            }
            for (var i = 0; i < value.Members.Count; i++) Values(value.Members[i].Value, location + "/members/" + i + "/value");
            for (var i = 0; i < value.Items.Count; i++) Values(value.Items[i], location + "/items/" + i);
        }

        static void CheckAliases(Acornima.Ast.Node node, HashSet<string> bound)
        {
            var parameters = node switch
            {
                Acornima.Ast.ArrowFunctionExpression arrow => arrow.Params.OfType<Acornima.Ast.Identifier>().Select(p => p.Name),
                Acornima.Ast.FunctionExpression function => function.Params.OfType<Acornima.Ast.Identifier>().Select(p => p.Name),
                _ => Enumerable.Empty<string>()
            };
            var scope = new HashSet<string>(bound, StringComparer.Ordinal); scope.UnionWith(parameters);
            if (node is Acornima.Ast.MemberExpression { Object: Acornima.Ast.Identifier identifier } && identifier.Name is "outputs" or "structured" or "input" or "runtime" && !scope.Contains(identifier.Name))
                throw new InvalidOperationException("Unsupported context alias '" + identifier.Name + "'. Use typed references or the declared data context.");
            foreach (var child in node.ChildNodes) CheckAliases(child, scope);
        }
    }

    public static IReadOnlyList<PlanningDiagnostic> CompilerErrors(WorkflowCompilationException exception, PlanningGraph graph)
    {
        var locations = graph.Workflows.SelectMany((w, wi) => PlanningGraphValidation.Located(w.Steps, "/workflows/" + wi + "/steps").Concat(PlanningGraphValidation.Located(w.Finally, "/workflows/" + wi + "/finally"))
            .Select(n => (Workflow: w.Key == graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(w.Key)[..16], Id: "n_" + PlanningGraphCompiler.Fingerprint(n.Node.Key)[..16], n.Path))).ToArray();
        return exception.Errors.Select(e =>
        {
            var matches = locations.Where(n => n.Id == e.StepId && (e.WorkflowName is null || n.Workflow == e.WorkflowName)).ToArray();
            return new PlanningDiagnostic(e.Code, matches.Length == 1 ? matches[0].Path + "/" + (e.Field ?? "").Replace('.', '/') : "/workflows", e.Message);
        }).ToArray();
    }

    public static PlanningDiagnostic MapRuntimeDiagnostic(PlanningDiagnostic diagnostic, PlanningGraph graph)
    {
        if (diagnostic.Location.StartsWith("field:functions", StringComparison.Ordinal))
            return diagnostic with { Location = "/" + diagnostic.Location["field:".Length..].Replace('.', '/') };
        if (!diagnostic.Location.StartsWith("workflow:", StringComparison.Ordinal)) return diagnostic;
        var parts = diagnostic.Location.Split('/');
        var workflowName = parts[0]["workflow:".Length..];
        var wi = graph.Workflows.FindIndex(w => (w.Key == graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(w.Key)[..16]) == workflowName);
        if (wi < 0) return diagnostic;
        var workflow = graph.Workflows[wi]; var path = "/workflows/" + wi;
        var field = parts.FirstOrDefault(p => p.StartsWith("field:", StringComparison.Ordinal))?["field:".Length..];
        var step = parts.FirstOrDefault(p => p.StartsWith("step:", StringComparison.Ordinal))?["step:".Length..];
        if (step is not null)
        {
            var locations = PlanningGraphValidation.Located(workflow.Steps, path + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, path + "/finally"))
                .Where(n => "n_" + PlanningGraphCompiler.Fingerprint(n.Node.Key)[..16] == step).ToArray();
            if (locations.Length != 1) return diagnostic;
            path = locations[0].Path;
        }
        else if (field is not null)
        {
            foreach (var boundary in new[] { "inputs", "outputs" })
            {
                if (!field.StartsWith(boundary + ".", StringComparison.Ordinal)) continue;
                var names = boundary == "inputs" ? workflow.Inputs.Select(p => p.Name) : workflow.Outputs.Select(p => p.Name);
                var index = names.ToList().FindIndex(n => field == boundary + "." + n);
                if (index >= 0) return diagnostic with { Location = path + "/" + boundary + "/" + index };
            }
        }
        return diagnostic with { Location = path + (field is null ? "" : "/" + field.Replace('.', '/')) };
    }

    private static JsonNode? Preview(PlanningValue value) => value.Kind switch
    {
        "object" => new JsonObject(value.Members.Select(m => new KeyValuePair<string, JsonNode?>(m.Name, Preview(m.Value)))),
        "array" => new JsonArray(value.Items.Select(Preview).ToArray()),
        "workflow" => new JsonObject { ["kind"] = "local", ["name"] = value.Source },
        "input" or "output" or "loop_item" or "loop_index" or "expression" or "compute" or "template" => JsonValue.Create("${data.value}"),
        _ => PlanningGraphValidation.Literal(value)
    };
}
