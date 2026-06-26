using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Parsing;

/// <summary>
/// Parses YAML workflow documents into the DSL model.
/// Uses RepresentationModel (DOM API) — no reflection, AOT-safe.
/// </summary>
public static class WorkflowParser
{
    public static WorkflowDocument Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
            throw new WorkflowParseException("Empty YAML document");

        var root = stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new WorkflowParseException("Root must be a YAML mapping");

        var doc = new WorkflowDocument();
        doc.RawYaml = yaml;
        CollectUnknownYamlFields(root, doc.UnknownFields);

        // version
        doc.Version = ParseWorkflowVersion(root);
        if (doc.Version != 1)
            throw new WorkflowParseException($"Unsupported workflow version: {doc.Version}");

        // name
        doc.Name = root.GetScalar("name");

        // meta
        if (root.HasKey("meta"))
            doc.Meta = root.GetStringMap("meta");

        // skill metadata (singular preferred; plural accepted for compatibility)
        var skillNode = root.GetMapping("skill") ?? root.GetMapping("skills");
        if (skillNode != null)
            doc.Skill = ParseWorkflowSkill(skillNode);

        // functions (global WFScript)
        doc.Functions = root.GetScalar("functions");

        // exports
        if (root.HasKey("exports"))
            doc.Exports = root.GetStringList("exports");

        // entrypoint
        doc.Entrypoint = root.GetScalar("entrypoint");

        // workflows
        var workflowsNode = root.GetMapping("workflows")
            ?? throw new WorkflowParseException("Missing required field 'workflows'");

        foreach (var child in workflowsNode.Children)
        {
            var name = (child.Key as YamlScalarNode)?.Value
                ?? throw new WorkflowParseException("Workflow name must be a string");
            var wfNode = child.Value as YamlMappingNode
                ?? throw new WorkflowParseException($"Workflow '{name}' must be a mapping");
            doc.Workflows[name] = ParseWorkflowDef(wfNode, name);
        }

        // Default entrypoint
        if (doc.Entrypoint == null && doc.Workflows.ContainsKey("main"))
            doc.Entrypoint = "main";

