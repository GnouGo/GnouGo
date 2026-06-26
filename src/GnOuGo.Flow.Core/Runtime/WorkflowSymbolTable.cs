using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed class WorkflowSymbolTable
{
    private readonly Dictionary<string, FlowTypeDescriptor> _stepOutputs;
    private readonly Dictionary<string, FlowTypeDescriptor> _dataVariables;

    private WorkflowSymbolTable(
        string workflowName,
        IReadOnlyDictionary<string, FlowTypeDescriptor> workflowInputs,
        IReadOnlySet<string> allStepIds,
        Dictionary<string, FlowTypeDescriptor>? stepOutputs = null,
        Dictionary<string, FlowTypeDescriptor>? dataVariables = null)
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
        new(WorkflowName, WorkflowInputs, AllStepIds, _stepOutputs, _dataVariables);

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
}
