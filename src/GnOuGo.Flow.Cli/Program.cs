using GnOuGo.Flow.Copilot;

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Cli;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Persistence;

// Root command
var rootCommand = new RootCommand("GnOuGo.Flow — YAML Workflow DSL Engine");

// === validate ===
var validateFileArg = new Argument<FileInfo>("file")
{
    Description = "YAML workflow file to validate"
};
var validateCommand = new Command("validate", "Validate a workflow YAML file");
validateCommand.Add(validateFileArg);
validateCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var file = parseResult.GetValue(validateFileArg);
    if (file is null || !file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file?.FullName ?? "(null)"}");
        Environment.ExitCode = 1;
        return;
    }

    var yaml = await File.ReadAllTextAsync(file.FullName, cancellationToken);

    try
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var errors = compiler.Validate(doc);

        if (errors.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Validation passed — no errors.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {errors.Count} validation error(s):");
            Console.ResetColor();
            foreach (var err in errors)
            {
                Console.WriteLine($"  {err}");
            }

            Environment.ExitCode = 1;
        }
    }
    catch (WorkflowParseException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"Parse error: {ex.Message}");
        Console.ResetColor();
        Environment.ExitCode = 1;
    }
});

// === run ===
var runFileArg = new Argument<FileInfo>("file")
{
    Description = "YAML workflow file to run"
};
var inputOption = new Option<string[]>("--input")
{
    Description = "Input values as key=value pairs",
    AllowMultipleArgumentsPerToken = true
};
inputOption.Aliases.Add("-i");

var inputJsonOption = new Option<string?>("--input-json")
{
    Description = "Full JSON object as input"
};
inputJsonOption.Aliases.Add("-j");

var mockOption = new Option<bool>("--mock")
{
    Description = "Use mock LLM and MCP clients (no real API calls)"
};
mockOption.Aliases.Add("-m");

var runIdOption = new Option<string?>("--run-id")
{
    Description = "Tenant-owned durable execution identity; inspect it before resuming"
};

