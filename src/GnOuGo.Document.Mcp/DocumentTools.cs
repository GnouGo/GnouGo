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

    [McpServerTool(Name = "document_get_policy"), Description(
        "Returns the active document server policy: allowed file extensions, working roots, max file size.")]
    public DocumentPolicyInfo GetPolicy() => _host.GetPolicy();

    [McpServerTool(Name = "document_list"), Description(
        "Lists files with allowed extensions in a directory inside the workspace. " +
        "Returns relative paths, sizes, and last-modified timestamps.")]
    public DocumentListResult ListFiles(
        [Description("Relative or absolute directory path. Omit to use default working directory.")] string? directoryPath = null,
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

    [McpServerTool(Name = "document_read"), Description(
        "Reads a document (PDF, DOCX, XLSX, PPTX, TXT, MD, CSV, JSON, XML, YAML) " +
        "and returns its text content. For Office/PDF formats, extracts text and optionally " +
        "formats it as markdown. Returns structured sections (pages, sheets, slides).")]
    public DocumentReadResult Read(
        [Description("Relative or absolute file path to read.")] string filePath,
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

    [McpServerTool(Name = "document_write"), Description(
        "Writes text content to a file. For .docx, generates a simple Word document. " +
        "For .xlsx, generates a spreadsheet from tab/comma-separated text. " +
        "For other allowed extensions, writes plain text.")]
    public DocumentWriteResult Write(
        [Description("Relative or absolute file path to write.")] string filePath,
        [Description("Text content to write. For .xlsx, use tab or comma separated values.")] string content,
        [Description("Text encoding: 'utf-8' (default), 'utf-8-bom', 'ascii', 'latin1'.")] string? encoding = null)
    {
        try
        {
            return _host.Write(filePath, content, encoding);
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

