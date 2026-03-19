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
var validateFileArg = new Argument<FileInfo>("file", "YAML workflow file to validate");
var validateCommand = new Command("validate", "Validate a workflow YAML file") { validateFileArg };
validateCommand.SetHandler(async (FileInfo file) =>
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        Environment.ExitCode = 1;
        return;
    }

    var yaml = await File.ReadAllTextAsync(file.FullName);

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
}, validateFileArg);

// === run ===
var runFileArg = new Argument<FileInfo>("file", "YAML workflow file to run");
var inputOption = new Option<string[]>("--input", "Input values as key=value pairs") { AllowMultipleArgumentsPerToken = true };
inputOption.AddAlias("-i");
var inputJsonOption = new Option<string?>("--input-json", "Full JSON object as input");
inputJsonOption.AddAlias("-j");
var mockOption = new Option<bool>("--mock", "Use mock LLM and MCP clients (no real API calls)");
mockOption.AddAlias("-m");
var runCommand = new Command("run", "Run a workflow YAML file") { runFileArg, inputOption, inputJsonOption, mockOption };
runCommand.SetHandler(async (FileInfo file, string[] inputs, string? inputJson, bool useMock) =>
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        Environment.ExitCode = 1;
        return;
    }

    var yaml = await File.ReadAllTextAsync(file.FullName);

    try
    {
        // Parse
        var doc = WorkflowParser.Parse(yaml);

        // Compile
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);

        // Find entrypoint workflow
        var entrypoint = compiled.Entrypoint;
        if (entrypoint == null || !compiled.Workflows.ContainsKey(entrypoint))
        {
            Console.Error.WriteLine("No entrypoint workflow found. Define 'main' or set 'entrypoint'.");
            Environment.ExitCode = 1;
            return;
        }

        // Build inputs
        var inputObj = new JsonObject();

        // Parse --input-json first (base)
        if (!string.IsNullOrWhiteSpace(inputJson))
        {
            var parsed = JsonNode.Parse(inputJson) as JsonObject;
            if (parsed != null)
            {
                foreach (var kv in parsed)
                    inputObj[kv.Key] = kv.Value?.DeepClone();
            }
        }

        // Then overlay --input key=value pairs
        if (inputs != null)
        {
            foreach (var inp in inputs)
            {
                var eqIndex = inp.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = inp[..eqIndex];
                    var val = inp[(eqIndex + 1)..];
                    // Try to parse as JSON, otherwise treat as string
                    try
                    {
                        inputObj[key] = JsonNode.Parse(val);
                    }
                    catch
                    {
                        inputObj[key] = val;
                    }
                }
            }
        }

        // Create LLM client and MCP factory
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
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var routingClient = new RoutingLLMClient(http, llmOptions);
            llmClient = new RoutingLLMClientAdapter(routingClient);

            if (llmOptions.McpServers.Count > 0)
            {
                mcpFactory = new ConfiguredMcpClientFactory(llmOptions.McpServers);
            }
            else
            {
                mcpFactory = new InMemoryMcpClientFactory();
            }
        }

        // ── OpenTelemetry: read config and build tracing + logging export ──
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
                        o.Headers = $"X-Tenant-Id={otelTenantId}";
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
                        o.Endpoint = new Uri(otelEndpoint!);
                        o.Protocol = otelProtocol;
                        if (!string.IsNullOrWhiteSpace(otelTenantId))
                            o.Headers = $"X-Tenant-Id={otelTenantId}";
                    });
                });
            }
        });

        var engine = new WorkflowEngine
        {
            LLMClient = llmClient,
            McpClientFactory = mcpFactory,
            McpCache = new MemoryCache(new MemoryCacheOptions()),
            Telemetry = new OTelWorkflowTelemetry(),
            Logger = loggerFactory.CreateLogger("GnOuGo.Flow.WorkflowEngine"),
        };

        var workflow = compiled.Workflows[entrypoint];
        inputObj = WorkflowInputDefaults.Apply(workflow.Source, inputObj);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"▶ Running workflow '{entrypoint}'...");
        Console.ResetColor();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await engine.ExecuteAsync(workflow, inputObj, cts.Token);

        // Output result
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

        // Dispose OTel providers to flush pending telemetry before exit
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
}, runFileArg, inputOption, inputJsonOption, mockOption);

// === inspect ===
var inspectFileArg = new Argument<FileInfo>("file", "YAML workflow file to inspect");
var inspectCommand = new Command("inspect", "Inspect a workflow YAML file structure") { inspectFileArg };
inspectCommand.SetHandler(async (FileInfo file) =>
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        Environment.ExitCode = 1;
        return;
    }

    var yaml = await File.ReadAllTextAsync(file.FullName);

    try
    {
        var doc = WorkflowParser.Parse(yaml);

        Console.WriteLine($"DSL: {doc.Dsl}");
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
                Console.WriteLine($"    Inputs:");
                foreach (var (inputName, inputDef) in wf.Inputs)
                    PrintInputDef(inputName, inputDef, "      ");
            }            Console.WriteLine($"    Steps: {wf.Steps.Count}");
            PrintSteps(wf.Steps, "    ");
            if (wf.Outputs != null)
                Console.WriteLine($"    Outputs: {string.Join(", ", wf.Outputs.Keys)}");
        }
    }
    catch (WorkflowParseException ex)
    {
        Console.Error.WriteLine($"Parse error: {ex.Message}");
        Environment.ExitCode = 1;
    }
}, inspectFileArg);

rootCommand.AddCommand(validateCommand);
rootCommand.AddCommand(runCommand);
rootCommand.AddCommand(inspectCommand);

return await rootCommand.InvokeAsync(args);

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


