using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;

namespace GnOuGo.Flow.Mermaid;

/// <summary>
/// Renders GnOuGo.Flow YAML workflows as Mermaid flowcharts.
/// </summary>
public static class MermaidWorkflowRenderer
{
    public static MermaidRenderResult Render(string yaml, MermaidRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return Render(WorkflowParser.Parse(yaml), options);
    }

    public static MermaidRenderResult Render(WorkflowDocument document, MermaidRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new MermaidRenderOptions();

        var mainWorkflowName = ResolveEntrypoint(document, options.Entrypoint);
        if (!document.Workflows.TryGetValue(mainWorkflowName, out var mainWorkflow))
            throw new ArgumentException($"Workflow '{mainWorkflowName}' was not found.", nameof(options));

        var references = DiscoverReferencedLocalWorkflows(document, mainWorkflowName, options.SubWorkflowMode);
        var missing = references.Where(name => !document.Workflows.ContainsKey(name)).Distinct(StringComparer.Ordinal).ToArray();
        var availableReferences = references.Where(document.Workflows.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();

        var main = RenderSingle(document, mainWorkflowName, mainWorkflow, isEntrypoint: true, options);
        var subWorkflowNames = ResolveSubWorkflowNames(document, mainWorkflowName, availableReferences, options.SubWorkflowMode);
        var subWorkflows = subWorkflowNames
            .Select(name => RenderSingle(document, name, document.Workflows[name], isEntrypoint: false, options))
            .ToArray();

        return new MermaidRenderResult
        {
            Main = main,
            SubWorkflows = subWorkflows,
            ReferencedLocalWorkflows = availableReferences,
            MissingLocalWorkflowReferences = missing
        };
    }

    private static string ResolveEntrypoint(WorkflowDocument document, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;

        if (!string.IsNullOrWhiteSpace(document.Entrypoint))
            return document.Entrypoint;

        if (document.Workflows.ContainsKey("main"))
            return "main";

        return document.Workflows.Keys.FirstOrDefault()
            ?? throw new ArgumentException("The workflow document does not contain any workflows.", nameof(document));
    }

    private static IReadOnlyList<string> ResolveSubWorkflowNames(
        WorkflowDocument document,
        string mainWorkflowName,
        IReadOnlyList<string> referenced,
        MermaidSubWorkflowMode mode)
    {
        return mode switch
        {
            MermaidSubWorkflowMode.None => Array.Empty<string>(),
            MermaidSubWorkflowMode.AllLocalWorkflows => document.Workflows.Keys
                .Where(name => !string.Equals(name, mainWorkflowName, StringComparison.Ordinal))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray(),
            _ => referenced
                .Where(name => !string.Equals(name, mainWorkflowName, StringComparison.Ordinal))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static MermaidDiagram RenderSingle(
        WorkflowDocument document,
        string workflowName,
        WorkflowDef workflow,
        bool isEntrypoint,
        MermaidRenderOptions options)
    {
        var state = new RenderState(document, workflowName, options);
        var direction = options.Direction == MermaidDirection.LeftRight ? "LR" : "TD";

        state.AppendLine($"flowchart {direction}");

        var startNode = state.AddSyntheticNode("start", "Start", NodeShape.Circle);
        var endNode = state.AddSyntheticNode("end", "End", NodeShape.Circle);
        var lastExits = new List<string> { startNode };

        if (options.IncludeInputsAndOutputs)
        {
            var inputNode = state.TryAddSummaryNode("inputs", "Inputs", workflow.Inputs?.Keys);
            if (inputNode != null)
            {
                state.ConnectMany(lastExits, inputNode);
                lastExits = new List<string> { inputNode };
            }
        }

        var rendered = state.RenderStepList(workflow.Steps, "step");
        if (rendered.Entry != null)
        {
            state.ConnectMany(lastExits, rendered.Entry, rendered.EntryEdgeLabel);
            lastExits = rendered.Exits.Count == 0 ? new List<string> { rendered.Entry } : rendered.Exits.ToList();
        }

        if (options.IncludeInputsAndOutputs)
        {
            var outputNode = state.TryAddSummaryNode("outputs", "Outputs", workflow.Outputs?.Keys);
            if (outputNode != null)
            {
                state.ConnectMany(lastExits, outputNode);
                lastExits = new List<string> { outputNode };
            }
        }

        state.ConnectMany(lastExits, endNode);

        return new MermaidDiagram
        {
            WorkflowName = workflowName,
            SuggestedFileName = $"{SanitizeFileName(workflowName)}.mmd",
            IsEntrypoint = isEntrypoint,
            Content = state.ToString()
        };
    }

    private static IReadOnlyList<string> DiscoverReferencedLocalWorkflows(
        WorkflowDocument document,
        string rootWorkflowName,
        MermaidSubWorkflowMode mode)
    {
        if (mode == MermaidSubWorkflowMode.None)
            return Array.Empty<string>();

        var result = new List<string>();
        var queued = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        queued.Enqueue(rootWorkflowName);

        while (queued.Count > 0)
        {
            var workflowName = queued.Dequeue();
            if (!visited.Add(workflowName))
                continue;

            if (!document.Workflows.TryGetValue(workflowName, out var workflow))
                continue;

            foreach (var reference in EnumerateLocalWorkflowReferences(workflow.Steps))
            {
                if (!result.Contains(reference, StringComparer.Ordinal))
                    result.Add(reference);

                if (document.Workflows.ContainsKey(reference) && !visited.Contains(reference))
                    queued.Enqueue(reference);
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateLocalWorkflowReferences(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            foreach (var reference in EnumerateLocalWorkflowReferences(step))
                yield return reference;
        }
    }

    private static IEnumerable<string> EnumerateLocalWorkflowReferences(StepDef step)
    {
        if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            && TryGetLocalWorkflowName(step.Input, out var workflowName))
        {
            yield return workflowName;
        }

        if (string.Equals(step.Type, "workflow.route", StringComparison.Ordinal))
        {
            foreach (var reference in EnumerateRouteLocalWorkflowReferences(step.Input))
                yield return reference;
        }

        if (step.Steps != null)
        {
            foreach (var reference in EnumerateLocalWorkflowReferences(step.Steps))
                yield return reference;
        }

        if (step.Branches != null)
        {
            foreach (var branch in step.Branches)
            {
                foreach (var reference in EnumerateLocalWorkflowReferences(branch.Steps))
                    yield return reference;
            }
        }

        if (step.Cases != null)
        {
            foreach (var switchCase in step.Cases)
            {
                foreach (var reference in EnumerateLocalWorkflowReferences(switchCase.Steps))
                    yield return reference;
            }
        }

        if (step.Default != null)
        {
            foreach (var reference in EnumerateLocalWorkflowReferences(step.Default))
                yield return reference;
        }
    }

    private static IEnumerable<string> EnumerateRouteLocalWorkflowReferences(JsonNode? input)
    {
        if (input is not JsonObject inputObject || inputObject["candidates"] is not JsonArray candidates)
            yield break;

        foreach (var candidate in candidates)
        {
            if (candidate is not JsonObject candidateObject)
                continue;

            var refObject = candidateObject["ref"] as JsonObject;
            if (TryGetLocalWorkflowName(refObject, out var workflowName))
                yield return workflowName;
        }
    }

    private static bool TryGetLocalWorkflowName(JsonNode? inputOrRef, out string workflowName)
    {
        workflowName = "";

        if (inputOrRef is not JsonObject obj)
            return false;

        var refObject = obj["ref"] as JsonObject ?? obj;
        var kind = GetString(refObject, "kind") ?? "local";
        if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase))
            return false;

        workflowName = GetString(refObject, "name") ?? "";
        return !string.IsNullOrWhiteSpace(workflowName);
    }

    private static string? GetString(JsonObject obj, string propertyName)
    {
        var node = obj[propertyName];
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "workflow";

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return builder.Length == 0 ? "workflow" : builder.ToString();
    }

    private sealed class RenderState
    {
        private readonly WorkflowDocument _document;
        private readonly string _workflowName;
        private readonly MermaidRenderOptions _options;
        private readonly StringBuilder _nodes = new();
        private readonly StringBuilder _edges = new();
        private int _nodeIndex;
        private int _edgeIndex;

        public RenderState(WorkflowDocument document, string workflowName, MermaidRenderOptions options)
        {
            _document = document;
            _workflowName = workflowName;
            _options = options;
        }

        public void AppendLine(string line) => _nodes.AppendLine(line);

        public string AddSyntheticNode(string name, string label, NodeShape shape)
        {
            var id = NewId(name);
            AppendNode(id, label, shape);
            return id;
        }

        public string? TryAddSummaryNode(string name, string title, IEnumerable<string>? keys)
        {
            if (keys == null)
                return null;

            var values = keys.Where(static key => !string.IsNullOrWhiteSpace(key)).OrderBy(static key => key, StringComparer.Ordinal).ToArray();
            if (values.Length == 0)
                return null;

            var id = NewId(name);
            AppendNode(id, title + ": " + string.Join(", ", values), NodeShape.Document);
            return id;
        }

        public RenderedStepList RenderStepList(IReadOnlyList<StepDef> steps, string scope)
        {
            string? entry = null;
            string? entryEdgeLabel = null;
            var exits = new List<string>();

            for (var i = 0; i < steps.Count; i++)
            {
                var rendered = RenderStep(steps[i], $"{scope}_{i}");
                if (rendered.Entry == null)
                    continue;

                if (entry == null)
                {
                    entry = rendered.Entry;
                    entryEdgeLabel = rendered.EntryEdgeLabel;
                }

                if (exits.Count > 0)
                    ConnectMany(exits, rendered.Entry, rendered.EntryEdgeLabel);

                exits = rendered.Exits.Count == 0
                    ? new List<string> { rendered.Entry }
                    : rendered.Exits.ToList();
            }

            return new RenderedStepList(entry, exits, entryEdgeLabel);
        }

        public void ConnectMany(IReadOnlyList<string> sources, string target, string? label = null)
        {
            foreach (var source in sources)
                Connect(source, target, label);
        }

        private RenderedStepList RenderStep(StepDef step, string scope)
        {
            if (IsHiddenStep(step))
                return new RenderedStepList(null, Array.Empty<string>(), null);

            var nodeId = NewId(step.Id);
            AppendNode(nodeId, BuildStepLabel(step), ShapeFor(step));

            var exits = new List<string> { nodeId };
            var entryEdgeLabel = IncomingEdgeLabelFor(step);

            if (step.Steps is { Count: > 0 } childSteps)
            {
                exits = IsLoop(step)
                    ? RenderLoopBody(nodeId, step, childSteps, $"{scope}_steps")
                    : RenderChildSteps(nodeId, step, childSteps, $"{scope}_steps");
            }

            if (step.Branches is { Count: > 0 } branches)
                exits = RenderBranches(nodeId, branches, $"{scope}_branch", "join");

            if (step.Cases is { Count: > 0 } cases || step.Default is { Count: > 0 })
                exits = RenderCases(nodeId, step, $"{scope}_case");

            return new RenderedStepList(nodeId, exits, entryEdgeLabel);
        }

        private List<string> RenderChildSteps(string sourceNodeId, StepDef step, IReadOnlyList<StepDef> childSteps, string scope)
        {
            var child = RenderStepList(childSteps, scope);
            if (child.Entry == null)
                return new List<string> { sourceNodeId };

            Connect(sourceNodeId, child.Entry, CombineEdgeLabels(ChildEdgeLabel(step), child.EntryEdgeLabel));
            return child.Exits.Count == 0 ? new List<string> { child.Entry } : child.Exits.ToList();
        }

        private List<string> RenderLoopBody(string sourceNodeId, StepDef step, IReadOnlyList<StepDef> childSteps, string scope)
        {
            var child = RenderStepList(childSteps, scope);
            if (child.Entry == null)
                return new List<string> { sourceNodeId };

            var join = AddSyntheticNode("loop_exit", "Loop exit", NodeShape.Circle);
            Connect(sourceNodeId, child.Entry, CombineEdgeLabels(ChildEdgeLabel(step), child.EntryEdgeLabel));
            ConnectMany(child.Exits.Count == 0 ? new[] { child.Entry } : child.Exits, join);
            return new List<string> { join };
        }

        private List<string> RenderBranches(string sourceNodeId, IReadOnlyList<BranchDef> branches, string scope, string joinName)
        {
            var join = AddSyntheticNode(joinName, "Join", NodeShape.Circle);

            for (var i = 0; i < branches.Count; i++)
            {
                var rendered = RenderStepList(branches[i].Steps, $"{scope}_{i}");
                var label = $"branch {i + 1}";
                if (rendered.Entry == null)
                {
                    Connect(sourceNodeId, join, label);
                    continue;
                }

                Connect(sourceNodeId, rendered.Entry, CombineEdgeLabels(label, rendered.EntryEdgeLabel));
                ConnectMany(rendered.Exits.Count == 0 ? new[] { rendered.Entry } : rendered.Exits, join);
            }

            return new List<string> { join };
        }

        private List<string> RenderCases(string sourceNodeId, StepDef step, string scope)
        {
            var join = AddSyntheticNode("join", "Join", NodeShape.Circle);
            var caseIndex = 0;

            if (step.Cases != null)
            {
                for (var i = 0; i < step.Cases.Count; i++)
                {
                    var switchCase = step.Cases[i];
                    var rendered = RenderStepList(switchCase.Steps, $"{scope}_{caseIndex++}");
                    var label = CaseLabel(switchCase, i);
                    if (rendered.Entry == null)
                    {
                        Connect(sourceNodeId, join, label);
                        continue;
                    }

                    Connect(sourceNodeId, rendered.Entry, CombineEdgeLabels(label, rendered.EntryEdgeLabel));
                    ConnectMany(rendered.Exits.Count == 0 ? new[] { rendered.Entry } : rendered.Exits, join);
                }
            }

            if (step.Default is { Count: > 0 } defaultSteps)
            {
                var rendered = RenderStepList(defaultSteps, $"{scope}_{caseIndex}");
                if (rendered.Entry == null)
                {
                    Connect(sourceNodeId, join, "default");
                }
                else
                {
                    Connect(sourceNodeId, rendered.Entry, CombineEdgeLabels("default", rendered.EntryEdgeLabel));
                    ConnectMany(rendered.Exits.Count == 0 ? new[] { rendered.Entry } : rendered.Exits, join);
                }
            }

            return new List<string> { join };
        }

        private string BuildStepLabel(StepDef step)
        {
            var lines = new List<string>
            {
                string.IsNullOrWhiteSpace(step.Id) ? "(unnamed)" : step.Id,
                string.IsNullOrWhiteSpace(step.Type) ? "(unknown)" : step.Type
            };

            if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal))
                AddWorkflowCallLabel(step, lines);
            else if (string.Equals(step.Type, "workflow.route", StringComparison.Ordinal))
                AddWorkflowRouteLabel(step, lines);
            else if (!string.IsNullOrWhiteSpace(step.Output))
                lines.Add("output: " + step.Output);

            if (ShouldRenderGuardInNode(step))
                lines.Add("if: " + step.If);

            return TrimLabel(string.Join(" - ", lines));
        }

        private void AddWorkflowCallLabel(StepDef step, List<string> lines)
        {
            if (TryGetLocalWorkflowName(step.Input, out var localName))
            {
                lines.Add(_document.Workflows.ContainsKey(localName)
                    ? $"local: {localName}"
                    : $"missing local: {localName}");
                return;
            }

            if (step.Input is JsonObject input && input["ref"] is JsonObject refObject)
            {
                var kind = GetString(refObject, "kind") ?? "local";
                var target = GetString(refObject, "url") ?? GetString(refObject, "path") ?? GetString(refObject, "name");
                lines.Add(string.IsNullOrWhiteSpace(target) ? kind : $"{kind}: {target}");
            }
        }

        private void AddWorkflowRouteLabel(StepDef step, List<string> lines)
        {
            var localReferences = EnumerateRouteLocalWorkflowReferences(step.Input).Distinct(StringComparer.Ordinal).ToArray();
            if (localReferences.Length == 0)
            {
                lines.Add("dynamic candidates");
                return;
            }

            lines.Add("routes: " + string.Join(", ", localReferences));
        }

        private string TrimLabel(string label)
        {
            if (_options.MaxLabelLength <= 0 || label.Length <= _options.MaxLabelLength)
                return label;

            return label[..Math.Max(1, _options.MaxLabelLength - 3)] + "...";
        }

        private static NodeShape ShapeFor(StepDef step)
        {
            return step.Type switch
            {
                "switch" => NodeShape.Decision,
                "loop.sequential" or "loop.parallel" => NodeShape.Hexagon,
                "sequence" or "parallel" => NodeShape.Stadium,
                "workflow.call" or "workflow.route" or "workflow.execute" or "workflow.plan" => NodeShape.Subroutine,
                "llm.call" => NodeShape.Asymmetric,
                "mcp.call" or "mcp.list" or "chat_history.get" or "chat_history.append" => NodeShape.Database,
                "human.input" => NodeShape.Trapezoid,
                "assert.non_null" => NodeShape.Decision,
                "template.render" or "emit" => NodeShape.Document,
                "set" => NodeShape.Rounded,
                _ => NodeShape.Rectangle
            };
        }

        private bool IsHiddenStep(StepDef step)
            => string.Equals(step.Type, "emit", StringComparison.Ordinal) && !_options.IncludeEmitSteps;

        private static bool IsLoop(StepDef step)
            => step.Type is "loop.sequential" or "loop.parallel";

        private bool ShouldRenderGuardInNode(StepDef step)
            => _options.IncludeConditions
                && _options.GuardRenderMode == MermaidGuardRenderMode.NodeLabel
                && !string.IsNullOrWhiteSpace(step.If);

        private string? IncomingEdgeLabelFor(StepDef step)
            => _options.IncludeConditions
                && _options.GuardRenderMode == MermaidGuardRenderMode.EdgeLabel
                && !string.IsNullOrWhiteSpace(step.If)
                    ? "if: " + step.If
                    : null;

        private static string? CombineEdgeLabels(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first))
                return string.IsNullOrWhiteSpace(second) ? null : second;

