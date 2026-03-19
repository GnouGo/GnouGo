using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Chunking;

/// <summary>
/// Resolves the effective <see cref="ChunkingMode"/> for a document.
///
/// When the user requests <see cref="ChunkingMode.Auto"/>, the resolver picks
/// the best strategy based on the document's MIME type:
///
/// <list type="bullet">
///   <item><b>Recursive</b> — for plain-text files (source code, config, JSON, YAML, CSV,
///   markdown, etc.). These files have no "semantic paragraph" structure that would benefit
///   from embedding-based merging. Splitting on double-newlines / sentences is ideal.</item>
///   <item><b>Semantic</b> — for narrative documents (PDF, DOCX, PPTX) where paragraphs
///   carry semantic meaning and adjacent paragraphs often discuss the same topic.
///   Embedding-based merging produces better retrieval quality.</item>
/// </list>
///
/// If the user explicitly requests Recursive or Semantic, that choice is honored as-is.
/// </summary>
public static class ChunkingModeResolver
{
    // MIME prefixes/values that indicate plain-text / code / config / data files.
    // These always get Recursive chunking.
    private static readonly HashSet<string> RecursiveMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Plain text
        "text/plain",
        "text/markdown",
        "text/html",
        "text/css",
        "text/csv",
        "text/xml",
        "text/yaml",
        "text/tab-separated-values",

        // Code
        "text/javascript",
        "text/typescript",
        "text/x-csharp",
        "text/x-python",
        "text/x-java-source",
        "text/x-c",
        "text/x-c++",
        "text/x-go",
        "text/x-rust",
        "text/x-ruby",
        "text/x-php",
        "text/x-swift",
        "text/x-kotlin",
        "text/x-scala",
        "text/x-groovy",
        "text/x-fsharp",
        "text/x-vb",
        "text/x-cshtml",
        "text/x-scss",
        "text/x-sass",
        "text/x-less",
        "text/x-shellscript",
        "text/x-powershell",
        "text/x-bat",
        "text/x-sql",
        "text/x-lua",
        "text/x-r",
        "text/x-perl",
        "text/x-haskell",
        "text/x-elixir",
        "text/x-erlang",
        "text/x-dart",
        "text/x-objectivec",
        "text/x-toml",

        // Data
        "application/json",
        "application/json5",
        "application/xml",
        "application/x-yaml",
        "application/x-ndjson",
    };

    /// <summary>
    /// Resolve the effective chunking mode for the given document.
    /// </summary>
    public static ChunkingMode Resolve(ChunkingMode requested, ExtractedDocument doc)
    {
        // Explicit choice → honor it
        if (requested is ChunkingMode.Recursive or ChunkingMode.Semantic)
            return requested;

        // Auto mode → decide based on MIME type
        return IsPlainTextDocument(doc.MimeType)
            ? ChunkingMode.Recursive
            : ChunkingMode.Semantic;
    }

    private static bool IsPlainTextDocument(string mimeType)
    {
        if (RecursiveMimeTypes.Contains(mimeType))
            return true;

        // Any "text/*" MIME type we don't explicitly list is still text
        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

