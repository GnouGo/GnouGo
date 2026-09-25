using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Editable business intent. Executor types, wire bindings and projections belong to the compiler.</summary>
public sealed class TaskPlan
{
    public List<TaskInput> Inputs { get; set; } = [];
    public TaskScope Root { get; set; } = new();
    public List<TaskGroup> Groups { get; set; } = [];
    public List<PlanningChoice> Choices { get; set; } = [];
}

public sealed class TaskScope
{
    public List<PlanTask> Tasks { get; set; } = [];
    public List<TaskOutput> Outputs { get; set; } = [];
    public List<PlanTask> Always { get; set; } = [];
}

public sealed class PlanTask
{
    public string Id { get; set; } = "";
    public string Objective { get; set; } = "";
    public string Kind { get; set; } = "operation";
    public List<string> DependsOn { get; set; } = [];
    public string? Operation { get; set; }
    public List<TaskOutput> Inputs { get; set; } = [];
    public List<TaskOutput> Outputs { get; set; } = [];
    /// <summary>Transform results: a closed object of required, fully typed business fields.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskType? ResultType { get; set; }
    public TaskValue? Condition { get; set; }
    public TaskScope? Body { get; set; }
    public TaskScope? Otherwise { get; set; }
    public List<TaskScope> Branches { get; set; } = [];
    public TaskValue? Items { get; set; }
    public bool Parallel { get; set; }
    public int MaxItems { get; set; } = 100;
    public int MaxConcurrency { get; set; } = 4;
    public string? Group { get; set; }
}

public sealed class TaskGroup
{
    public string Id { get; set; } = "";
    public List<TaskInput> Inputs { get; set; } = [];
    public TaskScope Body { get; set; } = new();
}

public sealed class TaskInput
{
    public string Name { get; set; } = "";
    public TaskType Type { get; set; } = new();
    public bool Required { get; set; } = true;
    public TaskValue? Default { get; set; }
}

/// <summary>Business boundary types; never a pointer into an executor or provider contract.</summary>
public sealed class TaskType
{
    public string Kind { get; set; } = "string";
    public bool Nullable { get; set; }
    public TaskType? Items { get; set; }
    public List<TaskInput> Fields { get; set; } = [];
}

public sealed record TaskOutput(string Name, TaskValue Value);

/// <summary>Closed semantic values. An output reference names a task and one declared business port.</summary>
public sealed class TaskValue
{
    public string Kind { get; set; } = "null";
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public bool? Boolean { get; set; }
    public string? Source { get; set; }
    public string? Port { get; set; }
    public List<TaskOutput> Members { get; set; } = [];
    public List<TaskValue> Items { get; set; } = [];
    public string? Predicate { get; set; }
}

/// <summary>A value-slot decision, never an execution permission or an arbitrary plan patch.</summary>
public sealed class PlanningChoice
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public TaskType Type { get; set; } = new();
    public List<PlanningAlternative> Alternatives { get; set; } = [];
    public string Recommended { get; set; } = "";
    public string? Selected { get; set; }
}

public sealed record PlanningAlternative(string Id, string Description, TaskValue Value);

/// <summary>Versioned producer-owned binding metadata. Paths are never supplied by a model.</summary>
public sealed class PlanningOperation
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public List<OperationPort> Inputs { get; set; } = [];
    public List<OperationPort> Outputs { get; set; } = [];
}

public sealed class OperationPort
{
    public string Name { get; set; } = "";
    public JsonObject Schema { get; set; } = new();
    public bool Required { get; set; }
    public List<string> Path { get; set; } = [];
}
