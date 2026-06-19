using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed class WorkflowPlanManifest
{
    public string Name { get; set; } = "generated-workflow";
    public string Description { get; set; } = "";
    public JsonObject Inputs { get; set; } = new();
    public JsonObject Outputs { get; set; } = new();
    public List<WorkflowPlanSubPlan> SubPlans { get; set; } = new();
    public WorkflowPlanAlgorithmNode Algorithm { get; set; } = new() { Type = "sequence" };
    public string RawYaml { get; set; } = "";
}

public sealed class WorkflowPlanSubPlan
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Responsibility { get; set; } = "";
    public JsonObject Inputs { get; set; } = new();
    public JsonObject Outputs { get; set; } = new();
    public JsonObject Constraints { get; set; } = new();
    public WorkflowPlanAlgorithmNode? Algorithm { get; set; }
}

public sealed class WorkflowPlanAlgorithmNode
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public string? Task { get; set; }
    public string? Plan { get; set; }
    public JsonObject? Args { get; set; }
    public string? Items { get; set; }
    public string? ItemVar { get; set; }
    public string? IndexVar { get; set; }
    public string? Expr { get; set; }
    public JsonObject? Outputs { get; set; }
    public List<WorkflowPlanAlgorithmNode> Steps { get; set; } = new();
    public List<WorkflowPlanAlgorithmNode> Branches { get; set; } = new();
    public List<WorkflowPlanSwitchCase> Cases { get; set; } = new();
    public WorkflowPlanAlgorithmNode? Default { get; set; }
}

public sealed class WorkflowPlanSwitchCase
{
    public string? Value { get; set; }
    public string? When { get; set; }
    public WorkflowPlanAlgorithmNode Step { get; set; } = new() { Type = "sequence" };
}

public static class WorkflowPlanManifestParser
{
    public static WorkflowPlanManifest Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Split manifest is empty.");

        var root = stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Split manifest root must be a mapping.");

        var manifest = new WorkflowPlanManifest
        {
            Name = root.GetScalar("name") ?? "generated-workflow",
            Description = root.GetScalar("description") ?? "",
            Inputs = ToJsonObject(root.GetMapping("inputs")),
            Outputs = ToJsonObject(root.GetMapping("outputs")),
            RawYaml = yaml
        };

        var subPlansNode = root.GetSequence("subplans") ?? root.GetSequence("sub_plans")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Split manifest requires 'subplans'.");

        foreach (var child in subPlansNode.Children.OfType<YamlMappingNode>())
            manifest.SubPlans.Add(ParseSubPlan(child));

        var algorithmNode = root.GetMapping("algorithm")
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Split manifest requires 'algorithm'.");
        manifest.Algorithm = ParseAlgorithmNode(algorithmNode);

        return manifest;
    }

    private static WorkflowPlanSubPlan ParseSubPlan(YamlMappingNode node)
    {
        return new WorkflowPlanSubPlan
        {
            Id = node.GetScalar("id") ?? "",
            Path = node.GetScalar("path") ?? "",
            Responsibility = node.GetScalar("responsibility") ?? node.GetScalar("prompt") ?? "",
            Inputs = ToJsonObject(node.GetMapping("inputs")),
            Outputs = ToJsonObject(node.GetMapping("outputs")),
            Constraints = ToJsonObject(node.GetMapping("constraints")),
            Algorithm = node.GetMapping("algorithm") is { } algorithm ? ParseAlgorithmNode(algorithm) : null
        };
    }

    private static WorkflowPlanAlgorithmNode ParseAlgorithmNode(YamlMappingNode node)
    {
        var type = node.GetScalar("type") ?? node.GetScalar("kind") ?? "";
        var task = node.GetScalar("task") ?? node.GetScalar("responsibility") ?? node.GetScalar("description");
        var plan = node.GetScalar("plan") ?? node.GetScalar("subplan");
        var items = node.GetScalar("items") ?? node.GetScalar("over");
        type = InferNodeType(
            type,
            task,
            plan,
            items,
            node.GetSequence("steps") is not null,
            node.GetSequence("branches") is not null,
            node.GetSequence("cases") is not null,
            node.GetMapping("default") is not null || node.GetSequence("default") is not null,
            node.GetMapping("outputs") is not null);
        var parsed = new WorkflowPlanAlgorithmNode
        {
            Type = NormalizeNodeType(type),
            Id = node.GetScalar("id"),
            Task = task,
            Plan = plan,
            Args = ToJsonObjectOrNull(node.GetMapping("args")),
            Items = items,
            ItemVar = node.GetScalar("item_var"),
            IndexVar = node.GetScalar("index_var"),
            Expr = node.GetScalar("expr"),
            Outputs = ToJsonObjectOrNull(node.GetMapping("outputs"))
        };

        if (node.GetSequence("steps") is { } steps)
        {
            foreach (var child in steps.Children.OfType<YamlMappingNode>())
                parsed.Steps.Add(ParseAlgorithmNode(child));
        }

        if (node.GetSequence("branches") is { } branches)
        {
            foreach (var child in branches.Children.OfType<YamlMappingNode>())
            {
                var branchSteps = child.GetSequence("steps");
                if (branchSteps != null && string.IsNullOrWhiteSpace(child.GetScalar("type")))
                {
                    var sequence = new WorkflowPlanAlgorithmNode
                    {
                        Type = "sequence",
                        Id = child.GetScalar("id")
                    };
                    foreach (var step in branchSteps.Children.OfType<YamlMappingNode>())
                        sequence.Steps.Add(ParseAlgorithmNode(step));
                    parsed.Branches.Add(sequence);
                }
                else
                {
                    parsed.Branches.Add(ParseAlgorithmNode(child));
                }
            }
        }

        if (node.GetSequence("cases") is { } cases)
        {
            foreach (var child in cases.Children.OfType<YamlMappingNode>())
            {
                var stepNode = child.GetMapping("step");
                var stepsNode = child.GetSequence("steps");
                WorkflowPlanAlgorithmNode caseStep;
                if (stepNode != null)
                {
                    caseStep = ParseAlgorithmNode(stepNode);
                }
                else
                {
                    caseStep = new WorkflowPlanAlgorithmNode
                    {
                        Type = "sequence",
                        Outputs = ToJsonObjectOrNull(child.GetMapping("outputs"))
                    };
                    if (stepsNode != null)
                    {
                        foreach (var step in stepsNode.Children.OfType<YamlMappingNode>())
                            caseStep.Steps.Add(ParseAlgorithmNode(step));
                    }
                }

                parsed.Cases.Add(new WorkflowPlanSwitchCase
                {
                    Value = child.GetScalar("value"),
                    When = child.GetScalar("when"),
                    Step = caseStep
                });
            }
        }

        if (node.GetMapping("default") is { } defaultNode)
            parsed.Default = ParseAlgorithmNode(defaultNode);
        else if (node.GetSequence("default") is { } defaultSteps)
        {
            parsed.Default = new WorkflowPlanAlgorithmNode { Type = "sequence" };
            foreach (var step in defaultSteps.Children.OfType<YamlMappingNode>())
                parsed.Default.Steps.Add(ParseAlgorithmNode(step));
        }

        return parsed;
    }

    private static string InferNodeType(
        string explicitType,
        string? task,
        string? plan,
        string? items,
        bool hasSteps,
        bool hasBranches,
        bool hasCases,
        bool hasDefault,
        bool hasOutputs)
    {
        if (!string.IsNullOrWhiteSpace(explicitType))
            return explicitType;
        if (!string.IsNullOrWhiteSpace(plan))
            return "workflow.call";
        if (!string.IsNullOrWhiteSpace(items))
            return "foreach.sequential";
        if (hasCases || hasDefault)
            return "switch";
        if (hasBranches)
            return "parallel";
        if (hasSteps)
            return "sequence";
        if (!string.IsNullOrWhiteSpace(task) || hasOutputs)
            return "task";
        return "";
    }

    private static string NormalizeNodeType(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "foreach sequential" or "foreach.sequential" or "foreach_sequential" => "foreach.sequential",
            "foreach parallel" or "foreach.parallel" or "foreach_parallel" => "foreach.parallel",
            "inline" or "inline.task" or "local.task" or "step" or "task" => "task",
            "workflow.call" or "call" => "workflow.call",
            var value => value
        };
    }

    private static JsonObject ToJsonObject(YamlMappingNode? node)
        => ToJsonObjectOrNull(node) ?? new JsonObject();

    private static JsonObject? ToJsonObjectOrNull(YamlMappingNode? node)
        => node == null ? null : WorkflowParser.YamlToJson(node) as JsonObject ?? new JsonObject();
}

