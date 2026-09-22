using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

public sealed class PlanningGraph
{
    public string Summary { get; set; } = "";
    public List<PlanningWorkflow> Workflows { get; set; } = [];
    public string Entrypoint { get; set; } = "main";
    public string? Functions { get; set; }
}

public sealed class PlanningWorkflow
{
    public string Key { get; set; } = "main";
    public string Purpose { get; set; } = "";
    public List<PlanningPort> Inputs { get; set; } = [];
    public List<PlanningOutput> Outputs { get; set; } = [];
    public List<PlanningNode> Steps { get; set; } = [];
    public List<PlanningNode> Finally { get; set; } = [];
    public string? Functions { get; set; }
}

public sealed class PlanningPort
{
    public string Name { get; set; } = "";
    public PlanningSchema Schema { get; set; } = new();
    public bool Required { get; set; } = true;
    public PlanningValue? Default { get; set; }
}

public sealed class PlanningOutput
{
    public string Name { get; set; } = "";
    public PlanningSchema Schema { get; set; } = new();
    public PlanningValue Value { get; set; } = new();
}

/// <summary>A structural schema or an exact JSON pointer into an authoritative capability schema.</summary>
public sealed class PlanningSchema
{
    /// <summary>Host-established complete contract, including explicit opaque values.</summary>
    public JsonObject? Contract { get; set; }
    public string Type { get; set; } = "string";
    public bool Nullable { get; set; }
    public string? Description { get; set; }
    public List<string> Enum { get; set; } = [];
    public PlanningSchema? Items { get; set; }
    public List<PlanningPort> Properties { get; set; } = [];
    public PlanningSchema? AdditionalProperties { get; set; }
    public string? CapabilityId { get; set; }
    public string? SchemaPointer { get; set; }
}

/// <summary>Literal, input/output reference, expression, object, array, or workflow reference.</summary>
public sealed class PlanningValue
{
    public string Kind { get; set; } = "null";
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public bool? Boolean { get; set; }
    public string? Source { get; set; }
    /// <summary>The default channel is the declared raw result; structured selects validated post-processing JSON.</summary>
    public string? ResultChannel { get; set; }
    public List<string> Path { get; set; } = [];
    public List<PlanningMember> Members { get; set; } = [];
    public List<PlanningValue> Items { get; set; } = [];
}

public sealed record PlanningMember(string Name, PlanningValue Value);

public sealed class PlanningNode
{
    /// <summary>Coordinator-owned pure adapter role; never supplied by model assignments.</summary>
    public string? InternalRole { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public string Key { get; set; } = "";
    public string Type { get; set; } = "set";
    public string Purpose { get; set; } = "";
    public string? CapabilityId { get; set; }
    public PlanningValue Input { get; set; } = new() { Kind = "object" };
    public PlanningValue? If { get; set; }
    public PlanningValue? Expr { get; set; }
    public PlanningSchema? OutputSchema { get; set; }
    public PlanningStructuredOutput? StructuredOutput { get; set; }
    public string? Output { get; set; }
    public string? ItemVar { get; set; }
    public string? IndexVar { get; set; }
    public Models.RetryPolicy? Retry { get; set; }
    public List<PlanningErrorCase> OnError { get; set; } = [];
    public List<PlanningNode> Steps { get; set; } = [];
    public List<PlanningBranch> Branches { get; set; } = [];
    public List<PlanningCase> Cases { get; set; } = [];
    public List<PlanningNode> Default { get; set; } = [];
}

public sealed record PlanningBranch(List<PlanningNode> Steps);
public sealed record PlanningStructuredOutput(PlanningSchema Schema, bool Strict = true);
public sealed record PlanningCase(string? Value, PlanningValue? When, List<PlanningNode> Steps);
public sealed record PlanningErrorCase(PlanningValue? If, string Action, PlanningValue? SetOutput, Models.RetryPolicy? Retry);