var resumeRevisionOption = new Option<long?>("--resume-revision") { Description = "Resume the existing --run-id only if its inspected revision still matches" };
var runCommand = new Command("run", "Run a workflow YAML file");
runCommand.Add(runFileArg);
runCommand.Add(inputOption);
runCommand.Add(inputJsonOption);
runCommand.Add(mockOption);
runCommand.Add(runIdOption);
runCommand.Add(resumeRevisionOption);
runCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var file = parseResult.GetValue(runFileArg);
    var inputs = parseResult.GetValue(inputOption) ?? Array.Empty<string>();
    var inputJson = parseResult.GetValue(inputJsonOption);
    var useMock = parseResult.GetValue(mockOption);

    if (file is null || !file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file?.FullName ?? "(null)"}");
        Environment.ExitCode = 1;
        return;
    }

    var yaml = await File.ReadAllTextAsync(file.FullName, cancellationToken);

    try
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        var entrypoint = compiled.Entrypoint;
        if (entrypoint == null || !compiled.Workflows.ContainsKey(entrypoint))
        {
            Console.Error.WriteLine("No entrypoint workflow found. Define 'main' or set 'entrypoint'.");
            Environment.ExitCode = 1;
            return;
        }

        var inputObj = new JsonObject();

        if (!string.IsNullOrWhiteSpace(inputJson) && JsonNode.Parse(inputJson) is JsonObject parsed)
        {
            foreach (var kv in parsed)
            {
                inputObj[kv.Key] = kv.Value?.DeepClone();
            }
        }

        foreach (var inp in inputs)
        {
            var eqIndex = inp.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var key = inp[..eqIndex];
            var val = inp[(eqIndex + 1)..];
            try
            {
                inputObj[key] = JsonNode.Parse(val);
            }
            catch
            {
                inputObj[key] = val;
            }
        }

        ILLMClient llmClient;
        IMcpClientFactory mcpFactory;
        IConfiguration? appConfig = null;
        var humanInput = new ConsoleHumanInputProvider();

        if (useMock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠ Running in MOCK mode — no real API calls will be made.");
            Console.ResetColor();
            llmClient = new MockLLMClient();
            mcpFactory = new MockMcpClientFactory();
        }
        else
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            var appSettingsBasePath = File.Exists(Path.Combine(currentDirectory, "appsettings.json"))
                ? currentDirectory
                : AppContext.BaseDirectory;

            appConfig = new ConfigurationBuilder()
                .SetBasePath(appSettingsBasePath)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var llmOptions = appConfig.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();
            var http = new HttpClient { Timeout = LLMHttpClientDefaults.MinimumTimeout };
            var llmLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
            var routingClient = new RoutingLLMClient(http, llmOptions, llmLoggerFactory);
            llmClient = new RoutingLLMClientAdapter(routingClient);
            mcpFactory = llmOptions.McpServers.Count > 0
                ? new ConfiguredMcpClientFactory(
                    llmOptions.McpServers,
                    humanInputProvider: humanInput,
                    defaultLlmProvider: llmOptions.DefaultProvider,
                    defaultLlmModel: llmOptions.DefaultModel)
                : new InMemoryMcpClientFactory();
        }

        var otelEndpoint = appConfig?["OpenTelemetry:OtlpEndpoint"];
        var otelEnabled = bool.TryParse(appConfig?["OpenTelemetry:Enabled"], out var configuredOtelEnabled) && configuredOtelEnabled;
        var otelServiceName = appConfig?["OpenTelemetry:ServiceName"] ?? "GnOuGo.Flow.Cli";
        var otelProtocolStr = appConfig?["OpenTelemetry:Protocol"] ?? "HttpProtobuf";
        var otelTenantId = appConfig?["OpenTelemetry:TenantId"];

        var otelProtocol = otelProtocolStr.Equals("Grpc", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.Grpc
            : OtlpExportProtocol.HttpProtobuf;

        TracerProvider? tracerProvider = null;
        if (otelEnabled && !string.IsNullOrWhiteSpace(otelEndpoint))
        {
            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(otelServiceName, serviceVersion: "1.0.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["host.name"] = Environment.MachineName
                });

            tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(OTelWorkflowTelemetry.ActivitySourceName)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otelEndpoint);
                    o.Protocol = otelProtocol;
                    if (!string.IsNullOrWhiteSpace(otelTenantId))
                    {
                        o.Headers = $"X-Tenant-Id={otelTenantId}";
                    }
                })
                .Build();
        }

        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            if (otelEnabled && !string.IsNullOrWhiteSpace(otelEndpoint))
            {
                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(otelServiceName, serviceVersion: "1.0.0")
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["host.name"] = Environment.MachineName
                    });

                b.AddOpenTelemetry(logging =>
                {
                    logging.SetResourceBuilder(resourceBuilder);
                    logging.IncludeFormattedMessage = true;
                    logging.IncludeScopes = true;
                    logging.ParseStateValues = true;
                    logging.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otelEndpoint);
                        o.Protocol = otelProtocol;
                        if (!string.IsNullOrWhiteSpace(otelTenantId))
                        {
                            o.Headers = $"X-Tenant-Id={otelTenantId}";
                        }
                    });
                });
            }
        });

        var runId = parseResult.GetValue(runIdOption) ?? Guid.NewGuid().ToString("N");
        Console.WriteLine($"Run ID: {runId}");
        var engine = new WorkflowEngine
        {
            RunStore = EncryptedWorkflowRunStore.CreateWorkspace(),
            Limits = new ExecutionLimits { RunId = runId, TenantId = string.IsNullOrWhiteSpace(otelTenantId) ? "default" : otelTenantId.Trim() },
            WorkflowPlanner = new GnOuGo.Flow.Planning.HybridWorkflowPlanner(),
            PlanningRuntimeFactory = GnOuGo.Flow.Integrations.Planning.WorkflowPlanningRuntimeFactory.CreateWorkspace(),
            LLMClient = llmClient,
            ModelUsageCostEstimator = new ModelMetadataUsageCostEstimator(),
            McpClientFactory = mcpFactory,
            McpCache = new MemoryCache(new MemoryCacheOptions()),
            HumanInputProvider = humanInput,
            Telemetry = new OTelWorkflowTelemetry(),
            Logger = loggerFactory.CreateLogger("GnOuGo.Flow.WorkflowEngine"),
        };

        engine.WithCopilotRunners(appConfig?.GetSection("Flow:CopilotRunners").GetChildren()
            .Select(c => new KeyValuePair<string, string>(c.Key, c.Value ?? throw new InvalidOperationException("A Copilot runner requires an MCP server name."))) ?? []);

        var workflow = compiled.Workflows[entrypoint];
        inputObj = WorkflowInputDefaults.Apply(workflow.Source, inputObj);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"▶ Running workflow '{entrypoint}'...");
        Console.ResetColor();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        var result = parseResult.GetValue(resumeRevisionOption) is { } revision
            ? await engine.ResumeAsync(engine.Limits.TenantId!, runId, revision, workflow, cts.Token)
            : await engine.ExecuteAsync(workflow, inputObj, cts.Token);
        var journal = await engine.RunStore!.ReadAsync(engine.Limits.TenantId!, runId, cancellationToken);
        Console.WriteLine($"Execution: {journal!.Status}; revision: {journal.Revision}; steps: {journal.StepsStarted}/{journal.Limits.MaxTotalStepsExecuted}");
        foreach (var invocation in journal.Invocations.Values.Where(i => i.Status == "needs_reconciliation"))
            Console.WriteLine($"Reconciliation required: {invocation.Id}");

        Console.WriteLine();
        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Workflow completed successfully.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Workflow failed: {result.Error?.Message}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("Steps executed:");
        foreach (var step in result.StepResults)
        {
            var icon = step.Status switch
            {
                StepStatus.Succeeded => "✓",
                StepStatus.Skipped => "○",
                StepStatus.Failed => "✗",
                _ => "?"
            };
            Console.WriteLine($"  {icon} {step.StepId} ({step.StepType}) — {step.Status} [{step.Duration.TotalMilliseconds:F1}ms]");
        }

        if (result.Outputs != null)
        {
            Console.WriteLine();
            Console.WriteLine("Outputs:");
            Console.WriteLine(result.Outputs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        loggerFactory.Dispose();
        tracerProvider?.Dispose();
    }
    catch (WorkflowParseException ex)
    {
        Console.Error.WriteLine($"Parse error: {ex.Message}");
        Environment.ExitCode = 1;
    }
    catch (WorkflowCompilationException ex)
    {
        Console.Error.WriteLine($"Compilation error: {ex.Message}");
        Environment.ExitCode = 1;
    }
});