public static class WorkflowPlanManifestNormalizer
{
    public static WorkflowPlanManifest Normalize(
        WorkflowPlanManifest manifest,
        string? requestedWorkflowName,
        string? requestedDescription)
    {
        var workflowName = SanitizePathSegment(
            string.IsNullOrWhiteSpace(requestedWorkflowName) ? manifest.Name : requestedWorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
            workflowName = "generated-workflow";

        manifest.Name = workflowName;
        if (!string.IsNullOrWhiteSpace(requestedDescription))
            manifest.Description = requestedDescription.Trim();
        else if (string.IsNullOrWhiteSpace(manifest.Description))
            manifest.Description = $"Handles the requested {workflowName} workflow.";
        else
            manifest.Description = manifest.Description.Trim();

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subPlan in manifest.SubPlans)
        {
            var subWorkflowName = SanitizePathSegment(subPlan.Id);
            if (string.IsNullOrWhiteSpace(subWorkflowName))
                subWorkflowName = "subworkflow";

            var uniqueSubWorkflowName = subWorkflowName;
            var suffix = 2;
            var path = $"./{workflowName}/{uniqueSubWorkflowName}.yaml";
            while (!usedPaths.Add(path))
            {
                uniqueSubWorkflowName = $"{subWorkflowName}-{suffix++}";
                path = $"./{workflowName}/{uniqueSubWorkflowName}.yaml";
            }

            subPlan.Path = path;
            if (string.IsNullOrWhiteSpace(subPlan.Responsibility))
                subPlan.Responsibility = $"Handle the {uniqueSubWorkflowName} part of the workflow.";
            else
                subPlan.Responsibility = subPlan.Responsibility.Trim();

            subPlan.Inputs = new JsonObject();
            subPlan.Outputs = new JsonObject();
            subPlan.Constraints = new JsonObject();

            if (subPlan.Algorithm != null)
            {
                NormalizeAlgorithmNode(subPlan.Algorithm);
                if (IsEmptyPlaceholderAlgorithm(subPlan.Algorithm))
                    subPlan.Algorithm = null;
            }
        }

        NormalizeAlgorithmNode(manifest.Algorithm);
        return manifest;
    }

    private static void NormalizeAlgorithmNode(WorkflowPlanAlgorithmNode node)
    {
        node.Args = null;

        NormalizeChildren(node.Steps);
        NormalizeChildren(node.Branches);

        for (var i = node.Cases.Count - 1; i >= 0; i--)
        {
            var @case = node.Cases[i];
            NormalizeAlgorithmNode(@case.Step);
            if (IsEmptyPlaceholderAlgorithm(@case.Step))
                node.Cases.RemoveAt(i);
        }

        if (node.Default != null)
        {
            NormalizeAlgorithmNode(node.Default);
            if (IsEmptyPlaceholderAlgorithm(node.Default))
                node.Default = null;
        }
    }

    private static void NormalizeChildren(List<WorkflowPlanAlgorithmNode> children)
    {
        for (var i = children.Count - 1; i >= 0; i--)
        {
            NormalizeAlgorithmNode(children[i]);
            if (IsEmptyPlaceholderAlgorithm(children[i]))
                children.RemoveAt(i);
        }
    }

    private static bool IsEmptyPlaceholderAlgorithm(WorkflowPlanAlgorithmNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Type))
        {
            return string.IsNullOrWhiteSpace(node.Id)
                && string.IsNullOrWhiteSpace(node.Task)
                && string.IsNullOrWhiteSpace(node.Plan)
                && string.IsNullOrWhiteSpace(node.Items)
                && string.IsNullOrWhiteSpace(node.ItemVar)
                && string.IsNullOrWhiteSpace(node.IndexVar)
                && string.IsNullOrWhiteSpace(node.Expr)
                && (node.Outputs == null || node.Outputs.Count == 0)
                && node.Steps.Count == 0
                && node.Branches.Count == 0
                && node.Cases.Count == 0
                && node.Default == null;
        }

        return node.Type switch
        {
            "sequence" => node.Steps.Count == 0 && (node.Outputs == null || node.Outputs.Count == 0),
            "parallel" => node.Branches.Count == 0,
            "foreach.sequential" or "foreach.parallel" => node.Steps.Count == 0,
            "switch" => node.Cases.Count == 0 && node.Default == null,
            "task" => string.IsNullOrWhiteSpace(node.Task) && (node.Outputs == null || node.Outputs.Count == 0),
            "workflow.call" => string.IsNullOrWhiteSpace(node.Plan),
            _ => false
        };
    }

    private static string SanitizePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder();
        var lastWasDash = false;
        foreach (var c in value.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }
}

