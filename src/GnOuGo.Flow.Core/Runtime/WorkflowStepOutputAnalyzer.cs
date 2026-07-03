using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed record WorkflowStepOutputAnalysis(
    IReadOnlyDictionary<string, FlowTypeDescriptor> StepOutputs,
    WorkflowSymbolTable Symbols);

internal static class WorkflowStepOutputAnalyzer
{
    public static WorkflowStepOutputAnalysis AnalyzeWorkflow(
        string workflowName,
        WorkflowDef workflow,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        IReadOnlyDictionary<(string ServerName, string ToolName), McpToolOutputContract>? mcpContracts = null,
        IReadOnlyDictionary<string, StepContract>? stepContracts = null)
    {
        var allStepIds = EnumerateSteps(workflow.Steps)
            .Select(static step => step.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var symbols = WorkflowSymbolTable.Create(workflowName, workflow.Inputs, allStepIds);
        AnalyzeStepList(
            workflow.Steps,
            workflowName,
            workflows,
            symbols,
            mcpContracts ?? new Dictionary<(string ServerName, string ToolName), McpToolOutputContract>(),
            stepContracts ?? BuiltInStepContracts.All);

        return new WorkflowStepOutputAnalysis(
            symbols.StepOutputs.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            symbols);
    }

    public static FlowTypeDescriptor BuildParallelOutputType(IReadOnlyList<FlowTypeDescriptor> branchSnapshots)
    {
        var branchItemType = branchSnapshots.Count == 0
            ? FlowTypeDescriptor.Object()
            : FlowTypeDescriptor.Union(branchSnapshots);

        return FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
        {
            ["branches"] = new(FlowTypeDescriptor.Array(branchItemType), Required: true)
        });
    }

    public static FlowTypeDescriptor BuildSwitchOutputType(
        IReadOnlyList<FlowTypeDescriptor> branchSnapshots,
        bool hasDefault)
    {
        var variants = new List<FlowTypeDescriptor>(branchSnapshots);
        if (!hasDefault)
            variants.Add(FlowTypeDescriptor.Null);

        return variants.Count == 0
            ? FlowTypeDescriptor.Null
            : FlowTypeDescriptor.Union(variants);
    }