// === inspect ===
var inspectFileArg = new Argument<FileInfo>("file")
{
    Description = "YAML workflow file to inspect"
};
var inspectCommand = new Command("inspect", "Inspect a workflow YAML file structure");
inspectCommand.Add(inspectFileArg);
inspectCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var file = parseResult.GetValue(inspectFileArg);
    if (file is null || !file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file?.FullName ?? "(null)"}");
        Environment.ExitCode = 1;
        return;
    }

    var yaml = await File.ReadAllTextAsync(file.FullName, cancellationToken);

    try
    {
        var doc = WorkflowParser.Parse(yaml);

        Console.WriteLine($"Version: {doc.Version}");
        Console.WriteLine($"Name: {doc.Name ?? "(none)"}");
        Console.WriteLine($"Entrypoint: {doc.Entrypoint ?? "(auto)"}");
        Console.WriteLine($"Workflows: {doc.Workflows.Count}");
        Console.WriteLine($"Exports: {(doc.Exports != null ? string.Join(", ", doc.Exports) : "(none)")}");

        foreach (var (name, wf) in doc.Workflows)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Workflow: {name}");
            Console.ResetColor();
            if (wf.Inputs != null)
            {
                Console.WriteLine("    Inputs:");
                foreach (var (inputName, inputDef) in wf.Inputs)
                {
                    PrintInputDef(inputName, inputDef, "      ");
                }
            }

            Console.WriteLine($"    Steps: {wf.Steps.Count}");
            PrintSteps(wf.Steps, "    ");

            if (wf.Outputs != null)
            {
                Console.WriteLine("    Outputs:");
                foreach (var (outputName, outputDef) in wf.Outputs)
                {
                    PrintOutputDef(outputName, outputDef, "      ");
                }
            }
        }
    }
    catch (WorkflowParseException ex)
    {
        Console.Error.WriteLine($"Parse error: {ex.Message}");
        Environment.ExitCode = 1;
    }
});

