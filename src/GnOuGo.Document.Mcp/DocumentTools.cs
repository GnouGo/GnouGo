using GnOuGo.Mcp.Core;
using ModelContextProtocol;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace GnOuGo.Document.Mcp;

[McpServerToolType]
public sealed class DocumentTools
{
    private readonly DocumentOperationHost _host;
    private readonly ILogger<DocumentTools> _logger;

    public DocumentTools(DocumentOperationHost host, ILogger<DocumentTools> logger)
    {
        _host = host;
        _logger = logger;
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Read)]
    [McpServerTool(Name = "document_get_policy", UseStructuredContent = true, OutputSchemaType = typeof(DocumentPolicyInfo)), Description(
        "Returns the active document server policy: allowed file extensions, working roots, max file size. Call this first to discover the default workspace.")]
    public DocumentPolicyInfo GetPolicy() => _host.GetPolicy();

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Read)]
    [McpServerTool(Name = "document_list", UseStructuredContent = true, OutputSchemaType = typeof(DocumentListResult)), Description(
        "Lists files with allowed extensions in a directory inside the workspace. " +
        "Returns relative paths, sizes, and last-modified timestamps. " +
        "Use relative paths only from the workspace root; omit directoryPath to use the default workspace (recommended — only the default workspace is authorized).")]
    public DocumentListResult ListFiles(
        [Description("Relative directory path inside the workspace, or omit/null to use the default working directory (recommended).")] string? directoryPath = null,
        [Description("Whether to search subdirectories recursively (default false).")] bool recursive = false)
    {
        try
        {
            return _host.ListFiles(directoryPath, recursive);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "document_list policy violation");
            return new DocumentListResult(false, "POLICY_VIOLATION", ex.Message, []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "document_list unexpected error");
            return new DocumentListResult(false, "INTERNAL_ERROR", ex.Message, []);
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Read)]
    [McpServerTool(Name = "document_read", UseStructuredContent = true, OutputSchemaType = typeof(DocumentReadResult)), Description(
        "Reads a document (PDF, DOCX, XLSX, PPTX, TXT, MD, CSV, JSON, XML, YAML) " +
        "and returns its text content. For Office/PDF formats, extracts text and optionally " +
        "formats it as markdown. Returns structured sections (pages, sheets, slides). " +
        "Use a relative path only from the workspace root.")]
    public DocumentReadResult Read(
        [Description("Relative file path only inside the workspace root, for example 'docs/report.pdf'.")] string filePath,
        [Description("Output format: 'markdown' (default) or 'plain'.")] string? format = null)
    {
        try
        {
            return _host.Read(filePath, format);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "document_read policy violation for {Path}", filePath);
            return DocumentReadResult.Error("POLICY_VIOLATION", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "document_read unexpected error for {Path}", filePath);
            return DocumentReadResult.Error("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public DocumentWriteResult Write(string filePath, string content, string? encoding = null, bool append = false)
        => WriteDocument(filePath, content, encoding, append, null);

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Write)]
    [McpServerTool(Name = "document_write", UseStructuredContent = true, OutputSchemaType = typeof(DocumentWriteResult)), Description(
        "Writes text content to a file, creating missing parent directories within the allowed workspace. For .docx, automatically detects Markdown and maps headings, lists, emphasis, code, links, blockquotes, and tables to Word structures. " +
        "For .pdf, generates a readable A4 PDF and automatically renders Markdown headings, lists, emphasis, code, links, blockquotes, tables, and separators. " +
        "For .xlsx, generates a spreadsheet from tab/comma-separated text: each nonblank line is a row; tabs separate cells when present, otherwise commas do. Cells are trimmed. Quotes are literal, not CSV escaping. Use optional structured rows instead to preserve exact strings, whitespace and cell boundaries; null cells are blank. Supply exactly one of content or rows. " +
        "For other allowed extensions, writes plain text. " +
        "Allowed targets come from document_get_policy: paths must be relative to the workspace root, resolve inside AllowedRoots, and use an extension from AllowedExtensions. " +
        "Use relative paths only; only workspace paths are authorized. " +
        "For long LLM workflows, initialize the document with append=false, then call document_write repeatedly with append=true as each section is ready. " +
        "This avoids sending one very large final save payload that can exceed the model context window.")]
    public DocumentWriteResult WriteDocument(
        [Description("Relative file path only inside the workspace root, for example 'output/result.md'. The target must match document_get_policy.AllowedRoots and document_get_policy.AllowedExtensions.")] string filePath,
        [Description("Legacy text content; mutually exclusive with rows. XLSX text is split and trimmed; use rows for exact values.")] string? content = null,
        [Description("Text encoding: 'utf-8' (default), 'utf-8-bom', 'ascii', 'latin1'.")] string? encoding = null,
        [Description("When false (default), overwrite/create the target file. When true, append content to an existing document; initialize the file first with append=false, then append incrementally to avoid oversized LLM context payloads.")] bool append = false,
        [Description("XLSX only: ordered arrays of exact strings or null cells. Headers are ordinary rows. No trimming, splitting, numeric conversion or formulas. Mutually exclusive with content.")] string?[][]? rows = null)
    {
        try
        {
            var result = _host.Write(filePath, content, encoding, append, rows);
            if (result.Success && (string.IsNullOrWhiteSpace(result.FilePath) || string.IsNullOrWhiteSpace(result.FilePathAbsolute) ||
                result.BytesWritten is null or < 0 || result.ErrorCode is not null || result.ErrorMessage is not null))
                throw new InvalidOperationException("The completed write did not provide its required receipt.");
            return result;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "document_write policy violation for {Path}", filePath);
            return DocumentWriteResult.Error("POLICY_VIOLATION", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "document_write unexpected error for {Path}", filePath);
            return DocumentWriteResult.Error("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
