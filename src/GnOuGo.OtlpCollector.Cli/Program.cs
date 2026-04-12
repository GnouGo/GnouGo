using System.CommandLine;
using OtlpTestCli;

var rootCommand = new RootCommand("CLI de test pour envoyer des traces RAG/LLM au collecteur OTLP");

var urlOption = new Option<string>("--url")
{
    Description = "URL du collecteur OTLP",
    DefaultValueFactory = _ => "http://localhost:4318"
};

var tenantOption = new Option<string>("--tenant")
{
    Description = "Tenant ID",
    DefaultValueFactory = _ => string.Empty
};

var protocolOption = new Option<string>("--protocol")
{
    Description = "Protocole à utiliser (http ou grpc)",
    DefaultValueFactory = _ => "http"
};

// Commande: send-rag-workflow
var sendRagCmd = new Command("send-rag-workflow", "Envoie un workflow RAG complet avec embedding, search et generation");
sendRagCmd.Add(urlOption);
sendRagCmd.Add(tenantOption);
sendRagCmd.Add(protocolOption);

sendRagCmd.SetAction(async parseResult =>
{
    var url = parseResult.GetValue(urlOption) ?? "http://localhost:4318";
    var tenant = parseResult.GetValue(tenantOption) ?? string.Empty;
    var protocol = parseResult.GetValue(protocolOption) ?? "http";
    Console.WriteLine($"📤 Envoi d'un workflow RAG via OpenTelemetry .NET SDK...");
    Console.WriteLine($"   URL: {url}");
    Console.WriteLine($"   Tenant: {tenant}");
    Console.WriteLine($"   Protocol: {protocol}");
    Console.WriteLine();

    await OtelRagWorkflowSender.SendAsync(url, tenant, protocol);

    Console.WriteLine();
    Console.WriteLine("✅ Workflow RAG envoyé avec succès!");
    Console.WriteLine($"   Ouvrez {url} dans votre navigateur pour visualiser les traces.");
});

// Commande: send-llm-completion
var sendLlmCmd = new Command("send-llm-completion", "Envoie une trace simple de completion LLM");
sendLlmCmd.Add(urlOption);
sendLlmCmd.Add(tenantOption);
sendLlmCmd.Add(protocolOption);

sendLlmCmd.SetAction(async parseResult =>
{
    var url = parseResult.GetValue(urlOption) ?? "http://localhost:4318";
    var tenant = parseResult.GetValue(tenantOption) ?? string.Empty;
    var protocol = parseResult.GetValue(protocolOption) ?? "http";
    Console.WriteLine($"📤 Envoi d'une completion LLM via OpenTelemetry .NET SDK...");
    Console.WriteLine($"   URL: {url}");
    Console.WriteLine($"   Tenant: {tenant}");
    Console.WriteLine($"   Protocol: {protocol}");
    Console.WriteLine();

    await OtelLlmCompletionSender.SendAsync(url, tenant, protocol);

    Console.WriteLine();
    Console.WriteLine("✅ Completion LLM envoyée avec succès!");
});

// Commande: send-logs
var sendLogsCmd = new Command("send-logs", "Envoie des logs de test (TRACE, DEBUG, INFO, WARN, ERROR, FATAL)");
sendLogsCmd.Add(urlOption);
sendLogsCmd.Add(tenantOption);
sendLogsCmd.Add(protocolOption);

sendLogsCmd.SetAction(async parseResult =>
{
    var url = parseResult.GetValue(urlOption) ?? "http://localhost:4318";
    var tenant = parseResult.GetValue(tenantOption) ?? string.Empty;
    var protocol = parseResult.GetValue(protocolOption) ?? "http";
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
});

rootCommand.Add(sendRagCmd);
rootCommand.Add(sendLlmCmd);
rootCommand.Add(sendLogsCmd);

return await rootCommand.Parse(args).InvokeAsync();

