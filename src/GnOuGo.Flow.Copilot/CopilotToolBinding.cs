using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Copilot;

public sealed record CopilotToolBinding(string Server, string Method) : AgentMcpToolBinding(Server, Method);
