using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OtlpTestCli;

public static class OtelRagWorkflowSender
{
    public static async Task SendAsync(string endpoint, string tenantId, string protocol = "http")
    {
        Console.WriteLine("[DEBUG] Starting RAG workflow sender...");
        Console.WriteLine($"[DEBUG] Protocol: {protocol}, Endpoint: {endpoint}");

        // CRITIQUE: Créer l'ActivitySource AVANT le TracerProvider
        var activitySource = new ActivitySource("GnOuGo.OtlpCollector.Cli.RAG", "1.0.0");
        Console.WriteLine("[DEBUG] ActivitySource created BEFORE TracerProvider");

        var otlpProtocol = protocol.ToLowerInvariant() == "grpc"
            ? OpenTelemetry.Exporter.OtlpExportProtocol.Grpc
            : OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;

        var finalEndpoint = protocol.ToLowerInvariant() == "grpc"
            ? endpoint
            : $"{endpoint}/v1/traces";

        Console.WriteLine($"[DEBUG] OTLP Protocol: {otlpProtocol}, Final Endpoint: {finalEndpoint}");

        // NE PAS utiliser `using` ici — on dispose manuellement APRÈS le ForceFlush
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService("rag-test-service", serviceVersion: "1.0.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = "test",
                    ["tenant.id"] = tenantId
                }))
            .AddSource("GnOuGo.OtlpCollector.Cli.RAG")
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(finalEndpoint);
                options.Protocol = otlpProtocol;
                options.Headers = $"X-Tenant-Id={tenantId}";
                // Simple exporte chaque span dès Activity.Stop() — EndTimeUnixNano sera rempli
                options.ExportProcessorType = ExportProcessorType.Simple;
            })
            .Build();

        Console.WriteLine("[DEBUG] TracerProvider configured");
        await Task.Delay(100); // Laisser le listener s'initialiser
        Console.WriteLine($"[DEBUG] ActivitySource.HasListeners: {activitySource.HasListeners()}");

        try
        {
            Console.WriteLine("[DEBUG] Starting workflow...");

            // Le bloc using garantit que TOUS les spans enfants sont fermés
            // avant que le span parent ne se ferme (ordre LIFO des using)
            using (var ragWorkflow = activitySource.StartActivity("rag.workflow", ActivityKind.Internal))
            {
                if (ragWorkflow == null)
                {
                    Console.WriteLine("[ERROR] Failed to create ragWorkflow activity!");
                    Console.WriteLine($"[ERROR] ActivitySource.HasListeners: {activitySource.HasListeners()}");
                    return;
                }

                Console.WriteLine($"[DEBUG] ragWorkflow TraceId={ragWorkflow.TraceId} SpanId={ragWorkflow.SpanId}");
                ragWorkflow.SetTag("workflow.type", "rag");
                ragWorkflow.SetTag("user.query", "Quelles sont les meilleures pratiques pour optimiser un RAG?");

                // Phase 1: Query Processing
                await Task.Delay(10);
                using (var querySpan = activitySource.StartActivity("query.processing", ActivityKind.Internal))
                {
                    Console.WriteLine($"[DEBUG] querySpan created: {querySpan?.OperationName}");
                    querySpan?.SetTag("query.original", "Quelles sont les meilleures pratiques pour optimiser un RAG?");
                    querySpan?.SetTag("query.normalized", "meilleures pratiques optimiser rag");
                    await Task.Delay(40);
                } // querySpan.Stop() + export ici

                // Phase 2: Embedding Generation
                using (var embeddingSpan = activitySource.StartActivity("embedding.generation", ActivityKind.Client))
                {
                    Console.WriteLine($"[DEBUG] embeddingSpan created: {embeddingSpan?.OperationName}");
                    embeddingSpan?.SetTag("gen_ai.system", "openai");
                    embeddingSpan?.SetTag("gen_ai.operation.name", "embeddings");
                    embeddingSpan?.SetTag("gen_ai.request.model", "text-embedding-3-small");
                    embeddingSpan?.SetTag("gen_ai.response.model", "text-embedding-3-small");
                    embeddingSpan?.SetTag("gen_ai.usage.input_tokens", 12);
                    embeddingSpan?.SetTag("embedding.dimensions", 1536);
                    await Task.Delay(120);
                } // embeddingSpan.Stop() + export ici

                // Phase 3: Vector Search
                using (var searchSpan = activitySource.StartActivity("vector.search", ActivityKind.Client))
                {
                    Console.WriteLine($"[DEBUG] searchSpan created: {searchSpan?.OperationName}");
                    searchSpan?.SetTag("db.system", "GnOuGovectordb");
                    searchSpan?.SetTag("db.operation", "similarity_search");
                    searchSpan?.SetTag("search.top_k", 5);
                    searchSpan?.SetTag("search.min_similarity", 0.8);
                    searchSpan?.SetTag("search.results_count", 3);
                    await Task.Delay(160);
                }

                // Phase 4: Documents Retrieval
                using (var retrievalSpan = activitySource.StartActivity("documents.retrieval", ActivityKind.Internal))
                {
                    Console.WriteLine($"[DEBUG] retrievalSpan created: {retrievalSpan?.OperationName}");
                    retrievalSpan?.SetTag("documents.retrieved", 3);
                    retrievalSpan?.SetTag("context.total_chars", 4523);
                    await Task.Delay(90);
                }

                // Phase 5: LLM Generation
                using (var llmSpan = activitySource.StartActivity("llm.chat", ActivityKind.Client))
                {
                    Console.WriteLine($"[DEBUG] llmSpan created: {llmSpan?.OperationName}");
                    llmSpan?.SetTag("gen_ai.system", "openai");
                    llmSpan?.SetTag("gen_ai.operation.name", "chat");
                    llmSpan?.SetTag("gen_ai.request.model", "gpt-4-turbo");
                    llmSpan?.SetTag("gen_ai.response.model", "gpt-4-turbo");
                    llmSpan?.SetTag("gen_ai.request.temperature", 0.7);
                    llmSpan?.SetTag("gen_ai.request.max_tokens", 500);
                    llmSpan?.SetTag("gen_ai.usage.input_tokens", 1245);
                    llmSpan?.SetTag("gen_ai.usage.output_tokens", 387);
                    llmSpan?.SetTag("gen_ai.usage.total_tokens", 1632);

                    llmSpan?.AddEvent(new ActivityEvent("gen_ai.content.prompt",
                        tags: new ActivityTagsCollection
                        {
                            ["gen_ai.prompt"]       = GetFullPrompt(),
                            ["prompt.type"]         = "rag_with_context",
                            ["prompt.tokens"]       = 1245,
                            ["context.documents"]   = 3
                        }));

                    await Task.Delay(2000);

                    llmSpan?.AddEvent(new ActivityEvent("gen_ai.content.completion",
                        tags: new ActivityTagsCollection
                        {
                            ["gen_ai.completion"]           = GetCompletion(),
                            ["gen_ai.usage.output_tokens"]  = 387,
                            ["completion.finish_reason"]    = "stop"
                        }));
                } // llmSpan.Stop() + export ici

                ragWorkflow.SetStatus(ActivityStatusCode.Ok);
                Console.WriteLine("[DEBUG] All child spans closed. Closing ragWorkflow now...");
            } // ragWorkflow.Stop() + export ici — EndTimeUnixNano sera rempli car le using se termine proprement

            Console.WriteLine("[DEBUG] ragWorkflow closed. Waiting briefly before flush...");
            // Petit délai pour laisser l'export gRPC async se terminer avant le ForceFlush
            await Task.Delay(200);
        }
        finally
        {
            // ForceFlush AVANT Dispose pour garantir que tous les exports sont terminés
            Console.WriteLine("[DEBUG] Forcing flush...");
            var flushResult = tracerProvider.ForceFlush(15_000);
            Console.WriteLine($"[DEBUG] Flush result: {flushResult}");

            tracerProvider.Dispose();
            activitySource.Dispose();
        }

        Console.WriteLine("Traces sent successfully!");
    }

    private static string GetFullPrompt() => @"Tu es un assistant expert en RAG (Retrieval-Augmented Generation). 
