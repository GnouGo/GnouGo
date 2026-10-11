using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    // Existing typed bindings reconnect the chosen block. The extra projection
    // only removes the identity from the public record; it performs no inference.
    private static void UseVerbatimBlocks(TaskPlan plan)
    {
        static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
        static TaskValue Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
        var acquire = plan.Root.Tasks.Single(t => t.Id == "acquire");
        acquire.Inputs.Single(i => i.Name == "maxRecords").Value.Number = 2;
        var visit = plan.Root.Tasks.Single(t => t.Id == "visit");
        var extraction = visit.Body!.Tasks.Single(t => t.Id == "extract_facts");
        var decision = visit.Body.Tasks.Single(t => t.Id == "reconcile");
        var recordType = decision.ResultType!;
        recordType.Fields.Insert(0, new() { Name = "key", Type = new() { Kind = "string" } });
        extraction.Objective = "Copy complete observed blocks and their opaque identities; preserve missing facts as null.";
        extraction.ResultType = new() { Kind = "object", Fields = [new() { Name = "pageFacts", Type = new()
        {
            Kind = "array", MinItems = 0, MaxItems = 100, Items = new()
            { Kind = "array", MinItems = 0, MaxItems = 20, Items = recordType }
        } }] };
        decision.Objective = "Select one offered block identity. Never return rewritten text.";
        decision.Inputs = [new("offeredBlocks", Flatten(Output("extract_facts", "pageFacts")))];
        decision.ResultType = new() { Kind = "object", Fields = [new() { Name = "selectedIds", Type = new()
        { Kind = "array", MinItems = 1, MaxItems = 1, Items = new() { Kind = "string" } } }] };
        visit.Body.Tasks.Add(new()
        {
            Id = "selected_block", Kind = "foreach", MaxItems = 1,
            Objective = "Recover the exact offered record; exclude its technical identity from public output.",
            Items = new() { Kind = "lookup", Port = "key", Items =
                [Flatten(Output("extract_facts", "pageFacts")), Output("reconcile", "selectedIds")] },
            Body = new() { Outputs = [new("record", new() { Kind = "object", Members =
                new[] { "name", "description", "price", "error" }.Select(name => new TaskOutput(name,
                    new() { Kind = "field", Port = name, Items = [new() { Kind = "item" }] })).ToList() })] }
        });
        visit.Body.Outputs = [new("rows", Output("selected_block", "record"))];
        var rowsProjection = plan.Root.Tasks.Single(t => t.Id == "format");
        rowsProjection.Items = Flatten(Output("visit", "rows"));
        // Reuse the existing bounded row projection for the public ten-record
        // contract; flatten alone does not assert concatenated cardinality.
        rowsProjection.Body!.Outputs.Add(new("record", new() { Kind = "item" }));
        var rows = plan.Root.Outputs.FindIndex(o => o.Name == "rows");
        plan.Root.Outputs[rows] = new("rows", Output("format", "record"));
    }
}
