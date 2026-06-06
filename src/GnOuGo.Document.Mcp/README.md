# GnOuGo.Document.Mcp

MCP (Model Context Protocol) stdio server for reading and writing document files.

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| PDF    | ✅ (text extraction via PdfPig) | ✅ (plain text or auto-detected Markdown) |
| DOCX   | ✅ (paragraphs, tables, headings) | ✅ (plain text or auto-detected Markdown) |
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

## DOCX Markdown Output

When `document_write` targets a `.docx` file, the server detects Markdown-like input automatically and converts common Markdown structures to Word/OpenXML structures:

- `#` through `######` headings become Word heading styles.
- `-`, `*`, and `+` list items become bullet lists.
- `1.` / `1)` list items become numbered lists.
- `**bold**`, `*italic*`, and inline `` `code` `` become formatted Word runs.
- `[text](https://example.com)` and relative links become Word hyperlinks.
- `>` blockquotes become quote-styled paragraphs.
- `---`, `***`, `___`, and `===`-style separator lines become horizontal separators.
- fenced code blocks use a monospace code style.
- Markdown tables become Word tables, including inline formatting inside cells and tables with or without outer `|` characters.

Plain text content still writes as one paragraph per line.

## PDF Markdown Output

When `document_write` targets a `.pdf` file, the server generates a readable A4 PDF with automatic Markdown rendering for headings, lists, emphasis, inline code, links, blockquotes, fenced code blocks, tables, and separator lines. Links are rendered as readable link text with their URL visible when useful.

PdfPig's PDF writer supports Standard 14 fonts and ASCII text. Non-ASCII characters are normalized to ASCII equivalents where possible for generated PDFs.

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
