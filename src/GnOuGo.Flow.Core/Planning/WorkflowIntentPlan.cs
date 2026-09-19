using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Business interpretation. Executable step types, transports and catalog contracts belong to the builder.</summary>
public sealed class WorkflowIntentPlan
{
    public string Summary { get; set; } = "";
    public List<IntentInput> Inputs { get; set; } = [];
    public List<IntentOperation> Operations { get; set; } = [];
    public List<IntentOutput> Outputs { get; set; } = [];
    public List<IntentSubflow> Subflows { get; set; } = [];
    public List<PlanningQuestion> Questions { get; set; } = [];
}

public sealed record IntentInput(string Name, IntentType? Type = null, bool Optional = false, IntentValue? Default = null);
public sealed record IntentOutput(string Name, IntentValue Value);
public sealed record IntentSubflow(string Name, List<IntentInput> Inputs, List<IntentOperation> Operations, List<IntentOutput> Outputs);
public sealed record IntentMember(string Name, IntentValue Value);
public sealed record IntentBlock(List<IntentOperation> Operations, IntentValue Result);
public sealed record IntentBranch(string Name, IntentBlock Body);

/// <summary>Only novel business values need an explicit type. There are no schema references or transport fields.</summary>
public sealed class IntentType
{
    public string Type { get; set; } = "string";
    public bool Nullable { get; set; }
    public List<string> Enum { get; set; } = [];
    public IntentType? Items { get; set; }
    public List<IntentField> Fields { get; set; } = [];
}
public sealed record IntentField(string Name, IntentType Type, bool Optional = false);

/// <summary>Literal, business reference or pure calculation over named values. Omitted arguments have no member.</summary>
public sealed class IntentValue
{
    public string Kind { get; set; } = "null";
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public bool? Boolean { get; set; }
    public string? Source { get; set; }
    public List<string> Path { get; set; } = [];
    public List<IntentMember> Members { get; set; } = [];
    public List<IntentValue> Items { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InvokeIntentOperation), "invoke")]
[JsonDerivedType(typeof(CalculateIntentOperation), "calculate")]
[JsonDerivedType(typeof(TransformIntentOperation), "transform")]
[JsonDerivedType(typeof(ChooseIntentOperation), "choose")]
[JsonDerivedType(typeof(EachIntentOperation), "each")]
[JsonDerivedType(typeof(ParallelIntentOperation), "parallel")]
[JsonDerivedType(typeof(CallIntentOperation), "call")]
[JsonDerivedType(typeof(CleanupIntentOperation), "cleanup")]
public abstract class IntentOperation
{
    public string Id { get; set; } = "";
    public string Purpose { get; set; } = "";
    /// <summary>Business ordering. In cleanup this does not require successful completion; bound resources and When determine availability.</summary>
    public List<string> After { get; set; } = [];
    public IntentValue? When { get; set; }
}
public sealed class InvokeIntentOperation : IntentOperation
{
    public string? Capability { get; set; }
    public List<IntentMember> Arguments { get; set; } = [];
    public IntentValue? Fallback { get; set; }
}
public sealed class CalculateIntentOperation : IntentOperation
{
    public IntentValue Value { get; set; } = new();
    public IntentType? ResultType { get; set; }
}
public sealed class TransformIntentOperation : IntentOperation
{
    public string Instruction { get; set; } = "";
    public List<IntentMember> Data { get; set; } = [];
    public IntentType? ResultType { get; set; }
}
public sealed class ChooseIntentOperation : IntentOperation
{
    public IntentValue Condition { get; set; } = new();
    public IntentBlock Then { get; set; } = new([], new());
    public IntentBlock Otherwise { get; set; } = new([], new());
}
public sealed class EachIntentOperation : IntentOperation
{
    public IntentValue Items { get; set; } = new();
    public bool Parallel { get; set; }
    public IntentBlock Body { get; set; } = new([], new());
}
public sealed class ParallelIntentOperation : IntentOperation
{
    public List<IntentBranch> Branches { get; set; } = [];
}
public sealed class CallIntentOperation : IntentOperation
{
    public string Flow { get; set; } = "";
    public List<IntentMember> Arguments { get; set; } = [];
}
public sealed class CleanupIntentOperation : IntentOperation
{
    public List<IntentOperation> Operations { get; set; } = [];
}

public sealed record PlanningQuestion(string Id, string Question, IntentType AnswerType);
/// <summary>Execution samples are separate from business intent and contain literal JSON only.</summary>
public sealed class PlanningFixtures
{
    public JsonObject? Inputs { get; set; }
    public List<PlanningObservation> Observations { get; set; } = [];
}
public sealed record PlanningObservation(string Workflow, string Node, List<JsonNode?> Responses);
public sealed record PlanningHole(string Id, string WorkflowKey, string? NodeKey, string Path, string Kind, JsonObject? ExpectedSchema, bool Optional = false);
