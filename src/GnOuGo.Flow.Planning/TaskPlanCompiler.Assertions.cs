using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private sealed record AssertionFact(string Reference, string Literal, bool Equal, string Location, string Contract);

    // Explicit immutable references only. No producer guarantees, implication solver,
    // branch joins, call-site substitution or facts carried between loop iterations.
    // Called after authoritative symbol/type/availability checks, before lowering.
    private List<PlanningDiagnostic> AssertionContradictions()
    {
        var findings = new List<PlanningDiagnostic>();
        Inspect(_plan.Root, []);
        foreach (var group in _plan.Groups) Inspect(group.Body, []);
        return findings;

        void Inspect(TaskScope scope, List<AssertionFact> inherited)
        {
            Tasks(scope.Tasks, new(inherited));
            Tasks(scope.Always, new(inherited)); // Normal completion is not guaranteed in cleanup.
        }
        void Tasks(List<PlanTask> tasks, List<AssertionFact> facts)
        {
            foreach (var task in tasks)
            {
                if (task.Requires is { } assertion) Add(assertion, true, "/tasks/" + task.Id + "/requires", facts);
                if (task.Kind == "conditional" && task.Condition is { } condition)
                {
                    if (task.Body is { } yes && Branch(true) is { } yesFacts) Inspect(yes, yesFacts);
                    if (task.Otherwise is { } no && Branch(false) is { } noFacts) Inspect(no, noFacts);
                    List<AssertionFact>? Branch(bool selected)
                    {
                        if (condition.Kind == "boolean" && condition.Boolean != selected) return null;
                        var branch = new List<AssertionFact>(facts);
                        // An impossible alternative is allowed. Only its assertions
                        // are diagnosed; routing alone is never an assertion.
                        var conditions = Facts(condition, selected, "/tasks/" + task.Id + "/condition").ToArray();
                        foreach (var fact in conditions)
                        {
                            if (branch.Any(old => Conflicts(old, fact))) return null;
                            branch.Add(fact);
                        }
                        return branch;
                    }
                }
                else
                {
                    // An explicit empty selection cannot enter the iteration.
                    // Do not infer emptiness from producer contracts or other calls.
                    if (task.Body is { } body && !(task.Kind == "foreach" && task.Items is { Kind: "array", Items.Count: 0 }))
                        Inspect(body, new(facts));
                    if (task.Otherwise is { } otherwise) Inspect(otherwise, new(facts));
                }
                foreach (var branch in task.Branches) Inspect(branch, new(facts));
            }
        }
        void Add(TaskValue assertion, bool expected, string location, List<AssertionFact> facts)
        {
            foreach (var fact in Facts(assertion, expected, location))
            {
                var conflict = facts.FirstOrDefault(old => Conflicts(old, fact));
                if (conflict is not null)
                    findings.Add(new("TASK_GUARD_CONTRADICTION", location,
                        $"Assertion conflicts with {conflict.Location} on immutable value {fact.Contract}. Consumer assertions: {conflict.Location} and {location}. Clarify or explicitly revise the business guards; validation cannot weaken them."));
                facts.Add(fact);
            }
        }
        static bool Conflicts(AssertionFact old, AssertionFact fact)
        {
            if (old.Reference != fact.Reference) return false;
            var same = JsonNode.DeepEquals(JsonNode.Parse(old.Literal), JsonNode.Parse(fact.Literal));
            return old.Equal && fact.Equal && !same || old.Equal != fact.Equal && same;
        }
        IEnumerable<AssertionFact> Facts(TaskValue value, bool expected, string location)
        {
            if (value.Kind == "predicate")
            {
                if (value.Predicate == "not" && value.Items.Count == 1)
                    return Facts(value.Items[0], !expected, location);
                if ((value.Predicate == "and" && expected || value.Predicate == "or" && !expected) && value.Items.Count == 2)
                    return value.Items.SelectMany(v => Facts(v, expected, location));
                if (value.Predicate is "equal" or "not_equal" && value.Items.Count == 2)
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var literal = Literal(value.Items[1 - i]);
                        if (literal is not null && Reference(value.Items[i], location) is { } reference)
                            return [new(reference.Key, literal, expected == (value.Predicate == "equal"), location, reference.Contract)];
                    }
                }
                return [];
            }
            return Reference(value, location) is { } direct
                ? [new(direct.Key, expected ? "true" : "false", true, location, direct.Contract)] : [];
        }
        (string Key, string Contract)? Reference(TaskValue value, string location)
        {
            static string Part(string? text) => JsonValue.Create(text)?.ToJsonString() ?? "null";
            if (value.Kind == "field" && value.Items.Count == 1 && Reference(value.Items[0], location) is { } parent)
                return (parent.Key + "/field/" + Part(value.Port), parent.Contract + " property " + Part(value.Port));
            var scope = _symbols.Values[location].Scope;
            if (value.Kind == "input")
            {
                while (scope.Parent is not null) scope = scope.Parent;
                var group = _plan.Groups.SingleOrDefault(g => ReferenceEquals(g.Body, scope.Source));
                var input = (group?.Inputs ?? _plan.Inputs).Single(i => i.Name == value.Source);
                var path = (group is null ? "" : "/groups/" + group.Id) + "/inputs/" + value.Source;
                return (scope.Path + "/input/" + Part(value.Source), path + " " + TypeSchema(input.Type, _advisoryOptimization).ToJsonString());
            }
            if (value.Kind == "output" && value.Source is { } id)
            {
                var producer = _symbols.Tasks[id].Task;
                return ("output/" + Part(id) + "/" + Part(value.Port), "/tasks/" + id + "/outputs/" + value.Port +
                    (producer.Operation is null ? "" : " operation " + producer.Operation));
            }
            if (value.Kind == "item")
            {
                while (scope.Owner?.Kind != "foreach" && scope.Parent is not null) scope = scope.Parent;
                return (scope.Path + "/item", scope.Path + "/item");
            }
            return null;
        }
        static string? Literal(TaskValue value) => value.Kind switch
        {
            "null" => "null", "boolean" => value.Boolean == true ? "true" : "false",
            "string" => JsonSerializer.Serialize(value.Text, PlanningJsonContext.Default.String),
            // Use the source-generated wire token, preserving exact integers and
            // decimals. Numeric equality also treats equivalent spellings equally.
            "number" => JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)?["number"]?.ToJsonString(), _ => null
        };
    }
}
