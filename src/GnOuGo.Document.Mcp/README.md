# GnOuGo.Document.Mcp

MCP (Model Context Protocol) stdio server for reading and writing document files.

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| PDF    | ✅ (text extraction via PdfPig) | ❌ |
| DOCX   | ✅ (paragraphs, tables, headings) | ✅ (simple paragraphs) |
| XLSX   | ✅ (sheets → markdown tables) | ✅ (from TSV/CSV text) |
| PPTX   | ✅ (slides → markdown) | ❌ |
| TXT, MD, CSV, JSON, XML, YAML | ✅ | ✅ |

## MCP Tools

| Tool | Description |
|------|-------------|
| `document_get_policy` | Returns active policy (roots, extensions, limits) |
| `document_list` | Lists allowed files in a directory |
| `document_read` | Reads a document, returns structured sections (markdown or plain) |
| `document_write` | Writes content to a file (generates DOCX/XLSX for Office formats) |

## Build

```bash
dotnet build src/GnOuGo.Document.Mcp/
```

## Test

```bash
dotnet test tests/GnOuGo.Document.Mcp.Tests/
```

## Run

```bash
dotnet run --project src/GnOuGo.Document.Mcp/
```

## Native AOT Publish (win-x64)

```bash
dotnet publish src/GnOuGo.Document.Mcp/GnOuGo.Document.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true
```

## Bundled Agent publishes

`GnOuGo.Document.Mcp` is also bundled by:

- `GnOuGo.Agent.Server` publishes under `tools/GnOuGo.Document.Mcp/`
- `GnOuGo.Agent.Desktop` publishes under `tools/GnOuGo.Document.Mcp/`

This allows packaged agent/server builds to start the document MCP server without requiring the source tree.

## Configuration (`appsettings.json`)

```json
{
  "Document": {
    "DefaultWorkingDirectory": "GnOuGo",
    "MaxFileSizeBytes": 52428800,
    "AllowedExtensions": [".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml"],
    "AllowedWorkingRoots": []
  }
}
```

## Libraries

- **DocumentFormat.OpenXml** — DOCX, XLSX, PPTX reading and writing
- **PdfPig** — PDF text extraction
- **ModelContextProtocol** — MCP stdio transport

