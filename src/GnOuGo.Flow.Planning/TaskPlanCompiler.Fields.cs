using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private Bound SelectField(TaskValue value, Scope scope, bool consume)
    {
        if (value.Items.Count != 1 || string.IsNullOrWhiteSpace(value.Port) || value.Source is not null ||
            value.Text is not null || value.Number is not null || value.Boolean is not null || value.Predicate is not null || value.Members.Count != 0)
            Fail("TASK_FIELD_INVALID", "Field selection requires one business object and a nonblank literal field name.");
        var source = Value(value.Items[0], scope, consume: false);
        // A nullable object still declares its fields. The checked projection fails
        // on a null container; it never supplies a default or establishes success.
        var type = source.Schema["type"];
        var isObject = type?.ToString() == "object" || type is JsonArray types &&
            types.Any(t => t?.ToString() == "object") && types.All(t => t?.ToString() is "object" or "null");
        if (!isObject || source.Schema["x-gnougo-opaque"]?.ToString() == "true")
            Fail("TASK_FIELD_TYPE", "Field selection requires an authoritative object contract; opaque values cannot supply fields.");
        if (source.Schema["properties"] is not JsonObject fields || fields[value.Port!] is not JsonObject)
            Fail("TASK_FIELD_UNKNOWN", "The source contract does not declare field '" + value.Port + "'. Field names are literal, not paths.");
        var schema = source.Schema["properties"]![value.Port!]!.AsObject();
        // Only an unambiguous semantic location can grant producer-type repairs.
        var location = source.TypeLocation is { } declared && !value.Port!.Contains('/')
            ? declared + "/fields/" + value.Port + "/type" : null;
        // Compose explicit nested selections against the authoritative container.
        // Emit one check at consumption, including inside branches and finalizers.
        var selected = new Bound(new() { Kind = "output" }, schema,
            SelectionSource: source.SelectionSource ?? source, SelectionPath: [.. source.SelectionPath ?? [], value.Port!], TypeLocation: location);
        return consume ? Consume(selected, scope) : selected;
    }
}
