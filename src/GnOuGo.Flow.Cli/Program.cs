
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

var runCommand = new Command("run", "Run a workflow YAML file");
runCommand.Add(runFileArg);
runCommand.Add(inputOption);
runCommand.Add(inputJsonOption);
runCommand.Add(mockOption);
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
                ? new ConfiguredMcpClientFactory(llmOptions.McpServers)
                : new InMemoryMcpClientFactory();
        }

        var otelEndpoint = appConfig?.GetValue<string>("OpenTelemetry:OtlpEndpoint");
        var otelEnabled = appConfig?.GetValue<bool>("OpenTelemetry:Enabled") ?? false;
        var otelServiceName = appConfig?.GetValue<string>("OpenTelemetry:ServiceName") ?? "GnOuGo.Flow.Cli";
        var otelProtocolStr = appConfig?.GetValue<string>("OpenTelemetry:Protocol") ?? "HttpProtobuf";
        var otelTenantId = appConfig?.GetValue<string>("OpenTelemetry:TenantId");

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

        var engine = new WorkflowEngine
        {
            LLMClient = llmClient,
            McpClientFactory = mcpFactory,
            McpCache = new MemoryCache(new MemoryCacheOptions()),
            HumanInputProvider = new ConsoleHumanInputProvider(),
            Telemetry = new OTelWorkflowTelemetry(),
            Logger = loggerFactory.CreateLogger("GnOuGo.Flow.WorkflowEngine"),
        };

        var workflow = compiled.Workflows[entrypoint];
        inputObj = WorkflowInputDefaults.Apply(workflow.Source, inputObj);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"▶ Running workflow '{entrypoint}'...");
        Console.ResetColor();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        var result = await engine.ExecuteAsync(workflow, inputObj, cts.Token);

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

