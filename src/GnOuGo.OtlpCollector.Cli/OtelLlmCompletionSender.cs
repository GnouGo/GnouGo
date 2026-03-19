﻿using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OtlpTestCli;

public static class OtelLlmCompletionSender
{
    public static async Task SendAsync(string endpoint, string tenantId, string protocol = "http")
    {
        Console.WriteLine($"[DEBUG LLM] Protocol: {protocol}, Endpoint: {endpoint}");
        
        // Créer l'ActivitySource AVANT le TracerProvider
        var activitySource = new ActivitySource("GnOuGo.OtlpCollector.Cli.LLM", "1.0.0");
        
        // Déterminer le protocole OTLP et l'URL finale
        var otlpProtocol = protocol.ToLowerInvariant() == "grpc" 
            ? OpenTelemetry.Exporter.OtlpExportProtocol.Grpc 
            : OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        
        var finalEndpoint = protocol.ToLowerInvariant() == "grpc" 
            ? endpoint 
            : $"{endpoint}/v1/traces";
        
        Console.WriteLine($"[DEBUG LLM] Final Endpoint: {finalEndpoint}");
        
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService("llm-test-service", serviceVersion: "1.0.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = "test",
                    ["tenant.id"] = tenantId
                }))
            .AddSource("GnOuGo.OtlpCollector.Cli.LLM")
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(finalEndpoint);
                options.Protocol = otlpProtocol;
                options.Headers = $"X-Tenant-Id={tenantId}";
                // CRITIQUE: SimpleExportProcessor exporte chaque span APRÈS Activity.Stop()
                // Batch peut exporter AVANT la fermeture → EndTimeUnixNano = 0
                options.ExportProcessorType = ExportProcessorType.Simple;
            })
            .Build();

        // Attendre que le listener soit initialisé
        await Task.Delay(100);
        
        Console.WriteLine($"[DEBUG LLM] ActivitySource.HasListeners: {activitySource.HasListeners()}");

        using (var completionSpan = activitySource.StartActivity("chat.completion", ActivityKind.Client))
        {
            if (completionSpan == null)
            {
                Console.WriteLine("[ERROR LLM] Failed to create completionSpan activity!");
                Console.WriteLine($"[ERROR LLM] HasListeners: {activitySource.HasListeners()}");
                return;
            }
            
            Console.WriteLine($"[DEBUG LLM] Created completionSpan: {completionSpan.OperationName}");

            completionSpan?.SetTag("gen_ai.system", "anthropic");
        completionSpan?.SetTag("gen_ai.operation.name", "chat");
        completionSpan?.SetTag("gen_ai.request.model", "claude-3-5-sonnet-20241022");
        completionSpan?.SetTag("gen_ai.response.model", "claude-3-5-sonnet-20241022");
        completionSpan?.SetTag("gen_ai.request.temperature", 0.9);
        completionSpan?.SetTag("gen_ai.request.max_tokens", 1024);
        completionSpan?.SetTag("gen_ai.usage.input_tokens", 234);
        completionSpan?.SetTag("gen_ai.usage.output_tokens", 456);
        completionSpan?.SetTag("gen_ai.usage.total_tokens", 690);

        // Événement prompt
        var promptEvent = new ActivityEvent("gen_ai.content.prompt",
            tags: new ActivityTagsCollection
            {
                ["gen_ai.prompt"] = GetDetailedPrompt(),
                ["prompt.role"] = "user",
                ["prompt.tokens"] = 234
            });
        completionSpan?.AddEvent(promptEvent);

        await Task.Delay(1800);

        // Événement completion
        var completionEvent = new ActivityEvent("gen_ai.content.completion",
            tags: new ActivityTagsCollection
            {
                ["gen_ai.completion"] = GetDetailedCompletion(),
                ["gen_ai.usage.output_tokens"] = 456,
                ["completion.finish_reason"] = "stop",
                ["completion.role"] = "assistant"
            });
        completionSpan?.AddEvent(completionEvent);

        completionSpan?.SetStatus(ActivityStatusCode.Ok);
        } // Fin du using - span fermé

        Console.WriteLine("[DEBUG LLM] Span closed. Flushing...");
        
        // ForceFlush pour garantir que tout est envoyé
        Console.WriteLine("[DEBUG LLM] Forcing flush...");
        tracerProvider.ForceFlush(10000);
        
        Console.WriteLine("Traces sent successfully!");
    }

    private static string GetDetailedPrompt() => @"Tu es un expert en intelligence artificielle avec une spécialisation en IA générative.

CONTEXTE: Un développeur junior souhaite comprendre les concepts de base de l'IA générative.

TÂCHE: Explique les concepts fondamentaux de l'IA générative en 3 points principaux.

CONTRAINTES:
- Chaque point doit être clair et concis
- Utilise des exemples concrets
- Niveau: débutant à intermédiaire

Question: Quels sont les 3 concepts fondamentaux de l'IA générative?";

    private static string GetDetailedCompletion() => @"Voici les 3 concepts fondamentaux de l'IA générative:

## 1. Modèles de Langage de Grande Taille (LLMs)
Les LLMs sont des réseaux neuronaux entraînés sur d'énormes corpus de texte. Ils apprennent les patterns linguistiques et des connaissances factuelles.

**Exemple**: GPT-4, Claude, LLaMA sont entraînés sur des milliards de pages.

## 2. Génération Probabiliste et Tokens
Les LLMs prédisent le token le plus probable suivant, basé sur le contexte.

**Exemple**: ""Le chat dort sur le..."" → canapé (35%), lit (28%), tapis (20%)

## 3. Prompts et Ingénierie de Prompts
Le prompt est votre instruction au modèle. Un bon prompt = bonne réponse.

**Exemple fort**: ""Tu es un prof. Explique l'IA en 3 points avec des métaphores simples.""

Ces 3 concepts forment la base de l'IA générative moderne! 🚀";
}

