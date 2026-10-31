using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LocalFilePlanningTests
{
    [Theory]
    [InlineData("files_read")]
    [InlineData("files_copy")]
    [InlineData("files_loop_cleanup")]
    public async Task StaticTaskPlanExecutesEveryExistingLocalFileVariantWithoutPlanningInference(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-file-plan-").FullName;
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "GnOuGo.Cmd.Mcp", "GnOuGo.Cmd.Mcp");
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await using var files = new LocalExecutionCases(root, executable);
        var actual = new WorkflowPlanningRuntime(new() { McpClientFactory = files.Factory }, (_, _) => Task.CompletedTask);
        var catalog = await actual.DiscoverAsync(new(), ct);
        foreach (var source in await actual.Capabilities.ListSourcesAsync(ct))
        {
            string? cursor = null;
            do
            {
                var page = await actual.Capabilities.ListAsync(source.Id, cursor, ct);
                foreach (var entry in page.Capabilities) catalog.Capabilities.Add(await actual.Capabilities.ResolveAsync(entry, ct));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "cmd_run")).Id;
        TaskValue Text(string value) => new() { Kind = "string", Text = value };
        TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
        PlanTask Command(string id, string command, params TaskOutput[] parameters) => new() { Id = id, Kind = "operation", Operation = operation,
            Objective = command, Inputs = [new("commandName", Text(command)), new("parameters", new() { Kind = "object", Members = [.. parameters] })] };
        TaskScope Read(TaskValue path) => new() { Tasks = [Command("read", "cat_file", new TaskOutput("path", path)),
            new() { Id = "contents", Kind = "value", Objective = "Return observed file text", Requires = new() { Kind = "predicate", Predicate = "not_equal", Items = [Output("read", "stdout"), new() { Kind = "null" }] },
                Outputs = [new("text", Output("read", "stdout"))] }], Outputs = [new("result", Output("contents", "text"))] };
        var plan = new TaskPlan { Root = Read(Text(name == "files_copy" ? "workflows/copy/result.txt" : "seed.txt")) };
        if (name == "files_copy")
        {
            plan.Root.Tasks.InsertRange(0, [Command("create", "create_directory", new TaskOutput("path", Text("workflows/copy"))),
                Command("write", "write_file", new TaskOutput("path", Text("workflows/copy/source.txt")), new TaskOutput("contentBase64", Text("aGVsbG8="))),
                Command("copy", "copy_file", new TaskOutput("source", Text("workflows/copy/source.txt")), new TaskOutput("destination", Text("workflows/copy/result.txt")))]);
        }
        if (name == "files_loop_cleanup")
        {
            plan.Inputs = [new() { Name = "enabled", Type = new() { Kind = "boolean" } }, new() { Name = "files", Type = new() { Kind = "array", MaxItems = 3,
                Items = new() { Kind = "string", Enum = ["alpha.txt", "beta.txt"] } } }];
            plan.Root = new() { Tasks = [Command("create", "create_directory", new TaskOutput("path", Text("workflows/batch"))),
                new() { Id = "route", Kind = "conditional", Objective = "Read the supplied ordered selection when enabled", Condition = new() { Kind = "input", Source = "enabled" },
                    Body = new() { Tasks = [new() { Id = "each", Kind = "foreach", Objective = "Read each occurrence", Items = new() { Kind = "input", Source = "files" }, Body = Read(new() { Kind = "item" }) }],
                        Outputs = [new("result", Output("each", "result"))] },
                    Otherwise = new() { Outputs = [new("result", new() { Kind = "array", Items = [] })] } }],
                Outputs = [new("result", Output("route", "result"))], Always = [Command("cleanup", "delete_directory", new TaskOutput("path", Text("workflows/batch")))] };
        }
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Graph);
        PlanningConfirmationGuards.Apply(compilation.Graph, catalog);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog);
        Assert.Empty(files.Calls);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var variant in LocalExecutionCases.Variants(name))
        {
            var inputs = files.Prepare(name, variant);
            var engine = new WorkflowEngine { McpClientFactory = files.Factory,
                HumanInputProvider = variant == "permission_unavailable" ? null : new PlanningCorpus.Human(variant != "denied") };
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], inputs, ct);
            var evidence = files.Observe(name, variant, result, null);
            Assert.True(evidence["correct"]!.GetValue<bool>(), name + "/" + variant + ": " + evidence);
        }
    }
}