Réponds à la question suivante en utilisant UNIQUEMENT le contexte fourni ci-dessous.

=== CONTEXTE RÉCUPÉRÉ ===

[Document 1 - Score: 0.89]
Title: ""RAG Architecture Best Practices""
Content: ""Le chunking est crucial pour un RAG efficace. Utilisez des chunks de 512-1024 tokens avec un overlap de 10-20%.""

[Document 2 - Score: 0.85]
Title: ""Vector Database Optimization""
Content: ""L'indexation vectorielle avec HNSW offre un excellent compromis entre performance et précision.""

[Document 3 - Score: 0.82]
Title: ""LLM Prompt Engineering for RAG""
Content: ""Structurez vos prompts RAG en 3 sections: instructions, contexte, question.""

Question: Quelles sont les meilleures pratiques pour optimiser un RAG?";

    private static string GetCompletion() => @"Voici les meilleures pratiques pour optimiser un RAG:

## 1. Chunking Stratégique
- Utilisez des chunks de 512-1024 tokens
- Appliquez un overlap de 10-20%

## 2. Indexation Vectorielle
- Utilisez HNSW pour un bon compromis performance/précision
- Optimisez selon la taille du corpus

## 3. Prompt Engineering
- Structurez en 3 sections: instructions + contexte + question
- Limitez le contexte à 4000-8000 tokens";
}
