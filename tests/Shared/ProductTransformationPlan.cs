using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Planning.Examples;

public static class ProductTransformationPlan
{
    public static TaskPlan Create(PlanningCatalog catalog, ProductTransformationFixture fixture)
    {
        string Operation(string source, string method) => catalog.Capabilities.Single(c => c.Server == source && c.Method == method).Operation?.Id
            ?? catalog.Capabilities.Single(c => c.Server == source && c.Method == method).Id;
        var read = Operation(fixture.BrowserSource, fixture.ReadMethod); var close = Operation(fixture.BrowserSource, fixture.CloseMethod); var write = Operation(fixture.DocumentSource, fixture.WriteMethod);
        TaskType record = Obj(("name", new()), ("description", new() { Nullable = true }), ("price", new() { Nullable = true }));
        return new()
        {
            Inputs = [new() { Name = "search", Type = new() }],
            Root = new()
            {
                Tasks = [
                    new() { Id = "search", Kind = "operation", Objective = "Read search results", Operation = read, Inputs = [new("url", new() { Kind = "input", Source = "search" })] },
                    new() { Id = "urls", Kind = "transform", Objective = "Extract product URLs from the supplied search HTML in page order, up to 20. Return an empty list when there are no products.", Inputs = [new("html", Ref("search", "content"))], ResultType = Obj(("urls", new() { Kind = "array", Items = new() })) },
                    new() { Id = "products", Kind = "foreach", Objective = "Visit each product sequentially", Items = Ref("urls", "urls"), MaxItems = 20, MaxConcurrency = 1, Body = new()
                    {
                        Tasks = [new() { Id = "page", Kind = "operation", Objective = "Read product HTML", Operation = read, Inputs = [new("url", new() { Kind = "item" })] },
                            new() { Id = "extract", Kind = "transform", Objective = "Extract the name, description and price from the supplied HTML. Use null for absent descriptions or prices. Never invent observations.", Inputs = [new("html", Ref("page", "content"))], ResultType = record }],
                        Outputs = [new("records", Ref("extract"))]
                    } },
                    new() { Id = "table", Kind = "transform", Objective = "Format records as TSV with the header Name, Description, Price. Preserve row order, Unicode, commas and quotes. Normalize embedded whitespace to single spaces and replace null fields with empty cells. An empty collection produces only the header.", Inputs = [new("records", Ref("products", "records"))], ResultType = Obj(("content", new())) },
                    new() { Id = "write", Kind = "operation", Objective = "Save the XLSX document within the allowed workspace", Operation = write, Inputs = [new("path", Text(ProductTransformationFixture.OutputPath)), new("content", Ref("table", "content"))] }
                ],
                Always = [new() { Id = "cleanup", Kind = "operation", Objective = "Close the browser and preserve the workbook", Operation = close }],
                Outputs = [new("file", Ref("write", "path"))]
            }
        };
    }
    public static TaskValue Ref(string task, string? port = null) => new() { Kind = "output", Source = task, Port = port };
    public static TaskValue Text(string value) => new() { Kind = "string", Text = value };
    public static TaskType Obj(params (string Name, TaskType Type)[] fields) => new() { Kind = "object", Fields = fields.Select(f => new TaskInput { Name = f.Name, Type = f.Type }).ToList() };
}
