using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Assets.Animation.Preview;

public static class WorkflowPreviewParser
{
    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "version", "dsl", "name", "entrypoint", "workflows", "meta", "skill", "skills", "functions", "exports"
    };

    private static readonly HashSet<string> WorkflowFields = new(StringComparer.Ordinal)
    {
        "inputs", "steps", "outputs", "skill", "skills", "functions"
    };

    private static readonly HashSet<string> StepFields = new(StringComparer.Ordinal)
    {
        "id", "type", "if", "expr", "input", "output", "output_schema", "retry", "on_error",
        "steps", "branches", "cases", "default", "item_var", "index_var"
    };

    public static WorkflowPreviewDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
                throw new WorkflowPreviewParseException("The YAML document is empty.");

            var root = stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new WorkflowPreviewParseException("The YAML root must be a mapping.");

            var document = new WorkflowPreviewDocument
            {
                Version = ReadInt(root, "version") ?? ReadInt(root, "dsl") ?? 1,
                Name = ReadScalar(root, "name"),
                Entrypoint = ReadScalar(root, "entrypoint")
            };
            CollectUnknown(root, "$", RootFields, document);

            var workflowsNode = GetMapping(root, "workflows")
                ?? throw new WorkflowPreviewParseException("Missing required 'workflows' mapping.");

            foreach (var pair in workflowsNode.Children)
            {
                var workflowName = ScalarValue(pair.Key)
                    ?? throw new WorkflowPreviewParseException("Workflow names must be strings.");
                var workflowNode = pair.Value as YamlMappingNode
                    ?? throw new WorkflowPreviewParseException($"Workflow '{workflowName}' must be a mapping.");
                var path = $"workflows.{workflowName}";
                CollectUnknown(workflowNode, path, WorkflowFields, document);

                var workflow = new WorkflowPreviewDefinition
                {
                    Inputs = GetNode(workflowNode, "inputs") is { } inputsNode
                        ? ConvertNode(inputsNode) as JsonObject
                        : null,
                    Steps = ParseSteps(GetSequence(workflowNode, "steps"), $"{path}.steps", document)
                };
                document.Workflows[workflowName] = workflow;
            }

            document.Entrypoint ??= document.Workflows.ContainsKey("main")
                ? "main"
                : document.Workflows.Keys.FirstOrDefault();
            return document;
        }
        catch (WorkflowPreviewParseException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new WorkflowPreviewParseException($"Invalid YAML: {exception.Message}");
        }
    }

    private static List<WorkflowPreviewStep> ParseSteps(
        YamlSequenceNode? sequence,
        string path,
        WorkflowPreviewDocument document)
    {
        if (sequence is null)
            return [];

        var steps = new List<WorkflowPreviewStep>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var stepNode = sequence.Children[index] as YamlMappingNode
                ?? throw new WorkflowPreviewParseException($"Step at '{path}[{index}]' must be a mapping.");
            var stepPath = $"{path}[{index}]";
            CollectUnknown(stepNode, stepPath, StepFields, document);

            var step = new WorkflowPreviewStep
            {
                Id = ReadScalar(stepNode, "id") ?? "",
                Type = ReadScalar(stepNode, "type") ?? "",
                If = ReadScalar(stepNode, "if"),
                Expr = ReadScalar(stepNode, "expr"),
                Input = GetNode(stepNode, "input") is { } inputNode ? ConvertNode(inputNode) : null,
                ItemVar = ReadScalar(stepNode, "item_var"),
                IndexVar = ReadScalar(stepNode, "index_var"),
                Steps = GetSequence(stepNode, "steps") is { } childSteps
                    ? ParseSteps(childSteps, $"{stepPath}.steps", document)
                    : null,
                Default = GetSequence(stepNode, "default") is { } defaultSteps
                    ? ParseSteps(defaultSteps, $"{stepPath}.default", document)
                    : null,
                Branches = ParseBranches(GetSequence(stepNode, "branches"), $"{stepPath}.branches", document),
                Cases = ParseCases(GetSequence(stepNode, "cases"), $"{stepPath}.cases", document)
            };
            steps.Add(step);
        }

        return steps;
    }

    private static List<WorkflowPreviewBranch>? ParseBranches(
        YamlSequenceNode? sequence,
        string path,
        WorkflowPreviewDocument document)
    {
        if (sequence is null)
            return null;

        var branches = new List<WorkflowPreviewBranch>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var node = sequence.Children[index] as YamlMappingNode
                ?? throw new WorkflowPreviewParseException($"Branch at '{path}[{index}]' must be a mapping.");
            CollectUnknown(node, $"{path}[{index}]", new HashSet<string>(["id", "name", "steps"], StringComparer.Ordinal), document);
            branches.Add(new WorkflowPreviewBranch
            {
                Name = ReadScalar(node, "name") ?? ReadScalar(node, "id"),
                Steps = ParseSteps(GetSequence(node, "steps"), $"{path}[{index}].steps", document)
            });
        }

        return branches;
    }

    private static List<WorkflowPreviewCase>? ParseCases(
        YamlSequenceNode? sequence,
        string path,
        WorkflowPreviewDocument document)
    {
        if (sequence is null)
            return null;

        var cases = new List<WorkflowPreviewCase>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var node = sequence.Children[index] as YamlMappingNode
                ?? throw new WorkflowPreviewParseException($"Switch case at '{path}[{index}]' must be a mapping.");
            CollectUnknown(node, $"{path}[{index}]", new HashSet<string>(["value", "when", "steps"], StringComparer.Ordinal), document);
            cases.Add(new WorkflowPreviewCase
            {
                Value = ReadScalar(node, "value"),
                When = ReadScalar(node, "when"),
                Steps = ParseSteps(GetSequence(node, "steps"), $"{path}[{index}].steps", document)
            });
        }

        return cases;
    }

    private static void CollectUnknown(
        YamlMappingNode node,
        string path,
        IReadOnlySet<string> knownFields,
        WorkflowPreviewDocument document)
    {
        foreach (var pair in node.Children)
        {
            var field = ScalarValue(pair.Key);
            if (field is not null && !knownFields.Contains(field))
                document.UnknownFields.Add(new WorkflowPreviewUnknownField(path, field));
        }
    }

    private static JsonNode? ConvertNode(YamlNode node) => node switch
    {
        YamlMappingNode mapping => ConvertMapping(mapping),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ConvertNode).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null
    };

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            var key = ScalarValue(pair.Key) ?? pair.Key.ToString();
            result[key] = ConvertNode(pair.Value);
        }
        return result;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        var hasExplicitNullTag = !scalar.Tag.IsEmpty
            && !scalar.Tag.IsNonSpecific
            && scalar.Tag.Value.EndsWith(":null", StringComparison.Ordinal);
        if (value is null || hasExplicitNullTag)
            return null;
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
            return JsonValue.Create(value);
        if (bool.TryParse(value, out var boolean))
            return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        if (value is "null" or "Null" or "NULL" or "~")
            return null;
        return JsonValue.Create(value);
    }

    private static int? ReadInt(YamlMappingNode node, string key) =>
        int.TryParse(ReadScalar(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? ReadScalar(YamlMappingNode node, string key) =>
        GetNode(node, key) is { } value ? ScalarValue(value) : null;

    private static string? ScalarValue(YamlNode node) => (node as YamlScalarNode)?.Value;
    private static YamlMappingNode? GetMapping(YamlMappingNode node, string key) => GetNode(node, key) as YamlMappingNode;
    private static YamlSequenceNode? GetSequence(YamlMappingNode node, string key) => GetNode(node, key) as YamlSequenceNode;

    private static YamlNode? GetNode(YamlMappingNode node, string key)
    {
        foreach (var pair in node.Children)
        {
            if (string.Equals(ScalarValue(pair.Key), key, StringComparison.Ordinal))
                return pair.Value;
        }
        return null;
    }
}
