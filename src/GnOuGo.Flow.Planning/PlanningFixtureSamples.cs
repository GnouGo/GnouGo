using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

/// <summary>Fixtures are requested only when a declared contract cannot supply a valid deterministic sample.</summary>
internal static class PlanningFixtureSamples
{
    internal static IEnumerable<(string Path, JsonObject Schema, JsonNode? Sample)> Domains(PlanningSession state)
    {
        var main = state.Graph!.Workflows.Single(w => w.Key == state.Graph.Entrypoint);
        var fields = new JsonObject(); var required = new JsonArray(); var inputs = new JsonObject(); var validInputs = true;
        foreach (var port in main.Inputs)
        {
            JsonObject schema;
            try { schema = PlanningGraphCompiler.ToJsonSchema(port.Schema, state.Catalog!); }
            catch (InvalidOperationException) { validInputs = false; continue; }
            fields[port.Name] = schema.DeepClone(); if (port.Required && port.Default is null) required.Add((JsonNode?)JsonValue.Create(port.Name));
            // An unresolved or invalid default belongs to intent repair, not fixture generation.
            if (port.Default is not null && !PlanningGraphValidation.IsLiteral(port.Default)) { validInputs = false; continue; }
            if (port.Default is not null) inputs[port.Name] = PlanningGraphValidation.Literal(port.Default);
            else if (port.Required) inputs[port.Name] = WorkflowPlanDryRunValidator.CreateSampleFromJsonSchema(schema);
        }
        if (validInputs) yield return ("/fixtures/inputs", new() { ["type"] = "object", ["properties"] = fields, ["required"] = required, ["additionalProperties"] = false }, inputs);
        foreach (var workflow in state.Graph.Workflows)
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => n.Type is "mcp.call" or "llm.call"))
            {
                JsonObject? schema;
                var capability = state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
                try { schema = node.StructuredOutput is null ? capability?.OutputSchema : PlanningGraphCompiler.ToJsonSchema(node.StructuredOutput.Schema, state.Catalog); }
                catch (InvalidOperationException) { continue; }
                if (schema is null || schema.Count == 0) continue;
                var sample = capability?.ExampleResponse is { } example && PlanningContractValidation.ValidateInstance(example, schema).Count == 0 ? example.DeepClone()
                    : WorkflowPlanDryRunValidator.CreateArtifactSample(schema, capability?.ArtifactContract);
                yield return ("/fixtures/observations/" + PlanningFieldPaths.Escape(workflow.Key) + "/" + PlanningFieldPaths.Escape(node.Key), schema, sample);
            }
    }
    internal static List<PlanningDiagnostic> Validate(PlanningSession state)
    {
        var findings = new List<PlanningDiagnostic>();
        foreach (var domain in Domains(state))
        {
            var supplied = Values(state, domain.Path);
            if (supplied is null)
            {
                if (PlanningContractValidation.ValidateInstance(domain.Sample, domain.Schema).Count > 0)
                    findings.Add(new("SCENARIO_FIXTURE_REQUIRED", domain.Path, "Deterministic sampling cannot satisfy this contract; supply literal scenario data.", ValidationStage: "fixtures"));
            }
            else if (supplied.Count == 0 || supplied.Any(value => PlanningContractValidation.ValidateInstance(value, domain.Schema).Count > 0))
                findings.Add(new("SCENARIO_FIXTURE_INVALID", domain.Path, "The literal fixture must satisfy its authoritative contract.", ValidationStage: "fixtures"));
        }
        return findings;
    }
    internal static IReadOnlyList<JsonNode?>? Values(PlanningSession state, string path)
    {
        if (path == "/fixtures/inputs") return state.Fixtures?.Inputs is { } inputs ? [inputs] : null;
        var parts = path.Split('/'); var workflow = Unescape(parts[3]); var node = Unescape(parts[4]);
        return state.Fixtures?.Observations.FirstOrDefault(o => o.Workflow == workflow && o.Node == node)?.Responses;
    }
    internal static string Unescape(string text) => text.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
