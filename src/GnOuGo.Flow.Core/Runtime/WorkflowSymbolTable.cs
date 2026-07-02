using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed class WorkflowSymbolTable
{
    private readonly Dictionary<string, FlowTypeDescriptor> _stepOutputs;
    private readonly Dictionary<string, FlowTypeDescriptor> _dataVariables;
    private readonly Dictionary<string, HashSet<string>> _resourceTags;

    private WorkflowSymbolTable(
        string workflowName,
        IReadOnlyDictionary<string, FlowTypeDescriptor> workflowInputs,
        IReadOnlySet<string> allStepIds,
        Dictionary<string, FlowTypeDescriptor>? stepOutputs = null,
        Dictionary<string, FlowTypeDescriptor>? dataVariables = null,
        Dictionary<string, HashSet<string>>? resourceTags = null)
    {
        WorkflowName = workflowName;
        WorkflowInputs = workflowInputs;
        AllStepIds = allStepIds;
        _stepOutputs = stepOutputs == null
            ? new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal)
            : new Dictionary<string, FlowTypeDescriptor>(stepOutputs, StringComparer.Ordinal);
        _dataVariables = dataVariables == null
            ? new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal)
            : new Dictionary<string, FlowTypeDescriptor>(dataVariables, StringComparer.Ordinal);
        _resourceTags = resourceTags == null
            ? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            : resourceTags.ToDictionary(
                static pair => pair.Key,
                static pair => new HashSet<string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    public string WorkflowName { get; }
    public IReadOnlyDictionary<string, FlowTypeDescriptor> WorkflowInputs { get; }
    public IReadOnlySet<string> AllStepIds { get; }
    public IReadOnlyDictionary<string, FlowTypeDescriptor> StepOutputs => _stepOutputs;
    public IReadOnlyDictionary<string, FlowTypeDescriptor> DataVariables => _dataVariables;

    public static WorkflowSymbolTable Create(
        string workflowName,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlySet<string> allStepIds) =>
        new(
            workflowName,
            FlowTypeDescriptorConverter.InputMap(workflowInputs),
            allStepIds);

    public WorkflowSymbolTable Clone() =>
        new(WorkflowName, WorkflowInputs, AllStepIds, _stepOutputs, _dataVariables, _resourceTags);

    public bool HasStepOutput(string stepId) => _stepOutputs.ContainsKey(stepId);

    public bool TryGetStepOutput(string stepId, out FlowTypeDescriptor descriptor) =>
        _stepOutputs.TryGetValue(stepId, out descriptor!);

    public void SetStepOutput(string stepId, FlowTypeDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(stepId))
            _stepOutputs[stepId] = descriptor;
    }

    public bool TryGetDataVariable(string name, out FlowTypeDescriptor descriptor) =>
        _dataVariables.TryGetValue(name, out descriptor!);

    public void SetDataVariable(string name, FlowTypeDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _dataVariables[name] = descriptor;
    }

    public void MarkResource(string reference, string resourceKind)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(resourceKind))
            return;

        reference = NormalizeReference(reference);
        if (!_resourceTags.TryGetValue(reference, out var tags))
        {
            tags = new HashSet<string>(StringComparer.Ordinal);
            _resourceTags[reference] = tags;
        }

        tags.Add(resourceKind);
    }

    public bool HasResource(string reference, string resourceKind)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(resourceKind))
            return false;

        reference = NormalizeReference(reference);
        return _resourceTags.TryGetValue(reference, out var tags)
               && tags.Contains(resourceKind);
    }

    public IReadOnlyList<string> GetResourceKinds(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Array.Empty<string>();

        reference = NormalizeReference(reference);
        return _resourceTags.TryGetValue(reference, out var tags)
            ? tags.OrderBy(static tag => tag, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    public IReadOnlyList<string> ResourceReferences(string resourceKind, int take = int.MaxValue) =>
        _resourceTags
            .Where(pair => pair.Value.Contains(resourceKind))
            .Select(static pair => pair.Key)
            .OrderBy(static reference => reference, StringComparer.Ordinal)
            .Take(take)
            .ToArray();

    public void CopyResourceTags(string sourceReference, string targetReference)
    {
        foreach (var resourceKind in GetResourceKinds(sourceReference))
            MarkResource(targetReference, resourceKind);
    }

    public void CopyResourceTagsByPrefix(string sourcePrefix, string targetPrefix)
    {
        if (string.IsNullOrWhiteSpace(sourcePrefix) || string.IsNullOrWhiteSpace(targetPrefix))
            return;

        sourcePrefix = NormalizeReference(sourcePrefix);
        targetPrefix = NormalizeReference(targetPrefix);
        var sourceWithDot = sourcePrefix + ".";

        foreach (var (reference, tags) in _resourceTags.ToArray())
        {
            string? suffix = null;
            if (string.Equals(reference, sourcePrefix, StringComparison.Ordinal))
                suffix = "";
            else if (reference.StartsWith(sourceWithDot, StringComparison.Ordinal))
                suffix = reference[sourcePrefix.Length..];

            if (suffix == null)
                continue;

            foreach (var resourceKind in tags)
                MarkResource(targetPrefix + suffix, resourceKind);
        }
    }

    public IReadOnlyDictionary<string, JsonNode?> ToRuntimeSchemaMap()
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (stepId, descriptor) in _stepOutputs)
            map[stepId] = FlowTypeDescriptorConverter.ToRuntimeJsonSchema(descriptor);
        return map;
    }

    public IReadOnlyList<string> AvailableStepReferences() =>
        _stepOutputs.Keys
            .OrderBy(static id => id, StringComparer.Ordinal)
            .Select(static id => "data.steps." + id)
            .ToArray();

    public IReadOnlyList<string> AllowedPaths(string stepId, int take = int.MaxValue)
    {
        if (!_stepOutputs.TryGetValue(stepId, out var descriptor))
            return Array.Empty<string>();

        return FlowTypeDescriptorConverter
            .EnumerateAllowedPaths("data.steps." + stepId, descriptor)
            .Take(take)
            .ToArray();
    }

    public IReadOnlyList<string> AllowedDataVariablePaths(string name, int take = int.MaxValue)
    {
        if (!_dataVariables.TryGetValue(name, out var descriptor))
            return Array.Empty<string>();

        return FlowTypeDescriptorConverter
            .EnumerateAllowedPaths("data." + name, descriptor)
            .Take(take)
            .ToArray();
    }

    private static string NormalizeReference(string reference) =>
        reference.StartsWith("data.", StringComparison.Ordinal)
            ? reference["data.".Length..]
            : reference;
}
