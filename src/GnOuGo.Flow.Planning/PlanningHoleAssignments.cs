using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Materializes only coordinator-issued targets; the received delta remains durable on failure.</summary>
internal static class PlanningHoleAssignments
{
    internal static string WorkflowFingerprint(PlanningWorkflow workflow) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(workflow, PlanningJsonContext.Default.PlanningWorkflow));
    internal static (PlanningGraph? Graph, List<PlanningDiagnostic> Diagnostics) Evaluate(PlanningSnapshot state, PlanningStagedAssignments staged)
    {
        var progress = state.Construction.Workflows.Single(w => w.WorkflowKey == staged.WorkflowKey);
        var workflow = state.Graph!.Workflows.Single(w => w.Key == staged.WorkflowKey);
        if (WorkflowFingerprint(workflow) != staged.WorkflowFingerprint || PlanningWorkflowConstruction.DependencyFingerprint(state, progress) != staged.DependencyFingerprint)
            throw new PlanningConflictException("The staged fields no longer target the retained workflow and dependency contracts.");
        var diagnostics = PlanningContractValidation.ValidateInstanceFindings(staged.Payload, staged.ResponseSchema)
            .Select(d => new PlanningDiagnostic("HOLE_RESPONSE_INVALID", d.InstancePointer, d.Message, Rule: d.Rule)).ToList();
        if (diagnostics.Count > 0) return (null, diagnostics);
        var json = PlanningFieldPaths.Json(state.Graph);
        foreach (var hole in staged.Targets)
        {
            try
            {
                var assignment = staged.Payload["assignments"]![hole.Id]!.AsObject();
                JsonNode? value;
                if (hole.Kind == "schema")
                {
                    var contract = JsonSerializer.Deserialize(assignment, PlanningJsonContext.Default.PlanningSchema)!;
                    PlanningGraphValidation.RequireTyped(PlanningGraphCompiler.ToJsonSchema(contract, state.Preparation!), 0);
                    value = JsonSerializer.SerializeToNode(contract, PlanningJsonContext.Default.PlanningSchema);
                }
                else
                {
                    var binding = Value(assignment, staged.Bindings, staged.ParameterScopes.GetValueOrDefault(hole.Id));
                    if (binding is not null)
                    {
                        if (hole.Kind == "default" && !PlanningGraphValidation.IsLiteral(binding)) throw new InvalidOperationException("An input default must be literal.");
                        if (binding.Kind == "compute") PlanningComputations.Validate(binding);
                        if (PlanningHoleRequests.Expected(state, workflow, hole) is { } expected && PlanningGraphValidation.IsLiteral(binding))
                        {
                            var failures = PlanningContractValidation.ValidateInstanceFindings(PlanningGraphValidation.Literal(binding), expected);
                            foreach (var failure in failures)
                                diagnostics.Add(new("HOLE_VALUE_INVALID", assignment.ContainsKey("json") ? "/assignments/" + hole.Id + "/json" + failure.InstancePointer : LiteralLocation(binding, "/assignments/" + hole.Id + "/value", failure.InstancePointer), failure.Message, Rule: failure.Rule));
                        }
                    }
                    value = binding is null ? null : JsonSerializer.SerializeToNode(binding, PlanningJsonContext.Default.PlanningValue);
                }
                PlanningFieldPaths.Replace(json, hole.Path, value);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or Acornima.ParseErrorException)
            { diagnostics.Add(new("HOLE_VALUE_INVALID", "/assignments/" + hole.Id, ex.Message)); }
        }
        if (diagnostics.Count > 0) return (null, diagnostics);
        var graph = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningGraph)!;
        if (state.Construction.SkeletonFingerprint != PlanningGraphSkeleton.Fingerprint(graph))
            throw new PlanningConflictException("Hole assignments changed frozen topology.");
        var view = PlanningContext.Clone(state); view.Graph = graph;
        // Receipt replay keeps its response contract, but never retains obsolete binding authority.
        foreach (var hole in staged.Targets.Where(h => h.Kind != "schema"))
        {
            var scoped = PlanningContext.Clone(state); scoped.Graph = PlanningContext.Clone(graph);
            foreach (var target in staged.Targets) scoped.Construction.Holes.Single(h => h.Id == target.Id).Resolved = true;
            var current = PlanningFieldPaths.Json(scoped.Graph);
            var value = JsonSerializer.Deserialize(PlanningFieldPaths.Read(current, hole.Path)?.ToJsonString() ?? "null", PlanningJsonContext.Default.PlanningValue);
            PlanningFieldPaths.Replace(current, hole.Path, JsonSerializer.SerializeToNode(new PlanningValue { Kind = PlanningGraphSkeleton.Unresolved }, PlanningJsonContext.Default.PlanningValue));
            scoped.Graph = JsonSerializer.Deserialize(current, PlanningJsonContext.Default.PlanningGraph)!;
            scoped.Construction.Holes.Single(h => h.Id == hole.Id).Resolved = false;
            if (PlanningHoleEligibility.Validate(scoped, scoped.Graph.Workflows.Single(w => w.Key == workflow.Key), hole, value) is { } invalid)
                diagnostics.Add(Map(invalid, staged));
        }
        view.Construction.Workflows.Single(w => w.WorkflowKey == workflow.Key).Status = "constructed";
        var remainingHoles = state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && staged.Targets.All(t => t.Id != h.Id)).ToArray();
        var located = PlanningGraphValidation.Located(graph.Workflows.Single(w => w.Key == workflow.Key).Steps, "/workflows/" + graph.Workflows.FindIndex(w => w.Key == workflow.Key) + "/steps")
            .Concat(PlanningGraphValidation.Located(graph.Workflows.Single(w => w.Key == workflow.Key).Finally, "/workflows/" + graph.Workflows.FindIndex(w => w.Key == workflow.Key) + "/finally")).ToArray();
        foreach (var finding in PlanningValidationPipeline.TypedFindings(view, graph))
        {
            var normalized = PlanningDiagnosticLocations.TypedInput(finding, graph);
            var owner = located.Where(p => normalized.Location.StartsWith(p.Path + "/", StringComparison.Ordinal)).OrderByDescending(p => p.Path.Length).FirstOrDefault();
            if (remainingHoles.Length != 0 && !(owner.Node is not null && staged.Targets.Any(t => t.Path.StartsWith(owner.Path + "/", StringComparison.Ordinal)) && !remainingHoles.Any(h => h.Path.StartsWith(owner.Path + "/", StringComparison.Ordinal))) &&
                !staged.Targets.Any(t => normalized.Location == t.Path || normalized.Location.StartsWith(t.Path + "/", StringComparison.Ordinal))) continue;
            var descendants = staged.Targets.Where(t => t.Path.StartsWith(normalized.Location + "/", StringComparison.Ordinal)).ToArray();
            if (descendants.Length > 0)
                diagnostics.AddRange(descendants.Select(t => Map(normalized with { Location = t.Path }, staged)));
            else diagnostics.Add(Map(normalized, staged));
        }
        return (graph, diagnostics);
    }
    internal static string LiteralLocation(PlanningValue literal, string root, string pointer)
    {
        var current = literal;
        foreach (var token in pointer.Split('/').Skip(1))
        {
            if (current.Kind == "object")
            {
                var name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                var index = current.Members.FindIndex(m => m.Name == name);
                if (index < 0) return root;
                root += "/members/" + index + "/value"; current = current.Members[index].Value;
            }
            else if (current.Kind == "array" && int.TryParse(token, out var index) && index >= 0 && index < current.Items.Count)
            { root += "/items/" + index; current = current.Items[index]; }
            else break;
        }
        return root;
    }
    internal static PlanningValue? Value(JsonObject assignment, IReadOnlyDictionary<string, PlanningValue> bindings, IReadOnlyList<string>? fixedParameters = null)
    {
        if (assignment["kind"]?.ToString() == "compute" && assignment["bindings"] is JsonArray parameters &&
            parameters.Select(p => p!.ToString()).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
            throw new InvalidOperationException("A computation cannot repeat a binding parameter.");
        return assignment["kind"]!.GetValue<string>() switch
        {
            "absent" => null,
            // Retained receipts carry their original schema. New literal requests expose destination JSON,
            // which the coordinator turns into the same typed value as every other assignment.
            "literal" => assignment.ContainsKey("json") ? PlanningSkeletonInputs.Literal(assignment["json"]) : JsonSerializer.Deserialize(assignment["value"]!, PlanningJsonContext.Default.PlanningValue)!,
            "binding" => Clone(bindings[assignment["binding"]!.GetValue<string>()]),
            "compute" => Compute(),
            _ => throw new InvalidOperationException("Unknown hole assignment.")
        };
        PlanningValue Compute()
        {
            var value = new PlanningValue { Kind = "compute", Text = assignment["expression"]!.GetValue<string>(),
                Members = (assignment["bindings"] is JsonArray retainedParameters ? retainedParameters.Select(v => v!.GetValue<string>())
                    : fixedParameters ?? throw new InvalidOperationException("The computation has no retained coordinator parameter scope."))
                    .Select(id => new PlanningMember(id, Clone(bindings[id]))).ToList() };
            var used = PlanningComputationScopes.Used(new Acornima.Parser().ParseExpression(PlanningComputations.Expression(value.Text)), value.Members.Select(m => m.Name).ToArray());
            value.Members.RemoveAll(m => !used.Contains(m.Name));
            PlanningComputations.Validate(value);
            // An identity expression is the existing typed value. Keeping that
            // identity preserves item contracts and provenance without inference.
            return new Acornima.Parser().ParseExpression(PlanningComputations.Expression(value.Text)) is Acornima.Ast.Identifier parameter
                ? Clone(value.Members.Single(m => m.Name == parameter.Name).Value) : value;
        }
        static PlanningValue Clone(PlanningValue value) => JsonSerializer.Deserialize(JsonSerializer.Serialize(value, PlanningJsonContext.Default.PlanningValue), PlanningJsonContext.Default.PlanningValue)!;
    }
    internal static PlanningDiagnostic Map(PlanningDiagnostic diagnostic, PlanningStagedAssignments staged)
    {
        var hole = staged.Targets.Where(h => diagnostic.Location == h.Path || diagnostic.Location.StartsWith(h.Path + "/", StringComparison.Ordinal)).OrderByDescending(h => h.Path.Length).FirstOrDefault();
        if (hole is null) return diagnostic;
        var root = "/assignments/" + hole.Id;
        var suffix = diagnostic.Location[hole.Path.Length..];
        if (hole.Kind == "schema") return diagnostic with { Location = root + suffix };
        var kind = staged.Payload["assignments"]?[hole.Id]?["kind"]?.ToString();
        return diagnostic with { Location = kind switch
        {
            "binding" => root + "/binding",
            "compute" when suffix == "/text" => root + "/expression",
            "compute" when suffix.StartsWith("/members/", StringComparison.Ordinal) => staged.Payload["assignments"]![hole.Id]!["bindings"] is null ? root + "/expression" : root + "/bindings/" + suffix.Split('/')[2],
            "compute" => root,
            "literal" when staged.Payload["assignments"]![hole.Id]!.AsObject().ContainsKey("json") => root + "/json" + JsonLiteralSuffix(staged.Payload["assignments"]![hole.Id]!["json"], suffix),
            "literal" => root + "/value" + suffix,
            _ => root
        } };
    }
    private static string JsonLiteralSuffix(JsonNode? value, string suffix)
    {
        var parts = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries); var path = "";
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "members" && i + 2 < parts.Length && int.TryParse(parts[i + 1], out var index) && value is JsonObject obj && index >= 0 && index < obj.Count)
            { var member = obj.ElementAt(index); path += "/" + PlanningFieldPaths.Escape(member.Key); value = member.Value; i += 2; }
            else if (parts[i] == "items" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out index) && value is JsonArray array && index >= 0 && index < array.Count)
            { path += "/" + index; value = array[index]; i++; }
            else break; // Typed scalar storage names (text/number/boolean) are not JSON members.
        }
        return path;
    }
    internal static void Commit(PlanningSnapshot state, PlanningStagedAssignments staged, PlanningGraph graph)
    {
        state.Graph = graph;
        foreach (var hole in staged.Targets)
        {
            var retained = state.Construction.Holes.Single(h => h.Id == hole.Id);
            retained.Resolved = true;
            retained.ResolutionOrigin ??= retained.ExposedRequests.Count > 0 ? "model" : null;
        }
        state.Construction.Candidates.Remove(staged);
        var progress = state.Construction.Workflows.Single(w => w.WorkflowKey == staged.WorkflowKey);
        progress.ResolvedHoles = state.Construction.Holes.Count(h => h.WorkflowKey == staged.WorkflowKey && h.Resolved);
        progress.UnresolvedHoles = state.Construction.Holes.Count(h => h.WorkflowKey == staged.WorkflowKey && !h.Resolved);
        progress.Status = progress.UnresolvedHoles == 0 ? "constructed" : "pending";
        progress.Diagnostics.Clear(); progress.Gate = progress.Status == "constructed" ? PlanningGates.Typed : PlanningGates.Response;
        progress.GraphFingerprint = WorkflowFingerprint(graph.Workflows.Single(w => w.Key == staged.WorkflowKey));
        state.Diagnostics.Clear(); PlanningContext.InvalidateArtifact(state); PlanningConvergence.Refresh(state);
    }
}
