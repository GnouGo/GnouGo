using System.Diagnostics;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Stable telemetry context carried explicitly across async iterator boundaries.
/// </summary>
internal readonly record struct AgentTraceContext(
    ActivityContext ParentContext,
    string CorrelationId);