public sealed class WorkflowPlanManifestValidationException : Exception
{
    public WorkflowPlanManifestValidationException(IReadOnlyList<string> errors)
        : base("Split manifest validation failed: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public static class WorkflowPlanManifestDependencyPlanner
{
    public static IReadOnlyList<IReadOnlyList<WorkflowPlanSubPlan>> BuildGenerationBatches(WorkflowPlanManifest manifest)
    {
        var byId = manifest.SubPlans.ToDictionary(static plan => plan.Id, StringComparer.Ordinal);
        var dependenciesById = manifest.SubPlans.ToDictionary(
            static plan => plan.Id,
            static plan => GetSubPlanDependencies(plan),
            StringComparer.Ordinal);
        var generated = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new List<WorkflowPlanSubPlan>(manifest.SubPlans);
        var batches = new List<IReadOnlyList<WorkflowPlanSubPlan>>();

        while (remaining.Count > 0)
        {
            var batch = remaining
                .Where(plan => dependenciesById[plan.Id].All(generated.Contains))
                .ToArray();

            if (batch.Length == 0)
            {
                var unresolved = remaining
                    .Select(plan => $"{plan.Id} -> [{string.Join(", ", dependenciesById[plan.Id].Where(dep => !generated.Contains(dep)))}]");
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "Could not resolve split sub-plan dependency order: " + string.Join("; ", unresolved));
            }

            foreach (var plan in batch)
            {
                foreach (var dependency in dependenciesById[plan.Id])
                {
                    if (!byId.ContainsKey(dependency))
                        throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Sub-plan '{plan.Id}' depends on undeclared sub-plan '{dependency}'.");
                }

                generated.Add(plan.Id);
                remaining.Remove(plan);
            }

            batches.Add(batch);
        }

        return batches;
    }

    public static HashSet<string> GetSubPlanDependencies(WorkflowPlanSubPlan subPlan)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        if (subPlan.Algorithm != null)
            CollectWorkflowCallPlans(subPlan.Algorithm, dependencies);
        return dependencies;
    }

    public static void CollectWorkflowCallPlans(WorkflowPlanAlgorithmNode node, HashSet<string> plans)
    {
        if (node.Type == "workflow.call" && !string.IsNullOrWhiteSpace(node.Plan))
            plans.Add(node.Plan);

        foreach (var child in node.Steps)
            CollectWorkflowCallPlans(child, plans);
        foreach (var branch in node.Branches)
            CollectWorkflowCallPlans(branch, plans);
        foreach (var @case in node.Cases)
            CollectWorkflowCallPlans(@case.Step, plans);
        if (node.Default != null)
            CollectWorkflowCallPlans(node.Default, plans);
    }
}

public static class WorkflowPlanManifestValidator
{
    private static readonly HashSet<string> KnownAlgorithmTypes = new(StringComparer.Ordinal)
    {
        "sequence",
        "parallel",
        "foreach.sequential",
        "foreach.parallel",
        "switch",
        "task",
        "workflow.call"
    };

    public static void Validate(WorkflowPlanManifest manifest)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("manifest name is required");
        if (string.IsNullOrWhiteSpace(manifest.Description))
            errors.Add("manifest description is required");

        var subPlanIds = new HashSet<string>(StringComparer.Ordinal);
        var subPlanPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subPlan in manifest.SubPlans)
        {
            if (string.IsNullOrWhiteSpace(subPlan.Id))
                errors.Add("subplan id is required");
            else if (!subPlanIds.Add(subPlan.Id))
                errors.Add($"duplicate subplan id '{subPlan.Id}'");

            if (string.IsNullOrWhiteSpace(subPlan.Responsibility))
                errors.Add($"subplan '{subPlan.Id}' responsibility is required");
            if (!IsSafeRelativePath(subPlan.Path))
                errors.Add($"subplan '{subPlan.Id}' path must be safe and relative");
            else if (!HasCanonicalSubWorkflowPath(manifest.Name, subPlan.Path))
                errors.Add($"subplan '{subPlan.Id}' path must match './{manifest.Name}/<subworkflow>.yaml'");
            else if (!subPlanPaths.Add(subPlan.Path.Replace('\\', '/')))
                errors.Add($"duplicate subplan path '{subPlan.Path}'");
        }

        ValidateAlgorithmNode(manifest.Algorithm, subPlanIds, new HashSet<string>(StringComparer.Ordinal), errors, "algorithm");

        var subPlanDependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var subPlan in manifest.SubPlans)
        {
            if (subPlan.Algorithm == null)
                continue;

            var path = $"subplans.{subPlan.Id}.algorithm";
            ValidateAlgorithmNode(subPlan.Algorithm, subPlanIds, new HashSet<string>(StringComparer.Ordinal), errors, path);

            var dependencies = WorkflowPlanManifestDependencyPlanner.GetSubPlanDependencies(subPlan);
            subPlanDependencies[subPlan.Id] = dependencies;
            if (dependencies.Contains(subPlan.Id))
                errors.Add($"{path}: subplan '{subPlan.Id}' cannot call itself");
        }

        ValidateSubPlanDependencyGraph(subPlanDependencies, errors);

        if (errors.Count > 0)
            throw new WorkflowPlanManifestValidationException(errors);
    }

    private static void ValidateAlgorithmNode(
        WorkflowPlanAlgorithmNode node,
        IReadOnlySet<string> subPlanIds,
        HashSet<string> algorithmIds,
        List<string> errors,
        string path)
    {
        if (!KnownAlgorithmTypes.Contains(node.Type))
            errors.Add($"{path}: unknown algorithm type '{node.Type}'");

        if (!string.IsNullOrWhiteSpace(node.Id) && !algorithmIds.Add(node.Id))
            errors.Add($"{path}: duplicate algorithm id '{node.Id}'");

        switch (node.Type)
        {
            case "sequence":
                ValidateSequence(node, subPlanIds, algorithmIds, errors, path);
                break;
            case "parallel":
                ValidateChildren(node.Branches, subPlanIds, algorithmIds, errors, path + ".branches");
                break;
            case "foreach.sequential":
            case "foreach.parallel":
                if (string.IsNullOrWhiteSpace(node.Items))
                    errors.Add($"{path}: foreach requires 'items'");
                ValidateChildren(node.Steps, subPlanIds, algorithmIds, errors, path + ".steps");
                break;
            case "switch":
                if (node.Cases.Count == 0)
                    errors.Add($"{path}: switch requires cases");
                for (var i = 0; i < node.Cases.Count; i++)
                {
                    var @case = node.Cases[i];
                    if (string.IsNullOrWhiteSpace(@case.Value) && string.IsNullOrWhiteSpace(@case.When))
                        errors.Add($"{path}.cases[{i}]: case requires value or when");
                    ValidateAlgorithmNode(@case.Step, subPlanIds, algorithmIds, errors, $"{path}.cases[{i}]");
                }
                if (node.Default != null)
                    ValidateAlgorithmNode(node.Default, subPlanIds, algorithmIds, errors, path + ".default");
                break;
            case "task":
                if (string.IsNullOrWhiteSpace(node.Task) && (node.Outputs == null || node.Outputs.Count == 0))
                    errors.Add($"{path}: task requires 'task', 'responsibility', 'description', or outputs");
                if (node.Steps.Count > 0 || node.Branches.Count > 0 || node.Cases.Count > 0 || node.Default != null)
                    errors.Add($"{path}: task cannot contain child algorithm nodes");
                break;
            case "workflow.call":
                if (string.IsNullOrWhiteSpace(node.Plan))
                    errors.Add($"{path}: workflow.call requires 'plan'");
                else if (!subPlanIds.Contains(node.Plan))
                    errors.Add($"{path}: workflow.call references undeclared subplan '{node.Plan}'");
                break;
        }
    }

    private static void ValidateChildren(
        IReadOnlyList<WorkflowPlanAlgorithmNode> children,
        IReadOnlySet<string> subPlanIds,
        HashSet<string> algorithmIds,
        List<string> errors,
        string path)
    {
        if (children.Count == 0)
            errors.Add($"{path}: at least one child is required");

        for (var i = 0; i < children.Count; i++)
            ValidateAlgorithmNode(children[i], subPlanIds, algorithmIds, errors, $"{path}[{i}]");
    }

    private static void ValidateSequence(
        WorkflowPlanAlgorithmNode node,
        IReadOnlySet<string> subPlanIds,
        HashSet<string> algorithmIds,
        List<string> errors,
        string path)
    {
        if (node.Steps.Count == 0)
        {
            if (node.Outputs == null || node.Outputs.Count == 0)
                errors.Add($"{path}.steps: at least one child is required");
            return;
        }

        for (var i = 0; i < node.Steps.Count; i++)
            ValidateAlgorithmNode(node.Steps[i], subPlanIds, algorithmIds, errors, $"{path}.steps[{i}]");
    }

    private static void ValidateSubPlanDependencyGraph(
        IReadOnlyDictionary<string, HashSet<string>> dependencies,
        List<string> errors)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();

        foreach (var subPlanId in dependencies.Keys)
            Visit(subPlanId);

        void Visit(string subPlanId)
        {
            if (visited.Contains(subPlanId))
                return;
            if (!dependencies.ContainsKey(subPlanId))
                return;
            if (!visiting.Add(subPlanId))
            {
                var cycle = stack.Reverse().SkipWhile(id => id != subPlanId).Concat([subPlanId]);
                errors.Add("subplan call cycle detected: " + string.Join(" -> ", cycle));
                return;
            }

            stack.Push(subPlanId);
            foreach (var dependency in dependencies[subPlanId])
                Visit(dependency);
            stack.Pop();
            visiting.Remove(subPlanId);
            visited.Add(subPlanId);
        }
    }

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (Path.IsPathRooted(path))
            return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal) || normalized.EndsWith("/..", StringComparison.Ordinal))
            return false;
        return normalized.StartsWith("./", StringComparison.Ordinal)
            && normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCanonicalSubWorkflowPath(string workflowName, string path)
    {
        var normalized = path.Replace('\\', '/');
        var prefix = "./" + workflowName + "/";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var leaf = normalized[prefix.Length..];
        return leaf.Length > ".yaml".Length
            && leaf.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            && !leaf.Contains('/', StringComparison.Ordinal);
    }
}

