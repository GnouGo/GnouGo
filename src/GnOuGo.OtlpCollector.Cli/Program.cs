using System.CommandLine;
using OtlpTestCli;

var rootCommand = new RootCommand("CLI de test pour envoyer des traces RAG/LLM au collecteur OTLP");

var urlOption = new Option<string>(
    name: "--url",
    description: "URL du collecteur OTLP",
    getDefaultValue: () => "http://localhost:4318"
);

var tenantOption = new Option<string>(
    name: "--tenant",
    description: "Tenant ID",
    getDefaultValue: () => ""
);

var protocolOption = new Option<string>(
    name: "--protocol",
    description: "Protocole à utiliser (http ou grpc)",
    getDefaultValue: () => "http"
);

// Commande: send-rag-workflow
var sendRagCmd = new Command("send-rag-workflow", "Envoie un workflow RAG complet avec embedding, search et generation");
sendRagCmd.AddOption(urlOption);
sendRagCmd.AddOption(tenantOption);
sendRagCmd.AddOption(protocolOption);

sendRagCmd.SetHandler(async (string url, string tenant, string protocol) =>
{
    Console.WriteLine($"📤 Envoi d'un workflow RAG via OpenTelemetry .NET SDK...");
    Console.WriteLine($"   URL: {url}");
    Console.WriteLine($"   Tenant: {tenant}");
    Console.WriteLine($"   Protocol: {protocol}");
    Console.WriteLine();

    await OtelRagWorkflowSender.SendAsync(url, tenant, protocol);

    Console.WriteLine();
    Console.WriteLine("✅ Workflow RAG envoyé avec succès!");
    Console.WriteLine($"   Ouvrez {url} dans votre navigateur pour visualiser les traces.");
}, urlOption, tenantOption, protocolOption);

// Commande: send-llm-completion
var sendLlmCmd = new Command("send-llm-completion", "Envoie une trace simple de completion LLM");
sendLlmCmd.AddOption(urlOption);
sendLlmCmd.AddOption(tenantOption);
sendLlmCmd.AddOption(protocolOption);

sendLlmCmd.SetHandler(async (string url, string tenant, string protocol) =>
{
    Console.WriteLine($"📤 Envoi d'une completion LLM via OpenTelemetry .NET SDK...");
    Console.WriteLine($"   URL: {url}");
    Console.WriteLine($"   Tenant: {tenant}");
    Console.WriteLine($"   Protocol: {protocol}");
    Console.WriteLine();

    await OtelLlmCompletionSender.SendAsync(url, tenant, protocol);

    Console.WriteLine();
    Console.WriteLine("✅ Completion LLM envoyée avec succès!");
}, urlOption, tenantOption, protocolOption);

// Commande: send-logs
var sendLogsCmd = new Command("send-logs", "Envoie des logs de test (TRACE, DEBUG, INFO, WARN, ERROR, FATAL)");
sendLogsCmd.AddOption(urlOption);
sendLogsCmd.AddOption(tenantOption);
sendLogsCmd.AddOption(protocolOption);

sendLogsCmd.SetHandler(async (string url, string tenant, string protocol) =>
{
    Console.WriteLine($"📋 Envoi de logs de test via OpenTelemetry .NET SDK...");
    Console.WriteLine($"   URL: {url}");
    Console.WriteLine($"   Tenant: {tenant}");
    Console.WriteLine($"   Protocol: {protocol}");
    Console.WriteLine();

    await OtelLogSender.SendAsync(url, tenant, protocol);

    Console.WriteLine();
    Console.WriteLine("✅ Logs envoyés avec succès!");
    Console.WriteLine("   📊 11 logs envoyés:");
    Console.WriteLine("      - 1 TRACE");
    Console.WriteLine("      - 1 DEBUG");
    Console.WriteLine("      - 3 INFO");
    Console.WriteLine("      - 2 WARN");
    Console.WriteLine("      - 2 ERROR");
    Console.WriteLine("      - 1 CRITICAL (FATAL)");
    Console.WriteLine("      - 1 INFO (success)");
}, urlOption, tenantOption, protocolOption);

rootCommand.AddCommand(sendRagCmd);
rootCommand.AddCommand(sendLlmCmd);
rootCommand.AddCommand(sendLogsCmd);

return await rootCommand.InvokeAsync(args);