        return doc;
    }

    public static WorkflowSkillDef? ParseSkill(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
            return null;

        var root = stream.Documents[0].RootNode as YamlMappingNode;
        var skillNode = root?.GetMapping("skill") ?? root?.GetMapping("skills");
        return skillNode == null ? null : ParseWorkflowSkill(skillNode);
    }

    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "version", "name", "meta", "skill", "skills", "functions", "exports", "entrypoint", "workflows"
    };

    private static readonly HashSet<string> WorkflowFields = new(StringComparer.Ordinal)
    {
        "inputs", "skill", "skills", "functions", "steps", "outputs"
    };

    private static readonly HashSet<string> StepFields = new(StringComparer.Ordinal)
    {
        "id", "type", "if", "input", "output", "output_schema", "retry", "on_error",
        "steps", "branches", "cases", "expr", "default", "item_var", "index_var"
    };

    private static readonly HashSet<string> RetryFields = new(StringComparer.Ordinal)
    {
        "max", "backoff_ms", "backoff_mult", "jitter_ms"
    };

    private static readonly HashSet<string> OnErrorFields = new(StringComparer.Ordinal)
    {
        "cases"
    };

    private static readonly HashSet<string> OnErrorCaseFields = new(StringComparer.Ordinal)
    {
        "if", "action", "set_output", "retry"
    };

    private static readonly HashSet<string> BranchFields = new(StringComparer.Ordinal)
    {
        "id", "name", "steps"
    };

    private static readonly HashSet<string> SwitchCaseFields = new(StringComparer.Ordinal)
    {
        "value", "when", "steps"
    };

    private static void CollectUnknownYamlFields(YamlMappingNode root, List<UnknownYamlField> unknownFields)
    {
        AddUnknownFields(root, "$", RootFields, unknownFields);

        var workflowsNode = root.GetMapping("workflows");
        if (workflowsNode == null)
            return;

        foreach (var child in workflowsNode.Children)
        {
            if (child.Key is not YamlScalarNode workflowNameNode || child.Value is not YamlMappingNode workflowNode)
                continue;

            var workflowName = workflowNameNode.Value ?? "";
            var workflowPath = $"workflows.{workflowName}";
            AddUnknownFields(workflowNode, workflowPath, WorkflowFields, unknownFields);

            if (workflowNode.GetSequence("steps") is { } stepsNode)
                CollectUnknownStepListFields(stepsNode, $"{workflowPath}.steps", unknownFields);
        }
    }

    private static void CollectUnknownStepListFields(
        YamlSequenceNode stepsNode,
        string path,
        List<UnknownYamlField> unknownFields)
    {
        for (var i = 0; i < stepsNode.Children.Count; i++)
        {
            if (stepsNode.Children[i] is YamlMappingNode stepNode)
                CollectUnknownStepFields(stepNode, $"{path}[{i}]", unknownFields);
        }
    }

    private static void CollectUnknownStepFields(
        YamlMappingNode stepNode,
        string path,
        List<UnknownYamlField> unknownFields)
    {
        AddUnknownFields(stepNode, path, StepFields, unknownFields);

        if (stepNode.GetMapping("retry") is { } retryNode)
            AddUnknownFields(retryNode, $"{path}.retry", RetryFields, unknownFields);

        if (stepNode.GetMapping("on_error") is { } onErrorNode)
            CollectUnknownOnErrorFields(onErrorNode, $"{path}.on_error", unknownFields);

        if (stepNode.GetSequence("steps") is { } childSteps)
            CollectUnknownStepListFields(childSteps, $"{path}.steps", unknownFields);

        if (stepNode.GetSequence("branches") is { } branches)
        {
            for (var i = 0; i < branches.Children.Count; i++)
            {
                if (branches.Children[i] is not YamlMappingNode branchNode)
                    continue;

                var branchPath = $"{path}.branches[{i}]";
                AddUnknownFields(branchNode, branchPath, BranchFields, unknownFields);
                if (branchNode.GetSequence("steps") is { } branchSteps)
                    CollectUnknownStepListFields(branchSteps, $"{branchPath}.steps", unknownFields);
            }
        }

        if (stepNode.GetSequence("cases") is { } cases)
        {
            for (var i = 0; i < cases.Children.Count; i++)
            {
                if (cases.Children[i] is not YamlMappingNode caseNode)
                    continue;

                var casePath = $"{path}.cases[{i}]";
                AddUnknownFields(caseNode, casePath, SwitchCaseFields, unknownFields);
                if (caseNode.GetSequence("steps") is { } caseSteps)
                    CollectUnknownStepListFields(caseSteps, $"{casePath}.steps", unknownFields);
            }
        }

        if (stepNode.GetSequence("default") is { } defaultSteps)
            CollectUnknownStepListFields(defaultSteps, $"{path}.default", unknownFields);
    }

    private static void CollectUnknownOnErrorFields(
        YamlMappingNode onErrorNode,
        string path,
        List<UnknownYamlField> unknownFields)
    {
        AddUnknownFields(onErrorNode, path, OnErrorFields, unknownFields);

        if (onErrorNode.GetSequence("cases") is not { } cases)
            return;

        for (var i = 0; i < cases.Children.Count; i++)
        {
            if (cases.Children[i] is not YamlMappingNode caseNode)
                continue;

            var casePath = $"{path}.cases[{i}]";
            AddUnknownFields(caseNode, casePath, OnErrorCaseFields, unknownFields);
            if (caseNode.GetMapping("retry") is { } retryNode)
                AddUnknownFields(retryNode, $"{casePath}.retry", RetryFields, unknownFields);
        }
    }

    private static void AddUnknownFields(
        YamlMappingNode node,
        string path,
        IReadOnlySet<string> allowedFields,
        List<UnknownYamlField> unknownFields)
    {
        foreach (var child in node.Children)
        {
            if (child.Key is not YamlScalarNode keyNode)
                continue;

            var field = keyNode.Value ?? "";
            if (allowedFields.Contains(field))
                continue;

            unknownFields.Add(new UnknownYamlField
            {
                Field = field,
                Path = path == "$" ? field : $"{path}.{field}",
                AllowedFields = allowedFields.OrderBy(static allowed => allowed, StringComparer.Ordinal).ToArray()
            });
        }
    }

    private static int ParseWorkflowVersion(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("version"), out var node))
            throw new WorkflowParseException("Missing required field 'version'");

        if (node is not YamlScalarNode scalar)
            throw new WorkflowParseException("Unsupported workflow version: non-scalar");

        var value = scalar.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new WorkflowParseException("Unsupported workflow version: empty");

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intVersion))
            return intVersion;

        if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var decimalVersion)
            && decimalVersion == decimal.One)
            return 1;

        throw new WorkflowParseException($"Unsupported workflow version: {value}");
    }

    private static WorkflowDef ParseWorkflowDef(YamlMappingNode node, string name)
    {
        var wf = new WorkflowDef();

        // Optional per-workflow skill metadata (used by routers/catalogs).
        var skillNode = node.GetMapping("skill") ?? node.GetMapping("skills");
        if (skillNode != null)
            wf.Skill = ParseWorkflowSkill(skillNode);

        // inputs
        var inputsNode = node.GetMapping("inputs");
        if (inputsNode != null)
        {
            wf.Inputs = new Dictionary<string, InputDef>();
            foreach (var child in inputsNode.Children)
            {
                var key = (child.Key as YamlScalarNode)?.Value ?? "";
                wf.Inputs[key] = ParseInputDef(child.Value);
            }
        }

        // functions (local WFScript)
        wf.Functions = node.GetScalar("functions");

        // steps
        var stepsNode = node.GetSequence("steps")
            ?? throw new WorkflowParseException($"Workflow '{name}' missing required 'steps'");
        wf.Steps = ParseStepList(stepsNode);

        // outputs
        if (node.HasKey("outputs"))
        {
            var outputsNode = node.GetMapping("outputs");
            if (outputsNode != null)
            {
                wf.Outputs = new Dictionary<string, OutputDef>();
                foreach (var child in outputsNode.Children)
                {
                    var key = (child.Key as YamlScalarNode)?.Value ?? "";
                    wf.Outputs[key] = ParseOutputDef(child.Value);
                }
            }
        }

        return wf;
    }

    private static WorkflowSkillDef ParseWorkflowSkill(YamlMappingNode node)
    {
        var skill = new WorkflowSkillDef
        {
            Description = node.GetScalar("description")
        };

        var tags = node.GetStringList("tags");
        if (tags.Count > 0)
            skill.Tags = tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)).ToList();

        var inputsNode = node.GetMapping("inputs");
        if (inputsNode != null)
        {
            skill.Inputs = new Dictionary<string, InputDef>();
            foreach (var child in inputsNode.Children)
            {
                var key = (child.Key as YamlScalarNode)?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(key))
                    skill.Inputs[key] = ParseInputDef(child.Value);
            }
        }

        var outputsNode = node.GetMapping("outputs");
        if (outputsNode != null)
        {
            skill.Outputs = new Dictionary<string, OutputDef>();
            foreach (var child in outputsNode.Children)
            {
                var key = (child.Key as YamlScalarNode)?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(key))
                    skill.Outputs[key] = ParseOutputDef(child.Value);
            }
        }

        return skill;
    }

    private static InputDef ParseInputDef(YamlNode node)
    {
        if (node is YamlScalarNode scalar)
            return new InputDef { Type = scalar.Value ?? "any" };

        if (node is YamlMappingNode map)
        {
            var required = map.Children.TryGetValue(new YamlScalarNode("required"), out var requiredNode)
                && requiredNode is YamlScalarNode
                    ? map.GetBool("required")
                    : null;

            var def = new InputDef
            {
                Type = map.GetScalar("type") ?? "any",
                Required = required ?? true,
                Default = map.GetScalar("default"),
                Description = map.GetScalar("description")
            };

            // Array element type
            var itemsNode = map.Children
                .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "items").Value;
            if (itemsNode != null)
                def.Items = ParseInputDef(itemsNode);

            // Object property schemas
            var propsNode = map.GetMapping("properties");
            if (propsNode != null)
            {
                def.Properties = new Dictionary<string, InputDef>();
                foreach (var child in propsNode.Children)
                {
                    var key = (child.Key as YamlScalarNode)?.Value ?? "";
                    def.Properties[key] = ParseInputDef(child.Value);
                }
            }

            // Dictionary value type / extra object properties
            var additionalNode = map.Children
                .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "additional_properties").Value;
            if (additionalNode != null)
                def.AdditionalProperties = ParseInputDef(additionalNode);

            // Required property names (for objects). "required_properties" is the
            // generator-facing DSL key; "required" list remains accepted for
            // backward compatibility with older workflow files.
            var requiredProperties = map.GetStringList("required_properties");
            if (requiredProperties.Count == 0)
                requiredProperties = map.GetStringList("required");
            def.RequiredProperties = requiredProperties.Count > 0 ? requiredProperties : null;

            return def;
        }

        return new InputDef();
    }

    private static OutputDef ParseOutputDef(YamlNode node)
    {
        // Short form: scalar expression string → OutputDef { Expr = expr, Type = "any" }
        if (node is YamlScalarNode scalar)
            return OutputDef.FromExpr(scalar.Value ?? "");

        if (node is YamlMappingNode map)
        {
            var hasExpr = map.HasKey("expr");
            var hasType = map.HasKey("type");

            // Long form with "expr" key — typed output definition
            if (hasExpr)
            {
                var def = new OutputDef
                {
                    Expr = map.GetScalar("expr") ?? "",
                    Type = map.GetScalar("type") ?? "any",
                    Description = map.GetScalar("description")
                };

                // Array element type
                var itemsNode = map.Children
                    .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "items").Value;
                if (itemsNode != null)
                    def.Items = ParseOutputDef(itemsNode);

                // Object property schemas
                var propsNode = map.GetMapping("properties");
                if (propsNode != null)
                {
                    def.Properties = new Dictionary<string, OutputDef>();
                    foreach (var child in propsNode.Children)
                    {
                        var key = (child.Key as YamlScalarNode)?.Value ?? "";
                        def.Properties[key] = ParseOutputDef(child.Value);
                    }
                }

                // Dictionary value type / extra object properties
                var additionalNode = map.Children
                    .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "additional_properties").Value;
                if (additionalNode != null)
                    def.AdditionalProperties = ParseOutputDef(additionalNode);

                // Required property names (for objects). "required_properties" is
                // preferred; "required" list is accepted for older workflow files.
                var requiredProperties = map.GetStringList("required_properties");
                if (requiredProperties.Count == 0)
                    requiredProperties = map.GetStringList("required");
                def.RequiredProperties = requiredProperties.Count > 0 ? requiredProperties : null;

                return def;
            }

            // Type-only schema (no expr, but has type) — used in nested items/properties
            if (hasType)
            {
                var def = new OutputDef
                {
                    Type = map.GetScalar("type") ?? "any",
                    Description = map.GetScalar("description")
                };

                var itemsNode = map.Children
                    .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "items").Value;
                if (itemsNode != null)
                    def.Items = ParseOutputDef(itemsNode);

                var propsNode = map.GetMapping("properties");
                if (propsNode != null)
                {
                    def.Properties = new Dictionary<string, OutputDef>();
                    foreach (var child in propsNode.Children)
                    {
                        var key = (child.Key as YamlScalarNode)?.Value ?? "";
                        def.Properties[key] = ParseOutputDef(child.Value);
                    }
                }

                var additionalNode = map.Children
                    .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "additional_properties").Value;
                if (additionalNode != null)
                    def.AdditionalProperties = ParseOutputDef(additionalNode);

                var requiredProperties = map.GetStringList("required_properties");
                if (requiredProperties.Count == 0)
                    requiredProperties = map.GetStringList("required");
                def.RequiredProperties = requiredProperties.Count > 0 ? requiredProperties : null;

                return def;
            }

            // Backward-compat: nested mapping without "expr" or "type" — build a composite object output
            // e.g. { model: "${...}", attempts: "${...}" }
            return new OutputDef
            {
                Expr = "",
                Type = "object",
                Properties = ParseNestedOutputExpressions(map)
            };
        }

        return OutputDef.FromExpr("");
    }

    /// <summary>
    /// Handles backward-compatible nested mapping outputs (no "expr" key) like:
    /// <code>meta: { model: "${...}", attempts: "${...}" }</code>
    /// Each child becomes an OutputDef with just its expression.
    /// </summary>
    private static Dictionary<string, OutputDef> ParseNestedOutputExpressions(YamlMappingNode map)
    {
        var props = new Dictionary<string, OutputDef>();
        foreach (var child in map.Children)
        {
            var key = (child.Key as YamlScalarNode)?.Value ?? "";
            props[key] = ParseOutputDef(child.Value);
        }
        return props;
    }

    private static List<StepDef> ParseStepList(YamlSequenceNode seq)
    {
        var steps = new List<StepDef>();
        foreach (var item in seq.Children)
        {
            if (item is YamlMappingNode stepNode)
                steps.Add(ParseStep(stepNode));
        }
        return steps;
    }

    private static StepDef ParseStep(YamlMappingNode node)
    {
        var step = new StepDef
        {
            Id = node.GetScalar("id") ?? throw new WorkflowParseException("Step missing 'id'"),
            Type = node.GetScalar("type") ?? throw new WorkflowParseException("Step missing 'type'"),
            If = node.GetScalar("if"),
            Output = node.GetScalar("output"),
            ItemVar = node.GetScalar("item_var"),
            IndexVar = node.GetScalar("index_var"),
            Expr = node.GetScalar("expr"),
        };

        // input
        if (node.HasKey("input"))
        {
            var inputNode = node.Children[new YamlScalarNode("input")];
            step.Input = YamlToJson(inputNode);
        }

        if (node.HasKey("output_schema"))
        {
            var outputSchemaNode = node.Children[new YamlScalarNode("output_schema")];
            step.OutputSchema = YamlToJson(outputSchemaNode);
        }

        // retry
        var retryNode = node.GetMapping("retry");
        if (retryNode != null)
        {
            step.Retry = new RetryPolicy
            {
                Max = retryNode.GetInt("max") ?? 1,
                BackoffMs = retryNode.GetInt("backoff_ms") ?? 1000,
                BackoffMult = retryNode.GetDouble("backoff_mult") ?? 2.0,
                JitterMs = retryNode.GetInt("jitter_ms") ?? 0
            };
        }

        // on_error
        var onErrorNode = node.GetMapping("on_error");
        if (onErrorNode != null)
        {
            step.OnError = ParseOnError(onErrorNode);
        }

        // steps (for sequence, loop, etc.)
        var stepsSeq = node.GetSequence("steps");
        if (stepsSeq != null)
            step.Steps = ParseStepList(stepsSeq);

        // branches (for parallel)
        var branchesSeq = node.GetSequence("branches");
        if (branchesSeq != null)
        {
            step.Branches = new List<BranchDef>();
            foreach (var b in branchesSeq.Children)
            {
                if (b is YamlMappingNode branchMap)
                {
                    var branch = new BranchDef();
                    var branchSteps = branchMap.GetSequence("steps");
                    if (branchSteps != null)
                        branch.Steps = ParseStepList(branchSteps);
                    step.Branches.Add(branch);
                }
            }
        }

        // cases (for switch)
        var casesSeq = node.GetSequence("cases");
        if (casesSeq != null)
        {
            step.Cases = new List<SwitchCaseDef>();
            foreach (var c in casesSeq.Children)
            {
                if (c is YamlMappingNode caseMap)
                {
                    step.Cases.Add(new SwitchCaseDef
                    {
                        Value = caseMap.GetScalar("value"),
                        When = caseMap.GetScalar("when"),
                        Steps = ParseStepList(caseMap.GetSequence("steps") ?? new YamlSequenceNode())
                    });
                }
            }
        }

        // default (for switch)
        var defaultSeq = node.GetSequence("default");
        if (defaultSeq != null)
            step.Default = ParseStepList(defaultSeq);

        return step;
    }

    private static OnErrorDef ParseOnError(YamlMappingNode node)
    {
        var def = new OnErrorDef();
        var casesSeq = node.GetSequence("cases");
        if (casesSeq != null)
        {
            foreach (var c in casesSeq.Children)
            {
                if (c is YamlMappingNode caseMap)
                {
                    var setOutputNode = caseMap.Children
                        .FirstOrDefault(child => (child.Key as YamlScalarNode)?.Value == "set_output")
                        .Value;

                    def.Cases.Add(new OnErrorCase
                    {
                        If = caseMap.GetScalar("if"),
                        Action = caseMap.GetScalar("action") ?? "stop",
                        SetOutput = setOutputNode is null ? null : YamlToJson(setOutputNode),
                        Retry = caseMap.HasKey("retry") ? new RetryPolicy
                        {
                            Max = caseMap.GetMapping("retry")?.GetInt("max") ?? 1,
                            BackoffMs = caseMap.GetMapping("retry")?.GetInt("backoff_ms") ?? 1000,
                            BackoffMult = caseMap.GetMapping("retry")?.GetDouble("backoff_mult") ?? 2.0,
                            JitterMs = caseMap.GetMapping("retry")?.GetInt("jitter_ms") ?? 0
                        } : null
                    });
                }
            }
        }
        return def;
    }

    /// <summary>
    /// Convert a YamlNode to a System.Text.Json.Nodes.JsonNode.
    /// </summary>
    public static JsonNode? YamlToJson(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => ParseScalarToJson(scalar),
            YamlMappingNode map => YamlMapToJson(map),
            YamlSequenceNode seq => YamlSeqToJson(seq),
            _ => null
        };
    }

    private static JsonNode? ParseScalarToJson(YamlScalarNode scalar)
    {
        var val = scalar.Value;
        if (val == null) return null;
        if (!ShouldInferScalarType(scalar)) return JsonValue.Create(val);
        if (val == "null" || val == "~") return null;
        if (val == "true" || val == "True") return JsonValue.Create(true);
        if (val == "false" || val == "False") return JsonValue.Create(false);
        if (int.TryParse(val, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
            return JsonValue.Create(i);
        if (double.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(val);
    }

    private static bool ShouldInferScalarType(YamlScalarNode scalar) =>
        scalar.Style is ScalarStyle.Any or ScalarStyle.Plain;

    private static JsonObject YamlMapToJson(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var kv in map.Children)
        {
            var key = (kv.Key as YamlScalarNode)?.Value ?? "";
            obj[key] = YamlToJson(kv.Value);
        }
        return obj;
    }

    private static JsonArray YamlSeqToJson(YamlSequenceNode seq)
    {
        var arr = new JsonArray();
        foreach (var item in seq.Children)
            arr.Add(YamlToJson(item));
        return arr;
    }
}
