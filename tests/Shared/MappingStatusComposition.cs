using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Planning.Examples;

/// <summary>Explicit offline proposal edit, never an automatic compiler/runtime rewrite.</summary>
public static class MappingStatusComposition
{
    public static void Separate(TaskScope scope, string producerId, string extractionId, string statusId)
    {
        var extraction = scope.Tasks.Single(t => t.Id == extractionId);
        var snapshot = Field(Output(producerId), "observationSnapshot");
        var complete = Predicate("and", Predicate("not", Field(snapshot, "captureTruncated")),
            Predicate("not", Field(snapshot, "manifestTruncated")));
        var guard = Predicate("and", Clone(extraction.Requires!), complete);
        extraction.Requires = guard;
        extraction.ResultType!.Fields.RemoveAll(f => f.Name == "status");
        extraction.Objective = "Copy the requested observed facts and complete descriptive blocks verbatim. Preserve permitted null facts. Extract facts only; acquisition status is assembled separately from the producer result.";
        var status = new PlanTask
        {
            Id = statusId, Kind = "value", Objective = "Assemble the acquisition outcome from its declared result and complete snapshot, without inference.",
            DependsOn = [extractionId], Requires = Clone(guard), Outputs = [new("status", new()
            {
                Kind = "object", Members = [
                    new("success", Field(Output(producerId), "success")),
                    new("availability", Predicate("not_equal", snapshot, new() { Kind = "null" })),
                    new("completeness", complete),
                    new("message", Field(Output(producerId), "error_message"))]
            })]
        };
        scope.Tasks.Insert(scope.Tasks.IndexOf(extraction) + 1, status);
        var index = scope.Outputs.FindIndex(o => o.Name == "status");
        scope.Outputs[index] = new("status", new() { Kind = "output", Source = statusId, Port = "status" });
    }
    private static TaskValue Clone(TaskValue value) => JsonSerializer.Deserialize(JsonSerializer.Serialize(value, PlanningJsonContext.Default.TaskValue), PlanningJsonContext.Default.TaskValue)!;
    private static TaskValue Output(string id) => new() { Kind = "output", Source = id };
    private static TaskValue Field(TaskValue value, string port) => new() { Kind = "field", Port = port, Items = [value] };
    private static TaskValue Predicate(string name, params TaskValue[] values) => new() { Kind = "predicate", Predicate = name, Items = values.ToList() };
}