public static partial class WorkflowPlanManifestCompiler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string CompileMainYaml(WorkflowPlanManifest manifest, bool localCalls = false, int minimumSteps = 0)
    {
        var byId = manifest.SubPlans.ToDictionary(static plan => plan.Id, StringComparer.Ordinal);
        InferInlineTaskOutputs(manifest.Algorithm, manifest.Inputs);
        var sb = new StringBuilder();
        sb.AppendLine("version: 1");
        sb.AppendLine($"name: {Quote(manifest.Name)}");
        sb.AppendLine("skill:");
        sb.AppendLine($"  description: {Quote(manifest.Description)}");
        sb.AppendLine("  tags: [generated, split]");
        AppendJsonMapping(sb, "  inputs", manifest.Inputs, 2);
        AppendJsonMapping(sb, "  outputs", manifest.Outputs, 2);
        sb.AppendLine("workflows:");
        sb.AppendLine("  main:");
        AppendJsonMapping(sb, "    inputs", manifest.Inputs, 4);
        AppendOutputs(sb, manifest.Outputs, 4);
        sb.AppendLine("    steps:");

        var rootSteps = manifest.Algorithm.Type == "sequence"
            ? CompileChildren(manifest.Algorithm.Steps, byId, "plan", localCalls)
            : CompileNode(manifest.Algorithm, byId, "plan", localCalls);
        EnsureMinimumSteps(rootSteps, "split_orchestration", minimumSteps);
        foreach (var step in rootSteps)
            AppendStep(sb, step, 6);

        return sb.ToString();
    }

    public static string CompileManifestYaml(WorkflowPlanManifest manifest)
        => CompileManifestYaml(manifest, includeRuntimePaths: true);

    public static string CompileManifestYaml(WorkflowPlanManifest manifest, bool includeRuntimePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"name: {Quote(manifest.Name)}");
        sb.AppendLine($"description: {Quote(manifest.Description)}");
        if (manifest.Inputs.Count > 0)
            AppendJsonMapping(sb, "inputs", manifest.Inputs, 0);
        if (manifest.Outputs.Count > 0)
            AppendJsonMapping(sb, "outputs", manifest.Outputs, 0);
        sb.AppendLine("subplans:");
        foreach (var subPlan in manifest.SubPlans)
        {
            sb.AppendLine($"  - id: {Quote(subPlan.Id)}");
            if (includeRuntimePaths)
                sb.AppendLine($"    path: {Quote(subPlan.Path)}");
            sb.AppendLine($"    responsibility: {Quote(subPlan.Responsibility)}");
            if (subPlan.Algorithm != null)
            {
                sb.AppendLine("    algorithm:");
                AppendAlgorithmNode(sb, subPlan.Algorithm, 6);
            }
        }
        sb.AppendLine("algorithm:");
        AppendAlgorithmNode(sb, manifest.Algorithm, 2);
        return sb.ToString();
    }

    public static string CompileAlgorithmYaml(WorkflowPlanAlgorithmNode algorithm)
    {
        var sb = new StringBuilder();
        AppendAlgorithmNode(sb, algorithm, 0);
        return sb.ToString();
    }

    public static string CompileMermaid(WorkflowPlanManifest manifest)
    {
        var byId = manifest.SubPlans.ToDictionary(static plan => plan.Id, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("sequenceDiagram");
        sb.AppendLine("    participant Main");
        foreach (var subPlan in manifest.SubPlans)
            sb.AppendLine($"    participant {MermaidId(subPlan.Id)} as {EscapeMermaidText(subPlan.Id)}");

        foreach (var subPlan in manifest.SubPlans)
        {
            var task = EscapeMermaidText(Shorten(subPlan.Responsibility, 72));
            sb.AppendLine($"    Note over {MermaidId(subPlan.Id)}: {task}");
        }

        AppendMermaidNode(sb, manifest.Algorithm, "Main", byId, 1);
        return sb.ToString().TrimEnd();
    }

    public static string CompileSubPlanYaml(WorkflowPlanManifest manifest, WorkflowPlanSubPlan subPlan)
    {
        if (subPlan.Algorithm == null)
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Sub-plan '{subPlan.Id}' does not declare an algorithm.");

        var byId = manifest.SubPlans.ToDictionary(static plan => plan.Id, StringComparer.Ordinal);
        InferInlineTaskOutputs(subPlan.Algorithm, subPlan.Inputs);
        var steps = subPlan.Algorithm.Type == "sequence"
            ? CompileChildren(subPlan.Algorithm.Steps, byId, $"subplan_{SanitizeId(subPlan.Id)}", localCalls: false)
            : CompileNode(subPlan.Algorithm, byId, $"subplan_{SanitizeId(subPlan.Id)}", localCalls: false);
        var outputs = CompileCompositeOutputs(subPlan.Outputs, steps, byId);

        var sb = new StringBuilder();
        sb.AppendLine("version: 1");
        sb.AppendLine($"name: {Quote(manifest.Name + "-" + subPlan.Id)}");
        sb.AppendLine("skill:");
        sb.AppendLine($"  description: {Quote(subPlan.Responsibility)}");
        sb.AppendLine("  tags: [generated, split, composite]");
        AppendJsonMapping(sb, "  inputs", subPlan.Inputs, 2);
        AppendJsonMapping(sb, "  outputs", subPlan.Outputs, 2);
        sb.AppendLine("workflows:");
        sb.AppendLine("  main:");
        AppendJsonMapping(sb, "    inputs", subPlan.Inputs, 4);
        AppendOutputs(sb, outputs, 4);
        sb.AppendLine("    steps:");
        foreach (var step in steps)
            AppendStep(sb, step, 6);

        return sb.ToString();
    }

    private static void AppendAlgorithmNode(StringBuilder sb, WorkflowPlanAlgorithmNode node, int indent)
    {
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}type: {Quote(node.Type)}");
        if (!string.IsNullOrWhiteSpace(node.Id))
            sb.AppendLine($"{pad}id: {Quote(node.Id)}");
        if (!string.IsNullOrWhiteSpace(node.Task))
            sb.AppendLine($"{pad}task: {Quote(node.Task)}");
        if (!string.IsNullOrWhiteSpace(node.Plan))
            sb.AppendLine($"{pad}plan: {Quote(node.Plan)}");
        if (!string.IsNullOrWhiteSpace(node.Items))
            sb.AppendLine($"{pad}items: {Quote(node.Items)}");
        if (!string.IsNullOrWhiteSpace(node.ItemVar))
            sb.AppendLine($"{pad}item_var: {Quote(node.ItemVar)}");
        if (!string.IsNullOrWhiteSpace(node.IndexVar))
            sb.AppendLine($"{pad}index_var: {Quote(node.IndexVar)}");
        if (!string.IsNullOrWhiteSpace(node.Expr))
            sb.AppendLine($"{pad}expr: {Quote(node.Expr)}");
        if (node.Outputs != null)
            AppendJsonMapping(sb, $"{pad}outputs", node.Outputs, indent);

        if (node.Steps.Count > 0)
        {
            sb.AppendLine($"{pad}steps:");
            foreach (var child in node.Steps)
            {
                sb.AppendLine($"{pad}  -");
                AppendAlgorithmNode(sb, child, indent + 4);
            }
        }

        if (node.Branches.Count > 0)
        {
            sb.AppendLine($"{pad}branches:");
            foreach (var child in node.Branches)
            {
                sb.AppendLine($"{pad}  -");
                AppendAlgorithmNode(sb, child, indent + 4);
            }
        }

        if (node.Cases.Count > 0)
        {
            sb.AppendLine($"{pad}cases:");
            foreach (var @case in node.Cases)
            {
                sb.AppendLine($"{pad}  -");
                if (!string.IsNullOrWhiteSpace(@case.Value))
                    sb.AppendLine($"{pad}    value: {Quote(@case.Value)}");
                if (!string.IsNullOrWhiteSpace(@case.When))
                    sb.AppendLine($"{pad}    when: {Quote(@case.When)}");
                sb.AppendLine($"{pad}    step:");
                AppendAlgorithmNode(sb, @case.Step, indent + 6);
            }
        }

        if (node.Default != null)
        {
            sb.AppendLine($"{pad}default:");
            AppendAlgorithmNode(sb, node.Default, indent + 2);
        }
    }

    private static void AppendMermaidNode(
        StringBuilder sb,
        WorkflowPlanAlgorithmNode node,
        string caller,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        int depth)
    {
        var pad = new string(' ', 4);
        switch (node.Type)
        {
            case "sequence":
                foreach (var child in node.Steps)
                    AppendMermaidNode(sb, child, caller, subPlans, depth);
                break;
            case "parallel":
                sb.AppendLine($"{pad}par parallel");
                foreach (var branch in node.Branches)
                {
                    sb.AppendLine($"{pad}and branch");
                    AppendMermaidNode(sb, branch, caller, subPlans, depth + 1);
                }
                sb.AppendLine($"{pad}end");
                break;
            case "foreach.sequential":
            case "foreach.parallel":
                var loopLabel = node.Type == "foreach.parallel" ? "foreach parallel" : "foreach sequential";
                sb.AppendLine($"{pad}loop {EscapeMermaidText(loopLabel)}");
                foreach (var child in node.Steps)
                    AppendMermaidNode(sb, child, caller, subPlans, depth + 1);
                sb.AppendLine($"{pad}end");
                break;
            case "switch":
                for (var i = 0; i < node.Cases.Count; i++)
                {
                    var @case = node.Cases[i];
                    var label = @case.Value ?? @case.When ?? $"case {i + 1}";
                    sb.AppendLine(i == 0
                        ? $"{pad}alt {EscapeMermaidText(label)}"
                        : $"{pad}else {EscapeMermaidText(label)}");
                    AppendMermaidNode(sb, @case.Step, caller, subPlans, depth + 1);
                }
                if (node.Default != null)
                {
                    sb.AppendLine($"{pad}else default");
                    AppendMermaidNode(sb, node.Default, caller, subPlans, depth + 1);
                }
                if (node.Cases.Count > 0 || node.Default != null)
                    sb.AppendLine($"{pad}end");
                break;
            case "task":
                var task = string.IsNullOrWhiteSpace(node.Task) ? node.Id ?? "inline task" : node.Task;
                sb.AppendLine($"{pad}Note over {caller}: {EscapeMermaidText(Shorten(task, 72))}");
                break;
            case "workflow.call":
                if (!string.IsNullOrWhiteSpace(node.Plan) && subPlans.TryGetValue(node.Plan, out var plan))
                {
                    var target = MermaidId(plan.Id);
                    var label = EscapeMermaidText(Shorten(plan.Responsibility, 64));
                    sb.AppendLine($"{pad}{caller}->>{target}: {label}");
                    if (plan.Algorithm != null)
                        AppendMermaidNode(sb, plan.Algorithm, target, subPlans, depth + 1);
                    sb.AppendLine($"{pad}{target}-->>{caller}: done");
                }
                break;
        }
    }

    private static string MermaidId(string value)
    {
        var sanitized = SanitizeId(value).Replace('-', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "Workflow" : sanitized;
    }

    private static string EscapeMermaidText(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace(":", "-", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal)
            .Trim();

    private static string Shorten(string value, int maxLength)
    {
        var normalized = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length <= maxLength)
            return normalized;
        return normalized[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
    }

    private static List<CompiledPlanStep> CompileNode(
        WorkflowPlanAlgorithmNode node,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        string fallbackId,
        bool localCalls)
    {
        return node.Type switch
        {
            "sequence" => [new CompiledPlanStep(node.Id ?? fallbackId, "sequence") { Steps = AppendOutputSetStep(CompileChildren(node.Steps, subPlans, fallbackId, localCalls), node.Outputs, fallbackId) }],
            "parallel" => [new CompiledPlanStep(node.Id ?? fallbackId, "parallel") { Branches = node.Branches.Select((branch, index) => CompileNode(branch, subPlans, $"{fallbackId}_branch_{index}", localCalls)).ToList() }],
            "foreach.sequential" => [CompileLoop(node, subPlans, fallbackId, "loop.sequential", localCalls)],
            "foreach.parallel" => [CompileLoop(node, subPlans, fallbackId, "loop.parallel", localCalls)],
            "switch" => [CompileSwitch(node, subPlans, fallbackId, localCalls)],
            "task" => [CompileInlineTask(node, fallbackId)],
            "workflow.call" => [CompileWorkflowCall(node, subPlans, fallbackId, localCalls)],
            _ => throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Unknown split algorithm type '{node.Type}'.")
        };
    }

    private static List<CompiledPlanStep> CompileChildren(
        IReadOnlyList<WorkflowPlanAlgorithmNode> children,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        string fallbackId,
        bool localCalls)
    {
        var result = new List<CompiledPlanStep>();
        for (var i = 0; i < children.Count; i++)
            result.AddRange(CompileNode(children[i], subPlans, $"{fallbackId}_{i}", localCalls));
        return result;
    }

    private static List<CompiledPlanStep> AppendOutputSetStep(
        List<CompiledPlanStep> steps,
        JsonObject? outputs,
        string fallbackId)
    {
        if (outputs == null || outputs.Count == 0)
            return steps;

        steps.Add(new CompiledPlanStep($"set_outputs_{SanitizeId(fallbackId)}", "set")
        {
            Input = BuildSetInputFromOutputs(outputs)
        });
        return steps;
    }

    private static JsonObject BuildSetInputFromOutputs(JsonObject outputs)
    {
        var input = new JsonObject();
        foreach (var (key, value) in outputs)
            input[key] = ExtractOutputExpression(value);
        return input;
    }

    private static void EnsureMinimumSteps(List<CompiledPlanStep> steps, string idPrefix, int minimumSteps)
    {
        if (minimumSteps <= 0)
            return;

        var current = CountSteps(steps);
        for (var i = current; i < minimumSteps; i++)
        {
            steps.Add(new CompiledPlanStep($"{idPrefix}_pad_{i + 1}", "set")
            {
                Input = new JsonObject
                {
                    ["ready"] = true
                }
            });
        }
    }

    private static int CountSteps(IEnumerable<CompiledPlanStep> steps)
    {
        var count = 0;
        foreach (var step in steps)
        {
            count++;
            count += CountSteps(step.Steps);
            foreach (var branch in step.Branches)
                count += CountSteps(branch);
            foreach (var @case in step.Cases)
                count += CountSteps(@case.Steps);
            if (step.Default != null)
                count += CountSteps(step.Default);
        }

        return count;
    }

    private static JsonNode? ExtractOutputExpression(JsonNode? value)
    {
        if (value is JsonObject obj && obj["expr"] != null)
            return obj["expr"]!.DeepClone();

        return value?.DeepClone();
    }

    private static CompiledPlanStep CompileLoop(
        WorkflowPlanAlgorithmNode node,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        string fallbackId,
        string type,
        bool localCalls)
    {
        var input = new JsonObject { ["items"] = BuildLoopItemsInput(node.Items) };
        return new CompiledPlanStep(node.Id ?? fallbackId, type)
        {
            Input = input,
            ItemVar = node.ItemVar,
            IndexVar = node.IndexVar,
            Steps = CompileChildren(node.Steps, subPlans, fallbackId, localCalls)
        };
    }

    private static JsonNode? BuildLoopItemsInput(string? items)
    {
        if (string.IsNullOrWhiteSpace(items))
            return null;

        var trimmed = items.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var parsed = JsonNode.Parse(trimmed);
                if (parsed is JsonArray)
                    return parsed;
            }
            catch (JsonException)
            {
                // Fall through to the regular string handling below.
            }
        }

        if (IsPlainIdentifier(trimmed))
            return new JsonArray(new JsonObject());

        return trimmed;
    }

    private static CompiledPlanStep CompileSwitch(
        WorkflowPlanAlgorithmNode node,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        string fallbackId,
        bool localCalls)
    {
        var step = new CompiledPlanStep(node.Id ?? fallbackId, "switch")
        {
            Expr = node.Expr
        };
        for (var i = 0; i < node.Cases.Count; i++)
        {
            var @case = node.Cases[i];
            step.Cases.Add(new CompiledPlanSwitchCase
            {
                Value = @case.Value,
                When = @case.When,
                Steps = CompileNode(@case.Step, subPlans, $"{fallbackId}_case_{i}", localCalls)
            });
        }
        if (node.Default != null)
            step.Default = CompileNode(node.Default, subPlans, $"{fallbackId}_default", localCalls);
        return step;
    }

    private static CompiledPlanStep CompileWorkflowCall(
        WorkflowPlanAlgorithmNode node,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        string fallbackId,
        bool localCalls)
    {
        var plan = subPlans[node.Plan!];
        var refObj = localCalls
            ? new JsonObject
            {
                ["kind"] = "local",
                ["name"] = plan.Id
            }
            : new JsonObject
            {
                ["kind"] = "workspace",
                ["path"] = plan.Path
            };
        return new CompiledPlanStep(node.Id ?? $"call_{SanitizeId(plan.Id)}_{fallbackId}", "workflow.call")
        {
            Input = new JsonObject
            {
                ["ref"] = refObj,
                ["args"] = node.Args?.DeepClone() ?? BuildDefaultArgs(plan.Inputs)
            }
        };
    }

    private static CompiledPlanStep CompileInlineTask(WorkflowPlanAlgorithmNode node, string fallbackId)
    {
        var input = new JsonObject
        {
            ["task"] = node.Task ?? node.Id ?? "inline task",
            ["completed"] = true
        };

        if (node.Outputs != null)
        {
            foreach (var (key, value) in node.Outputs)
                input[key] = ExtractOutputExpression(value);
        }

        return new CompiledPlanStep(node.Id ?? $"task_{SanitizeId(fallbackId)}", "set")
        {
            Input = input
        };
    }

    private static void InferInlineTaskOutputs(WorkflowPlanAlgorithmNode root, JsonObject availableInputs)
    {
        var inlineTasks = new Dictionary<string, WorkflowPlanAlgorithmNode>(StringComparer.Ordinal);
        IndexInlineTasks(root, inlineTasks);
        InferForeachInputs(root, inlineTasks, availableInputs, previousInlineTask: null);
    }

    private static void IndexInlineTasks(
        WorkflowPlanAlgorithmNode node,
        Dictionary<string, WorkflowPlanAlgorithmNode> inlineTasks)
    {
        if (node.Type == "task" && !string.IsNullOrWhiteSpace(node.Id))
            inlineTasks[node.Id] = node;

        foreach (var child in node.Steps)
            IndexInlineTasks(child, inlineTasks);
        foreach (var branch in node.Branches)
            IndexInlineTasks(branch, inlineTasks);
        foreach (var @case in node.Cases)
            IndexInlineTasks(@case.Step, inlineTasks);
        if (node.Default != null)
            IndexInlineTasks(node.Default, inlineTasks);
    }

    private static void InferForeachInputs(
        WorkflowPlanAlgorithmNode node,
        IReadOnlyDictionary<string, WorkflowPlanAlgorithmNode> inlineTasks,
        JsonObject availableInputs,
        WorkflowPlanAlgorithmNode? previousInlineTask)
    {
        if ((node.Type is "foreach.sequential" or "foreach.parallel")
            && TryExtractStepOutputReference(node.Items, out var stepId, out var field)
            && inlineTasks.TryGetValue(stepId, out var task))
        {
            EnsureInlineTaskArrayOutput(task, field);
        }
        else if ((node.Type is "foreach.sequential" or "foreach.parallel")
            && IsPlainIdentifier(node.Items))
        {
            var fieldName = node.Items!.Trim();
            if (availableInputs.ContainsKey(fieldName))
            {
                node.Items = "${data.inputs." + fieldName + "}";
            }
            else if (previousInlineTask != null && !string.IsNullOrWhiteSpace(previousInlineTask.Id))
            {
                EnsureInlineTaskArrayOutput(previousInlineTask, fieldName);
                node.Items = "${data.steps." + previousInlineTask.Id + "." + fieldName + "}";
            }
        }

        WorkflowPlanAlgorithmNode? lastInlineTask = null;
        foreach (var child in node.Steps)
        {
            InferForeachInputs(child, inlineTasks, availableInputs, lastInlineTask);
            if (child.Type == "task" && !string.IsNullOrWhiteSpace(child.Id))
                lastInlineTask = child;
        }

        foreach (var branch in node.Branches)
            InferForeachInputs(branch, inlineTasks, availableInputs, previousInlineTask: null);
        foreach (var @case in node.Cases)
            InferForeachInputs(@case.Step, inlineTasks, availableInputs, previousInlineTask: null);
        if (node.Default != null)
            InferForeachInputs(node.Default, inlineTasks, availableInputs, previousInlineTask: null);
    }

    private static void EnsureInlineTaskArrayOutput(WorkflowPlanAlgorithmNode task, string field)
    {
        task.Outputs ??= new JsonObject();
        if (!task.Outputs.ContainsKey(field))
            task.Outputs[field] = new JsonArray(new JsonObject());
    }

    private static bool IsPlainIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return PlainIdentifierRegex().IsMatch(trimmed);
    }

    private static bool TryExtractStepOutputReference(string? expression, out string stepId, out string field)
    {
        stepId = "";
        field = "";
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var match = StepOutputReferenceRegex().Match(expression);
        if (!match.Success)
            return false;

        stepId = match.Groups["step"].Value;
        field = match.Groups["field"].Value;
        return !string.IsNullOrWhiteSpace(stepId) && !string.IsNullOrWhiteSpace(field);
    }

    [GeneratedRegex(@"data\.steps\.(?<step>[A-Za-z0-9_-]+)\.(?<field>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StepOutputReferenceRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainIdentifierRegex();

    private static JsonObject BuildDefaultArgs(JsonObject inputs)
    {
        var args = new JsonObject();
        foreach (var key in inputs.Select(static kv => kv.Key))
            args[key] = "${data.inputs." + key + "}";
        return args;
    }

    private static JsonObject CompileCompositeOutputs(
        JsonObject declaredOutputs,
        IReadOnlyList<CompiledPlanStep> steps,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans)
    {
        var outputMappings = new JsonObject();
        var flattenedSteps = FlattenSteps(steps).ToList();

        foreach (var (key, value) in declaredOutputs)
        {
            if (IsOutputExpression(value))
            {
                outputMappings[key] = value?.DeepClone();
                continue;
            }

            var sourceStep = flattenedSteps.LastOrDefault(step => CanProvideOutput(step, key, subPlans));
            sourceStep ??= flattenedSteps.LastOrDefault(static step => step.Type is "workflow.call" or "set");

            if (sourceStep == null)
            {
                outputMappings[key] = value?.DeepClone();
                continue;
            }

            var expr = BuildOutputReferenceExpression(sourceStep, key);
            if (value is JsonObject schema)
            {
                var typed = schema.DeepClone().AsObject();
                typed["expr"] = expr;
                outputMappings[key] = typed;
            }
            else
            {
                outputMappings[key] = expr;
            }
        }

        return outputMappings;
    }

    private static bool CanProvideOutput(
        CompiledPlanStep step,
        string key,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans)
    {
        if (step.Type == "set")
            return step.Input?.ContainsKey(key) == true;

        return step.Type == "workflow.call"
            && TryGetCalledPlan(step, subPlans, out var calledPlan)
            && calledPlan.Outputs.ContainsKey(key);
    }

    private static string BuildOutputReferenceExpression(CompiledPlanStep step, string key)
        => step.Type == "workflow.call"
            ? "${data.steps." + step.Id + ".outputs." + key + "}"
            : "${data.steps." + step.Id + "." + key + "}";

    private static bool IsOutputExpression(JsonNode? value)
        => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _)
            || value is JsonObject obj && obj.ContainsKey("expr");

    private static bool TryGetCalledPlan(
        CompiledPlanStep step,
        IReadOnlyDictionary<string, WorkflowPlanSubPlan> subPlans,
        out WorkflowPlanSubPlan calledPlan)
    {
        var path = (step.Input?["ref"] as JsonObject)?["path"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var plan in subPlans.Values)
            {
                if (string.Equals(plan.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    calledPlan = plan;
                    return true;
                }
            }
        }

        calledPlan = null!;
        return false;
    }

    private static IEnumerable<CompiledPlanStep> FlattenSteps(IEnumerable<CompiledPlanStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in FlattenSteps(step.Steps))
                yield return child;
            foreach (var branch in step.Branches)
            {
                foreach (var child in FlattenSteps(branch))
                    yield return child;
            }
            foreach (var @case in step.Cases)
            {
                foreach (var child in FlattenSteps(@case.Steps))
                    yield return child;
            }
            if (step.Default != null)
            {
                foreach (var child in FlattenSteps(step.Default))
                    yield return child;
            }
        }
    }

    private static string SanitizeId(string value)
    {
        var chars = value.Select(static c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
        return new string(chars);
    }

    private static void AppendStep(StringBuilder sb, CompiledPlanStep step, int indent)
    {
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}- id: {Quote(step.Id)}");
        sb.AppendLine($"{pad}  type: {Quote(step.Type)}");
        if (step.Expr != null)
            sb.AppendLine($"{pad}  expr: {Quote(step.Expr)}");
        if (step.ItemVar != null)
            sb.AppendLine($"{pad}  item_var: {Quote(step.ItemVar)}");
        if (step.IndexVar != null)
            sb.AppendLine($"{pad}  index_var: {Quote(step.IndexVar)}");
        if (step.Input != null)
            AppendJsonMapping(sb, $"{pad}  input", step.Input, indent + 2);
        if (step.Branches.Count > 0)
        {
            sb.AppendLine($"{pad}  branches:");
            foreach (var branch in step.Branches)
            {
                sb.AppendLine($"{pad}    - steps:");
                foreach (var child in branch)
                    AppendStep(sb, child, indent + 8);
            }
        }
        if (step.Cases.Count > 0)
        {
            sb.AppendLine($"{pad}  cases:");
            foreach (var @case in step.Cases)
            {
                sb.AppendLine($"{pad}    -");
                if (@case.Value != null)
                    sb.AppendLine($"{pad}      value: {Quote(@case.Value)}");
                if (@case.When != null)
                    sb.AppendLine($"{pad}      when: {Quote(@case.When)}");
                sb.AppendLine($"{pad}      steps:");
                foreach (var child in @case.Steps)
                    AppendStep(sb, child, indent + 8);
            }
        }
        if (step.Default is { Count: > 0 })
        {
            sb.AppendLine($"{pad}  default:");
            foreach (var child in step.Default)
                AppendStep(sb, child, indent + 4);
        }
        if (step.Steps.Count > 0)
        {
            sb.AppendLine($"{pad}  steps:");
            foreach (var child in step.Steps)
                AppendStep(sb, child, indent + 4);
        }
    }

    private static void AppendOutputs(StringBuilder sb, string label, JsonObject outputs, int indent)
    {
        if (outputs.Count == 0)
        {
            sb.AppendLine($"{new string(' ', indent)}outputs: {{}}");
            return;
        }

        sb.AppendLine($"{new string(' ', indent)}outputs:");
        foreach (var (key, value) in outputs)
        {
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var expr))
                sb.AppendLine($"{new string(' ', indent + 2)}{key}: {Quote(expr)}");
            else
                AppendJsonValue(sb, key, value, indent + 2);
        }
    }

    private static void AppendOutputs(StringBuilder sb, JsonObject outputs, int indent)
        => AppendOutputs(sb, "outputs", outputs, indent);

    private static void AppendJsonMapping(StringBuilder sb, string label, JsonObject obj, int indent)
    {
        if (obj.Count == 0)
        {
            sb.AppendLine($"{label}: {{}}");
            return;
        }

        sb.AppendLine($"{label}:");
        foreach (var (key, value) in obj)
            AppendJsonValue(sb, key, value, indent + 2);
    }

    private static void AppendJsonValue(StringBuilder sb, string key, JsonNode? value, int indent)
    {
        var pad = new string(' ', indent);
        switch (value)
        {
            case JsonObject obj:
                sb.AppendLine($"{pad}{key}:");
                foreach (var (childKey, childValue) in obj)
                    AppendJsonValue(sb, childKey, childValue, indent + 2);
                break;
            case JsonArray arr:
                sb.AppendLine($"{pad}{key}:");
                foreach (var item in arr)
                    AppendJsonArrayItem(sb, item, indent + 2);
                break;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue):
                sb.AppendLine($"{pad}{key}: {Quote(stringValue)}");
                break;
            case JsonValue:
                sb.AppendLine($"{pad}{key}: {value!.ToJsonString(JsonOptions)}");
                break;
            default:
                sb.AppendLine($"{pad}{key}: null");
                break;
        }
    }

    private static void AppendJsonArrayItem(StringBuilder sb, JsonNode? value, int indent)
    {
        var pad = new string(' ', indent);
        switch (value)
        {
            case JsonObject obj:
                sb.AppendLine($"{pad}-");
                foreach (var (key, childValue) in obj)
                    AppendJsonValue(sb, key, childValue, indent + 2);
                break;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue):
                sb.AppendLine($"{pad}- {Quote(stringValue)}");
                break;
            case JsonValue:
                sb.AppendLine($"{pad}- {value!.ToJsonString(JsonOptions)}");
                break;
            default:
                sb.AppendLine($"{pad}- null");
                break;
        }
    }

    private static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    private sealed class CompiledPlanStep
    {
        public CompiledPlanStep(string id, string type)
        {
            Id = id;
            Type = type;
        }

        public string Id { get; }
        public string Type { get; }
        public JsonObject? Input { get; init; }
        public string? ItemVar { get; init; }
        public string? IndexVar { get; init; }
        public string? Expr { get; init; }
        public List<CompiledPlanStep> Steps { get; init; } = new();
        public List<List<CompiledPlanStep>> Branches { get; init; } = new();
        public List<CompiledPlanSwitchCase> Cases { get; init; } = new();
        public List<CompiledPlanStep>? Default { get; set; }
    }

    private sealed class CompiledPlanSwitchCase
    {
        public string? Value { get; init; }
        public string? When { get; init; }
        public List<CompiledPlanStep> Steps { get; init; } = new();
    }
}
