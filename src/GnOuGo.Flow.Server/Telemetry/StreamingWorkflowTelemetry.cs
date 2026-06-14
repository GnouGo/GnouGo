using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Server.Telemetry;

/// <summary>
/// Decorates the configured workflow telemetry and mirrors the activity into a
/// stream-friendly feed for the browser runner.
/// </summary>
public sealed class StreamingWorkflowTelemetry : IWorkflowTelemetry
{
	private readonly IWorkflowTelemetry _inner;
	private readonly Action<WorkflowStreamEvent> _emit;
	private readonly object _gate = new();
	private readonly HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);

	private long _inputTokens;
	private long _outputTokens;
	private decimal _estimatedCostUsd;
	private bool _hasEstimatedCost;
	private int _tokenizedStepCount;
	private int _pricedStepCount;

	public StreamingWorkflowTelemetry(IWorkflowTelemetry inner, Action<WorkflowStreamEvent> emit)
	{
		_inner = inner;
		_emit = emit;
	}

	public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
	{
		var innerSpan = _inner.WorkflowStart(info);
		Emit("workflow.started", new WorkflowStartedStreamData(
			WorkflowName: info.WorkflowName,
			DocumentName: info.DocumentName,
			Inputs: info.Inputs?.DeepClone()));
		return new StreamingWorkflowSpan(innerSpan);
	}

	public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info)
	{
		var innerParentSpan = parentSpan switch
		{
			StreamingWorkflowSpan streamingSpan => streamingSpan.Inner,
			StreamingStepSpan streamingStepSpan => streamingStepSpan.Inner,
			StreamingTelemetrySpan streamingTelemetrySpan => streamingTelemetrySpan.Inner,
			_ => parentSpan
		};
		var innerSpan = _inner.WorkflowStart(innerParentSpan, info);
		Emit("workflow.started", new WorkflowStartedStreamData(
			WorkflowName: info.WorkflowName,
			DocumentName: info.DocumentName,
			Inputs: info.Inputs?.DeepClone()));
		return new StreamingWorkflowSpan(innerSpan);
	}

	public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
	{
		var innerSpan = span is StreamingWorkflowSpan streamingSpan ? streamingSpan.Inner : span;
		_inner.WorkflowEnd(innerSpan, result);

		Emit("workflow.completed", new WorkflowCompletedStreamData(
			Success: result.Success,
			StepsExecuted: result.StepsExecuted,
			DurationMs: result.Duration.TotalMilliseconds,
			ErrorCode: result.ErrorCode,
			ErrorMessage: result.ErrorMessage,
			Summary: GetSummarySnapshot()));
	}

	public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
	{
		var innerParentSpan = parentSpan switch
		{
			StreamingWorkflowSpan streamingSpan => streamingSpan.Inner,
			StreamingStepSpan streamingStepSpan => streamingStepSpan.Inner,
			_ => parentSpan
		};
		var innerStepSpan = _inner.StepStart(innerParentSpan, info);

		Emit("step.started", new StepStartedStreamData(
			StepId: info.StepId,
			StepType: info.StepType,
			CallDepth: info.CallDepth,
			Input: info.Input?.DeepClone()));

		return new StreamingStepSpan(innerStepSpan, info, _emit);
	}

	public void StepEnd(IStepSpan span, StepResultInfo result)
	{
		var streamingSpan = span as StreamingStepSpan;
		var innerStepSpan = streamingSpan?.Inner ?? span;
		var stepInfo = streamingSpan?.Info ?? new StepTelemetryInfo();
		var attributes = streamingSpan?.GetAttributesSnapshot()
			?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

		var usage = BuildUsage(attributes, result);
		var enrichedResult = result with
		{
			GenAiFinishReason = usage.FinishReason ?? result.GenAiFinishReason,
			GenAiInputTokens = usage.InputTokens ?? result.GenAiInputTokens,
			GenAiOutputTokens = usage.OutputTokens ?? result.GenAiOutputTokens
		};

		_inner.StepEnd(innerStepSpan, enrichedResult);
		UpdateSummary(usage);

		Emit("step.completed", new StepCompletedStreamData(
			StepId: stepInfo.StepId,
			StepType: stepInfo.StepType,
			CallDepth: stepInfo.CallDepth,
			Status: result.Status.ToString(),
			DurationMs: result.Duration.TotalMilliseconds,
			Output: result.Output?.DeepClone(),
			ErrorCode: result.ErrorCode,
			ErrorMessage: result.ErrorMessage,
			Attributes: attributes,
			Usage: usage));

		Emit("workflow.summary", new WorkflowSummaryStreamData(GetSummarySnapshot()));
	}

	public ITelemetrySpan SpanStart(ITelemetrySpan parentSpan, TelemetrySpanInfo info)
	{
		var innerParentSpan = parentSpan switch
		{
			StreamingWorkflowSpan streamingSpan => streamingSpan.Inner,
			StreamingStepSpan streamingStepSpan => streamingStepSpan.Inner,
			StreamingTelemetrySpan streamingTelemetrySpan => streamingTelemetrySpan.Inner,
			_ => parentSpan
		};
		var innerSpan = _inner.SpanStart(innerParentSpan, info);
		Emit("workflow.phase.started", new
		{
			info.Name,
			info.Phase,
			info.StepId,
			info.StepType,
			info.CallDepth,
			Attributes = info.Attributes?.ToDictionary(kv => kv.Key, kv => kv.Value)
		});
		return new StreamingTelemetrySpan(innerSpan);
	}

	public void SpanEnd(ITelemetrySpan span, TelemetrySpanResultInfo result)
	{
		var streamingSpan = span as StreamingTelemetrySpan;
		var innerSpan = streamingSpan?.Inner ?? span;
		_inner.SpanEnd(innerSpan, result);
		Emit("workflow.phase.completed", new
		{
			result.Success,
			DurationMs = result.Duration.TotalMilliseconds,
			result.ErrorType,
			result.ErrorMessage
		});
	}

	public WorkflowUsageSummary GetSummarySnapshot()
	{
		lock (_gate)
		{
			return new WorkflowUsageSummary(
				InputTokens: _inputTokens,
				OutputTokens: _outputTokens,
				TotalTokens: _inputTokens + _outputTokens,
				EstimatedCostUsd: _hasEstimatedCost ? _estimatedCostUsd : null,
				TokenizedStepCount: _tokenizedStepCount,
				PricedStepCount: _pricedStepCount,
				Models: _models.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
		}
	}

	private void UpdateSummary(StepUsageSummary usage)
	{
		lock (_gate)
		{
			if (!string.IsNullOrWhiteSpace(usage.Model))
				_models.Add(usage.Model);

			if (usage.InputTokens.HasValue || usage.OutputTokens.HasValue)
			{
				_inputTokens += usage.InputTokens ?? 0;
				_outputTokens += usage.OutputTokens ?? 0;
				_tokenizedStepCount++;
			}

			if (usage.EstimatedCostUsd.HasValue)
			{
				_estimatedCostUsd += usage.EstimatedCostUsd.Value;
				_hasEstimatedCost = true;
				_pricedStepCount++;
			}
		}
	}

	private StepUsageSummary BuildUsage(IReadOnlyDictionary<string, object?> attributes, StepResultInfo result)
	{
		var model = GetString(attributes, "gen_ai.request.model")
			?? GetString(attributes, "gen_ai.response.model");
		var system = GetString(attributes, "gen_ai.system");
		var inputTokens = GetLong(attributes, "gen_ai.usage.input_tokens") ?? result.GenAiInputTokens;
		var outputTokens = GetLong(attributes, "gen_ai.usage.output_tokens") ?? result.GenAiOutputTokens;
		var totalTokens = GetLong(attributes, "gen_ai.usage.total_tokens");
		if (!totalTokens.HasValue && (inputTokens.HasValue || outputTokens.HasValue))
			totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);

		var finishReason = GetString(attributes, "gen_ai.response.finish_reason") ?? result.GenAiFinishReason;
		var estimatedCostUsd = ModelMetadataCatalog.EstimateCost(model, inputTokens, outputTokens);

		return new StepUsageSummary(
			Model: model,
			System: system,
			InputTokens: inputTokens,
			OutputTokens: outputTokens,
			TotalTokens: totalTokens,
			EstimatedCostUsd: estimatedCostUsd,
			FinishReason: finishReason);
	}

	private void Emit(string type, object data) => _emit(new WorkflowStreamEvent(type, data));

	private static string? GetString(IReadOnlyDictionary<string, object?> attributes, string key) =>
		attributes.TryGetValue(key, out var value) ? value?.ToString() : null;

	private static long? GetLong(IReadOnlyDictionary<string, object?> attributes, string key)
	{
		if (!attributes.TryGetValue(key, out var value) || value == null)
			return null;

		return value switch
		{
			byte b => b,
			short s => s,
			int i => i,
			long l => l,
			float f => (long)f,
			double d => (long)d,
			decimal m => (long)m,
			JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed) => parsed,
			_ when long.TryParse(value.ToString(), out var parsed) => parsed,
			_ => null
		};
	}

	private static string? ExtractContentText(string name, IReadOnlyDictionary<string, object?> attributes)
	{
		return name switch
		{
			"gen_ai.content.prompt" when attributes.TryGetValue("gen_ai.prompt", out var prompt) => prompt?.ToString(),
			"gen_ai.content.completion" when attributes.TryGetValue("gen_ai.completion", out var completion) => completion?.ToString(),
			_ => null
		};
	}

	private static JsonNode? ExtractContentJson(string name, IReadOnlyDictionary<string, object?> attributes)
	{
		var key = name switch
		{
			"gnougo-flow.step.input" => "gnougo-flow.content.input",
			"gnougo-flow.step.output" => "gnougo-flow.content.output",
			_ => null
		};

		if (key == null || !attributes.TryGetValue(key, out var value) || value == null)
			return null;

		if (value is JsonNode node)
			return node.DeepClone();

		var text = value.ToString();
		if (string.IsNullOrWhiteSpace(text))
			return null;

		try
		{
			return JsonNode.Parse(text);
		}
		catch
		{
			return JsonValue.Create(text);
		}
	}

	private sealed class StreamingWorkflowSpan(IWorkflowSpan inner) : IWorkflowSpan
	{
		public IWorkflowSpan Inner { get; } = inner;
		public void SetAttribute(string key, object? value) => Inner.SetAttribute(key, value);
		public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null) => Inner.AddEvent(name, attributes);
		public void Dispose() => Inner.Dispose();
	}

	private sealed class StreamingTelemetrySpan(ITelemetrySpan inner) : ITelemetrySpan
	{
		public ITelemetrySpan Inner { get; } = inner;
		public void SetAttribute(string key, object? value) => Inner.SetAttribute(key, value);
		public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null) => Inner.AddEvent(name, attributes);
		public void Dispose() => Inner.Dispose();
	}

	private sealed class StreamingStepSpan : IStepSpan
	{
		private readonly object _gate = new();
		private readonly Action<WorkflowStreamEvent> _emit;
		private readonly Dictionary<string, object?> _attributes = new(StringComparer.OrdinalIgnoreCase);

		public StreamingStepSpan(IStepSpan inner, StepTelemetryInfo info, Action<WorkflowStreamEvent> emit)
		{
			Inner = inner;
			Info = info;
			_emit = emit;
		}

		public IStepSpan Inner { get; }
		public StepTelemetryInfo Info { get; }

		public void SetAttribute(string key, object? value)
		{
			lock (_gate)
			{
				_attributes[key] = value;
			}

			Inner.SetAttribute(key, value);
		}

		public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
		{
			Inner.AddEvent(name, attributes);

			var copied = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
			if (attributes != null)
			{
				foreach (var kv in attributes)
					copied[kv.Key] = kv.Value;
			}

			_emit(new WorkflowStreamEvent("step.event", new StepTelemetryEventStreamData(
				StepId: Info.StepId,
				StepType: Info.StepType,
				CallDepth: Info.CallDepth,
				Name: name,
				Attributes: copied,
				ContentText: ExtractContentText(name, copied),
				ContentJson: ExtractContentJson(name, copied))));
		}

		public IReadOnlyDictionary<string, object?> GetAttributesSnapshot()
		{
			lock (_gate)
			{
				return new Dictionary<string, object?>(_attributes, StringComparer.OrdinalIgnoreCase);
			}
		}

		public void Dispose() => Inner.Dispose();
	}
}
