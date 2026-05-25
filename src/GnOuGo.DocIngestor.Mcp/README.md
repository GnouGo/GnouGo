# GnOuGo.DocIngestor.Mcp

HTTP MCP server for document ingestion. It downloads internal file URLs, extracts text/chunks with `GnOuGo.DocIngestor.Core`, optionally resolves an embedding configuration from `GnOuGo.KeyVault.Core`, stores original files, ingests chunks into the SQLite vector store, and exposes list/search/download/delete operations.

## Build

```powershell
dotnet build src/GnOuGo.DocIngestor.Mcp/GnOuGo.DocIngestor.Mcp.csproj
```

## Native AOT publish

The MCP executable is configured for Native AOT and trimming. It avoids EF Core in the AOT process by reading the shared KeyVault SQLite schema through explicit `Microsoft.Data.Sqlite` commands and uses source-generated `System.Text.Json` metadata for MCP tool schemas/results.

```powershell
dotnet publish src/GnOuGo.DocIngestor.Mcp/GnOuGo.DocIngestor.Mcp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish/doc-ingestor-mcp-aot/win-x64 `
  -p:PublishAot=true `
  -p:PublishTrimmed=true `
  -p:InvariantGlobalization=false
```

## Test

```powershell
dotnet test tests/GnOuGo.DocIngestor.Mcp.Tests/GnOuGo.DocIngestor.Mcp.Tests.csproj
```

## Run

```powershell
dotnet run --project src/GnOuGo.DocIngestor.Mcp/GnOuGo.DocIngestor.Mcp.csproj --urls http://localhost:5088
```

Health check:

```powershell
Invoke-RestMethod http://localhost:5088/health
```

MCP endpoint:

```text
http://localhost:5088/mcp
```

## MCP tools

- `docs_ingestor_vectorize_files`: downloads URLs and returns ordered chunks/metadata without storing.
- `docs_ingestor_ingest_files`: downloads URLs, resolves an embedding config, stores originals, embeds chunks, and ingests vectors. If the stored hash is unchanged, ingestion is skipped. If the hash changed, old chunks and original are deleted before replacement.
- `docs_ingestor_list_files`: lists stored originals and vectorized files.
- `docs_ingestor_vector_search`: embeds query text with a KeyVault-backed embedding config and searches vector chunks.
- `docs_ingestor_download_original`: returns original file bytes as base64.
- `docs_ingestor_delete_file`: deletes an original and all associated chunks.

## Configuration

`appsettings.json` section:

```json
"DocsIngestorMcp": {
  "DatabasePath": "data/gnougo-docs-ingestor-mcp.db",
  "VectorDatabasePath": "data/gnougo-docs-ingestor-vectors.sqlite",
  "OriginalsDirectory": "data/docs-ingestor/originals",
  "DefaultCollection": "default",
  "DefaultEmbeddingConfigName": "hash-384",
  "DefaultTenantId": "default"
}
```

Relative `data/...` paths are resolved under `<Desktop>/GnOuGo/...` for local process sharing.

## KeyVault embedding config schema

For local tests, `hash-384` and `hash-768` are built in. For external embedding providers, store a JSON secret in KeyVault under the embedding config name:

```json
{
  "provider": "openai-compatible",
  "name": "ada3-large",
  "endpointUrl": "https://api.openai.com/v1",
  "model": "text-embedding-3-large",
  "apiKeySecretKey": "openai-api-key",
  "dimensions": 3072
}
```

Ollama example:

```json
{
  "provider": "ollama",
  "name": "ollama-nomic",
  "baseUrl": "http://localhost:11434",
  "model": "nomic-embed-text",
  "dimensions": 768
}
```

Secrets remain encrypted at rest by `GnOuGo.KeyVault.Core`.
`GnOuGo.DocIngestor.Mcp` reads those encrypted secrets directly from the shared KeyVault SQLite database during Native AOT execution; secret values are still decrypted only in memory when needed for embedding configuration/API-key resolution.