rootCommand.Add(validateCommand);
var runsCommand = new Command("runs", "Inspect durable executions and issue revision-checked commands");
var tenantOption = new Option<string>("--tenant") { DefaultValueFactory = _ => "default", Description = "Execution tenant" };
var inspectIdOption = new Option<string?>("--id") { Description = "Execution identity; omit to list tenant runs" };
var commandOption = new Option<string>("--command") { DefaultValueFactory = _ => "inspect", Description = "inspect, cancel or reconcile (resume uses run --resume-revision)" };
var revisionOption = new Option<long?>("--revision") { Description = "Required inspected revision for commands" };
var invocationOption = new Option<string?>("--invocation") { Description = "Exact invocation identity from the inspected journal" };
var stoppedOption = new Option<string?>("--confirmed-stopped-reason") { Description = "Explicit confirmation of stopped external work; reconciliation records failure" };
runsCommand.Add(invocationOption); runsCommand.Add(stoppedOption);
runsCommand.Add(tenantOption); runsCommand.Add(inspectIdOption); runsCommand.Add(commandOption); runsCommand.Add(revisionOption);
runsCommand.SetAction(async (ParseResult parsed, CancellationToken ct) =>
{
    try
    {
        var store = EncryptedWorkflowRunStore.CreateWorkspace();
        var tenant = parsed.GetValue(tenantOption)!;
        var id = parsed.GetValue(inspectIdOption);
        var command = parsed.GetValue(commandOption);
        if (command == "cancel")
        {
            if (id is null || parsed.GetValue(revisionOption) is not { } revision) throw new ArgumentException("Cancellation requires --id and --revision.");
            Console.WriteLine(JsonSerializer.Serialize(await store.CancelAsync(tenant, id, revision, ct), WorkflowRunJsonContext.Default.WorkflowRun));
        }
        else if (command == "reconcile")
        {
            if (id is null || parsed.GetValue(revisionOption) is not { } revision || parsed.GetValue(invocationOption) is not { } invocation)
                throw new ArgumentException("Reconciliation requires --id, --revision and --invocation.");
            var directory = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")) ? Directory.GetCurrentDirectory() : AppContext.BaseDirectory;
            var config = new ConfigurationBuilder().SetBasePath(directory).AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
            var options = config.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();
            await using var transport = new ConfiguredMcpClientFactory(options.McpServers, new ConsoleHumanInputProvider(), options.DefaultProvider, options.DefaultModel);
            var engine = new WorkflowEngine { RunStore = store, McpClientFactory = transport }.WithCopilotRunners(
                config.GetSection("Flow:CopilotRunners").GetChildren().Select(c => new KeyValuePair<string, string>(c.Key, c.Value ?? throw new ArgumentException("A Copilot runner requires a server name."))));
            var reconciled = await engine.ReconcileAsync(tenant, id, revision, invocation, parsed.GetValue(stoppedOption), ct);
            Console.WriteLine(JsonSerializer.Serialize(reconciled, WorkflowRunJsonContext.Default.WorkflowRun));
        }
        else if (command != "inspect") throw new ArgumentException("Unknown execution command.");
        else if (id is null) Console.WriteLine(JsonSerializer.Serialize((await store.ListAsync(tenant, ct)).ToList(), WorkflowRunJsonContext.Default.ListWorkflowRun));
        else Console.WriteLine(JsonSerializer.Serialize(await store.ReadAsync(tenant, id, ct), WorkflowRunJsonContext.Default.WorkflowRun));
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
});
rootCommand.Add(runsCommand);
rootCommand.Add(runCommand);
rootCommand.Add(inspectCommand);

return await rootCommand.Parse(args).InvokeAsync();

// === Helpers ===

static void PrintSteps(List<GnOuGo.Flow.Core.Models.StepDef> steps, string indent)
{
    foreach (var step in steps)
    {
        Console.WriteLine($"{indent}  [{step.Type}] {step.Id}{(step.If != null ? $"  if: {step.If}" : "")}");
        if (step.Steps != null) PrintSteps(step.Steps, indent + "  ");
        if (step.Branches != null)
        {
            for (int i = 0; i < step.Branches.Count; i++)
            {
                Console.WriteLine($"{indent}    Branch {i}:");
                PrintSteps(step.Branches[i].Steps, indent + "    ");
            }
        }
        if (step.Cases != null)
        {
            foreach (var c in step.Cases)
            {
                Console.WriteLine($"{indent}    Case: {c.Value ?? c.When ?? "default"}");
                PrintSteps(c.Steps, indent + "    ");
            }
        }
    }
}

static void PrintInputDef(string name, GnOuGo.Flow.Core.Models.InputDef def, string indent)
{
    var req = def.Required ? "required" : "optional";
    var desc = def.Description != null ? $" — {def.Description}" : "";
    var defaultStr = def.Default != null ? $", default: {def.Default}" : "";
    Console.WriteLine($"{indent}{name}: {FormatInputType(def)} ({req}{defaultStr}){desc}");
}

static string FormatInputType(GnOuGo.Flow.Core.Models.InputDef def)
{
    return def.Type.ToLowerInvariant() switch
    {
        "array" when def.Items != null => $"array<{FormatInputType(def.Items)}>",
        "dictionary" when def.AdditionalProperties != null => $"dictionary<string, {FormatInputType(def.AdditionalProperties)}>",
        "object" when def.Properties != null =>
            $"object{{{string.Join(", ", def.Properties.Select(p => $"{p.Key}: {FormatInputType(p.Value)}"))}}}",
        _ => def.Type
    };
}

static void PrintOutputDef(string name, GnOuGo.Flow.Core.Models.OutputDef def, string indent)
{
    var desc = def.Description != null ? $" — {def.Description}" : "";
    var expr = !string.IsNullOrEmpty(def.Expr) ? $" = {def.Expr}" : "";
    Console.WriteLine($"{indent}{name}: {FormatOutputType(def)}{expr}{desc}");
}

static string FormatOutputType(GnOuGo.Flow.Core.Models.OutputDef def)
{
    return def.Type.ToLowerInvariant() switch
    {
        "array" when def.Items != null => $"array<{FormatOutputType(def.Items)}>",
        "dictionary" when def.AdditionalProperties != null => $"dictionary<string, {FormatOutputType(def.AdditionalProperties)}>",
        "object" when def.Properties != null =>
            $"object{{{string.Join(", ", def.Properties.Select(p => $"{p.Key}: {FormatOutputType(p.Value)}"))}}}",
        _ => def.Type
    };
}
