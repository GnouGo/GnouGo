using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Imports every supported executable field, rejecting unrepresentable data explicitly.</summary>
public static class PlanningGraphImporter
{
    public static PlanningGraph Import(string yaml, PlanningPreparation preparation)
        => ImportCore(yaml, preparation);

    // A revision baseline describes existing behavior, but grants no capability to execute it.
    internal static PlanningGraph InspectForRevision(string yaml) => ImportCore(yaml, null);

    private static PlanningGraph ImportCore(string yaml, PlanningPreparation? preparation)
    {
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1) throw new InvalidOperationException("The typed importer accepts exactly one YAML document.");
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        if (document.UnknownFields.Count > 0 || document.Exports is { Count: > 0 } || document.Meta is { Count: > 0 })
            throw new InvalidOperationException("This workflow contains exports, metadata, or unknown fields that the typed importer cannot preserve.");
        var graph = new PlanningGraph { Summary = document.Skill?.Description ?? "Imported workflow", Entrypoint = compiled.Entrypoint!, Functions = document.Functions };
        foreach (var (key, workflow) in document.Workflows)
        {
            if (workflow.Skill is not null) throw new InvalidOperationException("Per-workflow skill metadata requires explicit import support.");
            var imported = new PlanningWorkflow
            {
                Key = key, Purpose = "Imported workflow " + key, Functions = workflow.Functions,
                Inputs = (workflow.Inputs ?? []).Select(p => new PlanningPort
                {
                    Name = p.Key, Required = p.Value.Required,
                    Schema = Schema(JsonSchemaConverter.InputDefToSchema(p.Value).AsObject()),
                    Default = p.Value.Default is null ? null : Value(DefaultValue(p.Value.Default, p.Value.Type))
                }).ToList(),
                Outputs = (workflow.Outputs ?? []).Select(p => new PlanningOutput
                {
                    Name = p.Key, Schema = Schema(JsonSchemaConverter.OutputDefToSchema(p.Value).AsObject()), Value = new PlanningValue { Kind = "expression", Text = p.Value.Expr }
                }).ToList(),
                Steps = workflow.Steps.Select(s => Node(s, preparation)).ToList(),
                Finally = workflow.Finally.Select(s => Node(s, preparation)).ToList()
            };
            imported.OperationIds = PlanningGraphCompiler.Enumerate(imported.Steps.Concat(imported.Finally)).SelectMany(n => n.OperationIds).Distinct(StringComparer.Ordinal).ToList();
            graph.Workflows.Add(imported);
        }
        return graph;
    }

    private static PlanningNode Node(StepDef step, PlanningPreparation? preparation)
    {
        var input = Value(step.Input ?? new JsonObject());
        var node = new PlanningNode
        {
            Key = step.Id, Type = step.Type, Purpose = "Imported " + step.Type, Input = input,
            Output = step.Output, ItemVar = step.ItemVar, IndexVar = step.IndexVar, Retry = step.Retry,
            If = step.If is null ? null : new PlanningValue { Kind = "expression", Text = step.If },
            Expr = step.Expr is null ? null : new PlanningValue { Kind = "expression", Text = step.Expr },
            OutputSchema = step.OutputSchema is JsonObject schema ? Schema(schema) : null,
            Steps = (step.Steps ?? []).Select(s => Node(s, preparation)).ToList(),
            Default = (step.Default ?? []).Select(s => Node(s, preparation)).ToList(),
            Branches = (step.Branches ?? []).Select(b => new PlanningBranch(b.Steps.Select(s => Node(s, preparation)).ToList())).ToList(),
            Cases = (step.Cases ?? []).Select(c => new PlanningCase(c.Value, c.When is null ? null : new PlanningValue { Kind = "expression", Text = c.When }, c.Steps.Select(s => Node(s, preparation)).ToList())).ToList(),
            OnError = (step.OnError?.Cases ?? []).Select(c => new PlanningErrorCase(c.If is null ? null : new PlanningValue { Kind = "expression", Text = c.If }, c.Action, c.SetOutput is null ? null : Value(c.SetOutput), c.Retry)).ToList()
        };
        if (step.Type == "workflow.call")
        {
            var reference = step.Input?["ref"] as JsonObject ?? throw new InvalidOperationException("A workflow call requires a reference.");
            if ((reference["kind"]?.GetValue<string>() ?? "local") != "local" || reference.Any(p => p.Key is not ("kind" or "name"))) throw new InvalidOperationException("Only local workflow references are supported by the typed importer.");
            var member = input.Members.FindIndex(m => m.Name == "ref");
            input.Members[member] = new("ref", new PlanningValue { Kind = "workflow", Source = reference["name"]!.GetValue<string>() });
        }
        if (step.Type == "mcp.call" && preparation is not null)
        {
            var candidates = preparation.Capabilities.Where(c => c.StepType == "mcp.call" && c.Server == step.Input?["server"]?.GetValue<string>() && c.Method == step.Input?["method"]?.GetValue<string>() &&
                c.RequestBindings.All(binding => JsonNode.DeepEquals(PlanningGraphCompiler.ReadPointer(step.Input?["request"], binding.Path), binding.Value))).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("An imported external call requires one unambiguous locked capability binding.");
            node.CapabilityId = candidates[0].Id;
            node.OperationIds = candidates[0].OperationIds.ToList();
        }
        else if (preparation is not null)
        {
            var candidates = preparation.Capabilities.Where(c => c.StepType == step.Type && c.Required).ToArray();
            if (candidates.Length == 1) { node.CapabilityId = candidates[0].Id; node.OperationIds = candidates[0].OperationIds.ToList(); }
        }
        return node;
    }

    internal static PlanningValue Value(JsonNode? value) => value switch
    {
        null => new(),
        JsonObject obj => new() { Kind = "object", Members = obj.Select(p => new PlanningMember(p.Key, Value(p.Value))).ToList() },
        JsonArray array => new() { Kind = "array", Items = array.Select(Value).ToList() },
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => new() { Kind = text.Contains("${", StringComparison.Ordinal) ? "template" : "string", Text = text },
        JsonValue scalar when scalar.TryGetValue<bool>(out var boolean) => new() { Kind = "boolean", Boolean = boolean },
        JsonValue scalar when scalar.GetValueKind() == System.Text.Json.JsonValueKind.Number && decimal.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => new() { Kind = "number", Number = number },
        _ => throw new InvalidOperationException("Unsupported literal in imported workflow.")
    };

    internal static PlanningSchema Schema(JsonObject schema)
    {
        var unsupported = schema.Select(p => p.Key).Except(["type", "description", "nullable", "enum", "items", "properties", "required", "additionalProperties", "default"], StringComparer.Ordinal).ToArray();
        if (unsupported.Length > 0) throw new InvalidOperationException("The imported schema uses unsupported keywords: " + string.Join(", ", unsupported));
        if (schema["type"] is JsonArray union && union.Count(v => v?.GetValue<string>() != "null") != 1)
            throw new InvalidOperationException("The imported schema has an unsupported union type.");
        if (schema["additionalProperties"] is JsonValue openness && openness.TryGetValue<bool>(out var open) && open)
            throw new InvalidOperationException("Open object schemas require typed additional properties for import.");
        var type = schema["type"] is JsonArray types ? types.Select(t => t!.GetValue<string>()).FirstOrDefault(t => t != "null") : schema["type"]?.GetValue<string>();
        if (type is null or "any") throw new InvalidOperationException("The imported workflow must have concrete boundary schemas.");
        var required = (schema["required"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        return new PlanningSchema
        {
            Type = type, Nullable = schema["type"] is JsonArray array && array.Any(v => v?.GetValue<string>() == "null"),
            Description = schema["description"]?.GetValue<string>(),
            Enum = (schema["enum"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToList(),
            Items = schema["items"] is JsonObject items ? Schema(items) : null,
            Properties = (schema["properties"] as JsonObject ?? []).Select(p => new PlanningPort { Name = p.Key, Schema = Schema(p.Value!.AsObject()), Required = required.Contains(p.Key) }).ToList(),
            AdditionalProperties = schema["additionalProperties"] is JsonObject additional ? Schema(additional) : null
        };
    }

    private static JsonNode? DefaultValue(object value, string type) => value switch
    {
        JsonNode node => node.DeepClone(),
        string text when type == "boolean" && bool.TryParse(text, out var parsed) => JsonValue.Create(parsed),
        string text when type is "integer" or "number" && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
        string text => JsonValue.Create(text),
        bool boolean => JsonValue.Create(boolean),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => throw new InvalidOperationException("Unsupported imported default value.")
    };
}