            return string.IsNullOrWhiteSpace(second) ? first : $"{first}; {second}";
        }

        private static string? ChildEdgeLabel(StepDef step)
        {
            return step.Type switch
            {
                "loop.sequential" or "loop.parallel" => "loop body",
                "sequence" => "steps",
                _ => null
            };
        }

        private static string CaseLabel(SwitchCaseDef switchCase, int index)
        {
            if (!string.IsNullOrWhiteSpace(switchCase.Value))
                return switchCase.Value;

            if (!string.IsNullOrWhiteSpace(switchCase.When))
                return switchCase.When;

            return $"case {index + 1}";
        }

        private void AppendNode(string id, string label, NodeShape shape)
        {
            var safeLabel = EscapeLabel(label);
            var definition = shape switch
            {
                NodeShape.Circle => $"{id}((\"{safeLabel}\"))",
                NodeShape.Decision => $"{id}{{\"{safeLabel}\"}}",
                NodeShape.Document => $"{id}[/\"{safeLabel}\"/]",
                NodeShape.Rounded => $"{id}(\"{safeLabel}\")",
                NodeShape.Stadium => $"{id}([\"{safeLabel}\"])",
                NodeShape.Subroutine => $"{id}[[\"{safeLabel}\"]]",
                NodeShape.Database => $"{id}[(\"{safeLabel}\")]",
                NodeShape.Hexagon => $"{id}{{{{\"{safeLabel}\"}}}}",
                NodeShape.Asymmetric => $"{id}>\"{safeLabel}\"]",
                NodeShape.Trapezoid => $"{id}[/\"{safeLabel}\"\\]",
                _ => $"{id}[\"{safeLabel}\"]"
            };

            _nodes.Append("    ").AppendLine(definition);
        }

        private void Connect(string source, string target, string? label = null)
        {
            _edges.Append("    ")
                .Append(source)
                .Append(string.IsNullOrWhiteSpace(label) ? " --> " : $" -->|{EscapeEdgeLabel(label)}| ")
                .AppendLine(target);
            _edgeIndex++;
        }

        private string NewId(string? hint)
        {
            var builder = new StringBuilder();
            builder.Append('n').Append(_nodeIndex++);

            if (!string.IsNullOrWhiteSpace(hint))
            {
                builder.Append('_');
                foreach (var ch in hint)
                {
                    builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
                }
            }

            return builder.ToString();
        }

        public override string ToString()
        {
            if (_edgeIndex == 0)
                return _nodes.ToString();

            return _nodes.Append(_edges).ToString();
        }

        private static string EscapeLabel(string value)
            => value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "'", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);

        private static string EscapeEdgeLabel(string value)
            => value
                .Replace("|", "/", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
    }

    private sealed record RenderedStepList(string? Entry, IReadOnlyList<string> Exits, string? EntryEdgeLabel);

    private enum NodeShape
    {
        Rectangle,
        Rounded,
        Stadium,
        Circle,
        Decision,
        Document,
        Subroutine,
        Database,
        Hexagon,
        Asymmetric,
        Trapezoid
    }
}
