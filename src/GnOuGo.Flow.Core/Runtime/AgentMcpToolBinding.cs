using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>One operator-configured tool that may be included in an approved adaptive task.</summary>
public record AgentMcpToolBinding(string Server, string Method);

internal sealed record AgentMcpToolContract(AgentMcpToolBinding Binding, JsonObject Descriptor, string Id)
{
    internal static AgentMcpToolContract From(AgentMcpToolBinding binding, McpToolInfo tool)
    {
        if (tool.Meta?["gnougo"]?["management"]?["visibility"]?.ToString() == "management_only" ||
            tool.InputSchema is not JsonObject input || PlanningContractValidation.ValidateSchema(input, false).Count != 0)
            throw new InvalidOperationException("The configured adaptive tool lacks a usable business input contract.");
        var descriptor = new JsonObject
        {
            ["server"] = binding.Server, ["method"] = binding.Method, ["description"] = tool.Description,
            ["input_schema"] = input.DeepClone(), ["effect"] = tool.EffectKind,
            ["output_schema"] = tool.OutputContract is { Authoritative: true, Errors.Count: 0 } owned ? owned.Schema?.DeepClone() : null,
            ["metadata"] = tool.Meta?.DeepClone()
        };
        return new(binding, descriptor, AgentTaskContracts.McpToolId(descriptor));
    }
}
