﻿﻿namespace DocIngestor.Cli.Commands;

/// <summary>
/// Affiche l'aide de l'application.
/// </summary>
public static class HelpCommand
{
    public static void PrintUsage()
    {
        Console.WriteLine("DocIngestor CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  ingest --path <fileOrDir> [options]");
        Console.WriteLine("  search --query \"...\" [options]");
        Console.WriteLine();
        
        Console.WriteLine("Ingest Options:");
        Console.WriteLine("  --path <path>                     File or directory to ingest (required)");
        Console.WriteLine("  --mode <auto|recursive|semantic>  Chunking mode (default: auto)");
        Console.WriteLine("  --minTokens <int>                 Minimum tokens per chunk (default: 400)");
        Console.WriteLine("  --targetTokens <int>              Target tokens per chunk (default: 1200)");
        Console.WriteLine("  --maxTokens <int>                 Maximum tokens per chunk (default: 2000)");
        Console.WriteLine("  --overlapTokens <int>             Overlap tokens (default: 400)");
        Console.WriteLine("  --embed <true|false>              Enable embedding (default: true)");
        Console.WriteLine("  --model <model>                   Embedding model (default: hash-384)");
        Console.WriteLine("  --store <true|false>              Enable vector store (default: false)");
        Console.WriteLine("  --storeName <sqlite|jsonl>        Store type (default: sqlite)");
        Console.WriteLine("  --storeDir <path>                 Store directory (default: ./_store)");
        Console.WriteLine("  --collection <name>               Collection name (default: default)");
        Console.WriteLine("  --images <true|false>             Extract images (default: false)");
        Console.WriteLine("  --loadImageBytes <true|false>     Load image bytes (default: false)");
        Console.WriteLine("  --ocr <true|false>                Enable OCR (default: false)");
        Console.WriteLine("  --ocr-lang <lang>                 OCR language (default: eng)");
        Console.WriteLine("  --ocr-dpi <int>                   OCR DPI (default: 300)");
        Console.WriteLine("  --debug <true|false>              Enable debug mode (default: false)");
        Console.WriteLine("  --debugDir <path>                 Debug output directory (default: ./_debug)");
        Console.WriteLine();
        
        Console.WriteLine("Search Options:");
        Console.WriteLine("  --query <text>                    Search query (required)");
        Console.WriteLine("  --storeName <sqlite|jsonl>        Store type (default: sqlite)");
        Console.WriteLine("  --storeDir <path>                 Store directory (default: ./_store)");
        Console.WriteLine("  --collection <name>               Collection name (default: default)");
        Console.WriteLine("  --topK <int>                      Number of results (default: 5)");
        Console.WriteLine("  --model <model>                   Embedding model (default: hash-384)");
        Console.WriteLine();
        
        Console.WriteLine("Reranker Options:");
        Console.WriteLine("  --rerank <true|false>             Enable reranking (default: false)");
        Console.WriteLine("  --reranker <type>                 Reranker type: bm25, cross-encoder-openai, cross-encoder-ollama");
        Console.WriteLine("  --vector-weight <0.0-1.0>         Weight for vector similarity score (default: 0.5)");
        Console.WriteLine("  --rerank-weight <0.0-1.0>         Weight for reranker score (default: 0.5)");
        Console.WriteLine();
        
        Console.WriteLine("Embedding Model Options (for ada3-large):");
        Console.WriteLine("  --endpoint-url <url>              API endpoint URL (required for ada3-large)");
        Console.WriteLine();
        Console.WriteLine("  Authentication (choose one):");
        Console.WriteLine("    Option 1 - Simple API Key:");
        Console.WriteLine("      --api-key <key>               API key for Bearer authentication");
        Console.WriteLine();
        Console.WriteLine("    Option 2 - OIDC (advanced):");
        Console.WriteLine("      --oidc-issuer <url>           OIDC issuer URL");
        Console.WriteLine("      --oidc-client-id <id>         OIDC client ID");
        Console.WriteLine("      --oidc-scopes <scopes>        OIDC scopes (space-separated)");
        Console.WriteLine("      --oidc-client-secret <secret> OIDC client secret");
        Console.WriteLine("      --oidc-private-key-path <path> Path to OIDC private key (PEM)");
        Console.WriteLine();
        
        Console.WriteLine("OpenTelemetry Options:");
        Console.WriteLine("  --enable-otel <true|false>        Enable OpenTelemetry (default: true)");
        Console.WriteLine("  --otlp-endpoint <url>             OTLP collector endpoint (default: http://localhost:4317)");
        Console.WriteLine("  --tenant-id <id>                  Tenant identifier for multi-tenancy");
        Console.WriteLine();
        
        Console.WriteLine("Ollama Options (local models):");
        Console.WriteLine("  Configure in appsettings.json → \"Ollama\" section");
        Console.WriteLine("  Embedding models: ollama-nomic-embed-text, ollama-mxbai-embed-large, ...");
        Console.WriteLine("  Reranker:         cross-encoder-ollama (uses Ollama chat model locally)");
        Console.WriteLine();
        
        Console.WriteLine("Available Embedding Models:");
        Console.WriteLine("  hash-384                          Local hash (384 dims, no API needed)");
        Console.WriteLine("  hash-768                          Local hash (768 dims, no API needed)");
        Console.WriteLine("  ada3-large                        OpenAI text-embedding-3-large (3072 dims)");
        Console.WriteLine("  ollama-nomic-embed-text           Ollama local (768 dims, requires Ollama)");
        Console.WriteLine();
        
        Console.WriteLine("Available Rerankers:");
        Console.WriteLine("  bm25                              Local BM25 text matching (free, fast)");
        Console.WriteLine("  cross-encoder-openai              OpenAI LLM scoring (accurate, paid)");
        Console.WriteLine("  cross-encoder-ollama              Ollama local LLM scoring (free, local)");
        Console.WriteLine();
        
        Console.WriteLine("Configuration:");
        Console.WriteLine("  Default values are loaded from appsettings.json");
        Console.WriteLine("  Command-line arguments override configuration file values");
        Console.WriteLine();
    }
}