    public static FlowTypeDescriptor BuildLoopOutputType(WorkflowSymbolTable loopSymbols)
    {
        var iterationStepOutputs = loopSymbols.StepOutputs.ToDictionary(
            static pair => pair.Key,
            static pair => new FlowPropertyDescriptor(pair.Value, Required: true),
            StringComparer.Ordinal);

        return FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
        {
            ["results"] = new(FlowTypeDescriptor.Array(FlowTypeDescriptor.Object(iterationStepOutputs)), Required: true),
            ["count"] = new(FlowTypeDescriptor.Integer, Required: true)
        });
    }

    public static FlowTypeDescriptor BuildSequenceOutputType(
        WorkflowSymbolTable sequenceSymbols,
        IReadOnlySet<string> stepIdsBeforeSequence)
    {
        var sequenceStepOutputs = sequenceSymbols.StepOutputs
            .Where(pair => !stepIdsBeforeSequence.Contains(pair.Key))
            .ToDictionary(
                static pair => pair.Key,
                static pair => new FlowPropertyDescriptor(pair.Value, Required: true),
                StringComparer.Ordinal);

        return FlowTypeDescriptor.Object(sequenceStepOutputs);
    }

    public static FlowTypeDescriptor BuildStepSnapshotOutputType(WorkflowSymbolTable symbols)
    {
        var stepOutputs = symbols.StepOutputs.ToDictionary(
            static pair => pair.Key,
            static pair => new FlowPropertyDescriptor(pair.Value, Required: true),
            StringComparer.Ordinal);

        return FlowTypeDescriptor.Object(stepOutputs);
    }

    private static void AnalyzeStepList(
        IReadOnlyList<StepDef> steps,
        string workflowName,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        WorkflowSymbolTable symbols,
        IReadOnlyDictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        foreach (var step in steps)
            AnalyzeStep(step, workflowName, workflows, symbols, mcpContracts, stepContracts);
    }

    private static void AnalyzeStep(
        StepDef step,
        string workflowName,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        WorkflowSymbolTable symbols,
        IReadOnlyDictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        var stepIsConditional = !string.IsNullOrWhiteSpace(step.If);
        FlowTypeDescriptor? resolvedStepOutputType = null;

        if (step.Type == "parallel" && step.Branches != null)
        {
            var branchSnapshots = new List<FlowTypeDescriptor>();
            foreach (var branch in step.Branches)
            {
                var branchSymbols = symbols.Clone();
                AnalyzeStepList(branch.Steps, workflowName, workflows, branchSymbols, mcpContracts, stepContracts);
                branchSnapshots.Add(BuildStepSnapshotOutputType(branchSymbols));
            }

            if (branchSnapshots.Count > 0)
                resolvedStepOutputType = BuildParallelOutputType(branchSnapshots);
        }
        else if (step.Type == "switch")
        {
            var branchSnapshots = new List<FlowTypeDescriptor>();
            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                {
                    var caseSymbols = symbols.Clone();
                    AnalyzeStepList(@case.Steps, workflowName, workflows, caseSymbols, mcpContracts, stepContracts);
                    branchSnapshots.Add(BuildStepSnapshotOutputType(caseSymbols));
                }
            }

            if (step.Default is { Count: > 0 })
            {
                var defaultSymbols = symbols.Clone();
                AnalyzeStepList(step.Default, workflowName, workflows, defaultSymbols, mcpContracts, stepContracts);
                branchSnapshots.Add(BuildStepSnapshotOutputType(defaultSymbols));
            }

            resolvedStepOutputType = BuildSwitchOutputType(branchSnapshots, hasDefault: step.Default is { Count: > 0 });
        }
        else if (step.Type is "loop.sequential" or "loop.parallel")
        {
            if (step.Steps != null)
            {
                var loopSymbols = symbols.Clone();
                AddLoopScopedDataVariables(step, loopSymbols, symbols);
                AnalyzeStepList(step.Steps, workflowName, workflows, loopSymbols, mcpContracts, stepContracts);
                resolvedStepOutputType = BuildLoopOutputType(loopSymbols);
            }
        }
        else if (step.Type == "sequence")
        {
            if (step.Steps != null)
            {
                var beforeStepIds = symbols.StepOutputs.Keys.ToHashSet(StringComparer.Ordinal);
                if (stepIsConditional)
                {
                    var conditionalSymbols = symbols.Clone();
                    AnalyzeStepList(step.Steps, workflowName, workflows, conditionalSymbols, mcpContracts, stepContracts);
                    resolvedStepOutputType = BuildSequenceOutputType(conditionalSymbols, beforeStepIds);
                }
                else
                {
                    AnalyzeStepList(step.Steps, workflowName, workflows, symbols, mcpContracts, stepContracts);
                    resolvedStepOutputType = BuildSequenceOutputType(symbols, beforeStepIds);
                }
            }
        }
        else if (step.Steps != null)
        {
            if (stepIsConditional)
            {
                var conditionalSymbols = symbols.Clone();
                AnalyzeStepList(step.Steps, workflowName, workflows, conditionalSymbols, mcpContracts, stepContracts);
            }
            else
            {
                AnalyzeStepList(step.Steps, workflowName, workflows, symbols, mcpContracts, stepContracts);
            }
        }

        if (stepIsConditional || string.IsNullOrWhiteSpace(step.Id))
            return;

        var outputType = resolvedStepOutputType ?? StepOutputTypeResolver.Resolve(
            step,
            workflows,
            symbols,
            mcpContracts,
            stepContracts);
        symbols.SetStepOutput(step.Id, outputType);
        if (!string.IsNullOrWhiteSpace(step.Output))
            symbols.SetDataVariable(step.Output, outputType);
    }

    private static void AddLoopScopedDataVariables(
        StepDef step,
        WorkflowSymbolTable loopSymbols,
        WorkflowSymbolTable outerSymbols)
    {
        var itemType = InferLoopItemType(step, outerSymbols);
        if (itemType != null)
        {
            var itemVar = step.ItemVar ?? "item";
            var indexVar = step.IndexVar ?? "i";
            loopSymbols.SetDataVariable(itemVar, itemType);
            loopSymbols.SetDataVariable(indexVar, FlowTypeDescriptor.Integer);
            loopSymbols.SetDataVariable("_loop", LoopContextType(itemType));
            loopSymbols.SetDataVariable("loop", LoopContextType(itemType));
            return;
        }

        if (string.Equals(step.Type, "loop.sequential", StringComparison.Ordinal))
        {
            var loopContext = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
            {
                ["index"] = new(FlowTypeDescriptor.Integer, Required: true)
            });
            loopSymbols.SetDataVariable("_loop", loopContext);
            loopSymbols.SetDataVariable("loop", loopContext);
        }
    }

    private static FlowTypeDescriptor? InferLoopItemType(StepDef step, WorkflowSymbolTable symbols)
    {
        if (step.Input is not JsonObject input)
            return null;

        JsonNode? itemsNode = null;
        if (input.TryGetPropertyValue("items", out var items) && items != null)
            itemsNode = items;
        else if (string.Equals(step.Type, "loop.sequential", StringComparison.Ordinal)
                 && input.TryGetPropertyValue("over", out var over) && over != null)
            itemsNode = over;

        if (itemsNode == null)
            return null;

        var itemsType = StepExpressionTypeValidator.InferValueType(
            itemsNode,
            symbols.WorkflowInputs,
            symbols.StepOutputs,
            symbols.DataVariables);

        return ExtractArrayItemType(itemsType);
    }

    private static FlowTypeDescriptor ExtractArrayItemType(FlowTypeDescriptor? descriptor)
    {
        if (descriptor == null || descriptor.IsOpaque)
            return FlowTypeDescriptor.Any;

        if (descriptor.Kind == FlowTypeKind.Array)
            return descriptor.Items ?? FlowTypeDescriptor.Any;

        if (descriptor.Kind == FlowTypeKind.Union)
        {
            var itemTypes = descriptor.Variants
                .Where(static variant => variant.Kind == FlowTypeKind.Array)
                .Select(static variant => variant.Items ?? FlowTypeDescriptor.Any)
                .ToArray();

            return itemTypes.Length == 0
                ? FlowTypeDescriptor.Any
                : FlowTypeDescriptor.Union(itemTypes);
        }

        return FlowTypeDescriptor.Any;
    }

    private static FlowTypeDescriptor LoopContextType(FlowTypeDescriptor itemType) =>
        FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
        {
            ["index"] = new(FlowTypeDescriptor.Integer, Required: true),
            ["item"] = new(itemType, Required: true)
        });

    private static IEnumerable<StepDef> EnumerateSteps(IEnumerable<StepDef>? steps)
    {
        if (steps == null)
            yield break;

        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in EnumerateSteps(step.Steps))
                yield return child;
            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                foreach (var child in EnumerateSteps(branch.Steps))
                    yield return child;
            }
            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                foreach (var child in EnumerateSteps(@case.Steps))
                    yield return child;
            }
            foreach (var child in EnumerateSteps(step.Default))
                yield return child;
        }
    }
}
