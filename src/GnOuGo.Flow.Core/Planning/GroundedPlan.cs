using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Bound operations. The validator establishes contracts before graph lowering.</summary>
public sealed class GroundedPlan
{
    public string Summary { get; set; } = "";
    public List<GroundedInput> Inputs { get; set; } = [];
    public List<GroundedOperation> Operations { get; set; } = [];
    public List<GroundedOutput> Outputs { get; set; } = [];
    public List<GroundedSubflow> Subflows { get; set; } = [];
}

public sealed record GroundedInput(string Name, BusinessType? Type = null, bool Optional = false, GroundedValue? Default = null);
public sealed record GroundedOutput(string Name, GroundedValue Value);
public sealed record GroundedBusinessOutput(string Name, List<string> Path);
public sealed record GroundedSubflow(string Name, List<GroundedInput> Inputs, List<GroundedOperation> Operations, List<GroundedOutput> Outputs);
public sealed record GroundedMember(string Name, GroundedValue Value);
public sealed record GroundedBlock(List<GroundedOperation> Operations, GroundedValue Result);
public sealed record GroundedBranch(string Name, GroundedBlock Body);

/// <summary>Only novel business values need an explicit type. There are no schema references or transport fields.</summary>
public sealed class BusinessType
{
    public string Type { get; set; } = "string";
    public bool Nullable { get; set; }
    public List<string> Enum { get; set; } = [];
    public BusinessType? Items { get; set; }
    public List<BusinessField> Fields { get; set; } = [];
}
public sealed record BusinessField(string Name, BusinessType Type, bool Optional = false);

/// <summary>Literal, business reference or pure calculation over named values. Omitted arguments have no member.</summary>
public sealed class GroundedValue
{
    public string Kind { get; set; } = "null";
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public bool? Boolean { get; set; }
    public string? Source { get; set; }
    public List<string> Path { get; set; } = [];
    public List<GroundedMember> Members { get; set; } = [];
    public List<GroundedValue> Items { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InvokeGroundedOperation), "invoke")]
[JsonDerivedType(typeof(CalculateGroundedOperation), "calculate")]
[JsonDerivedType(typeof(TransformGroundedOperation), "transform")]
[JsonDerivedType(typeof(ChooseGroundedOperation), "choose")]
[JsonDerivedType(typeof(EachGroundedOperation), "each")]
[JsonDerivedType(typeof(ParallelGroundedOperation), "parallel")]
[JsonDerivedType(typeof(CallGroundedOperation), "call")]
[JsonDerivedType(typeof(CleanupGroundedOperation), "cleanup")]
[JsonDerivedType(typeof(ValidateGroundedOperation), "validate")]
public abstract class GroundedOperation
{
    public string Id { get; set; } = "";
    public string SemanticAction { get; set; } = "";
    public List<GroundedBusinessOutput> BusinessOutputs { get; set; } = [];
    public string Purpose { get; set; } = "";
    /// <summary>Business ordering. In cleanup this does not require successful completion; bound resources and When determine availability.</summary>
    public List<string> After { get; set; } = [];
    public GroundedValue? When { get; set; }
}
public sealed class InvokeGroundedOperation : GroundedOperation
{
    public string? Capability { get; set; }
    public List<GroundedMember> Arguments { get; set; } = [];
    public GroundedValue? Fallback { get; set; }
}
public sealed class CalculateGroundedOperation : GroundedOperation
{
    public GroundedValue Value { get; set; } = new();
    public BusinessType? ResultType { get; set; }
}
public sealed class TransformGroundedOperation : GroundedOperation
{
    public string Instruction { get; set; } = "";
    public List<GroundedMember> Data { get; set; } = [];
    public BusinessType? ResultType { get; set; }
}
public sealed class ChooseGroundedOperation : GroundedOperation
{
    public GroundedValue Condition { get; set; } = new();
    public GroundedBlock Then { get; set; } = new([], new());
    public GroundedBlock Otherwise { get; set; } = new([], new());
}
public sealed class EachGroundedOperation : GroundedOperation
{
    public GroundedValue Items { get; set; } = new();
    public bool Parallel { get; set; }
    public GroundedBlock Body { get; set; } = new([], new());
}
public sealed class ParallelGroundedOperation : GroundedOperation
{
    public List<GroundedBranch> Branches { get; set; } = [];
}
public sealed class CallGroundedOperation : GroundedOperation
{
    public string Flow { get; set; } = "";
    public List<GroundedMember> Arguments { get; set; } = [];
}
public sealed class CleanupGroundedOperation : GroundedOperation
{
    public List<GroundedOperation> Operations { get; set; } = [];
}

/// <summary>Explicit runtime validation of a whole value before typed projection.</summary>
public sealed class ValidateGroundedOperation : GroundedOperation
{
    public GroundedValue Value { get; set; } = new();
    public string Format { get; set; } = "json_value";
    public BusinessType ResultType { get; set; } = new();
}

public sealed record PlanningQuestion(string Id, string Question, BusinessType AnswerType);
/// <summary>Execution samples are separate from business intent and contain literal JSON only.</summary>
public sealed class PlanningFixtures
{
    public JsonObject? Inputs { get; set; }
    public List<PlanningObservation> Observations { get; set; } = [];
}
public sealed record PlanningObservation(string Workflow, string Node, List<JsonNode?> Responses);
