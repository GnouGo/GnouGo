namespace GnOuGo.Agent.Mcp.Services;

/// <summary>
/// Raised when attempting to create or rename an agent to a name that already exists.
/// </summary>
public sealed class DuplicateAgentNameException : Exception
{
    public DuplicateAgentNameException(string agentName)
        : base($"An agent named '{agentName}' already exists.")
    {
    }
}


