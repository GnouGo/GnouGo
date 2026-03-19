using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Metadata;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Extractors;

/// <summary>
/// Universal plain-text extractor that handles source code (all languages), configuration files,
/// structured data (JSON, YAML, XML, CSV, TSV), markdown, and any other text-based file.
///
/// This is registered as the <b>last</b> extractor so that binary-format extractors
/// (PDF, DOCX, PPTX, XLSX) get priority. If no other extractor matches, this one
/// acts as a catch-all for anything that looks like text.
/// </summary>
public sealed class PlainTextExtractor : IDocumentTextExtractor
{
    // ── Extension → (MIME type, language/format label) ──────────────

    private static readonly Dictionary<string, (string Mime, string Language)> ExtensionMap
        = new(StringComparer.OrdinalIgnoreCase)
    {
        // Markdown
        [".md"]         = ("text/markdown", "markdown"),
        [".markdown"]   = ("text/markdown", "markdown"),
        [".mdx"]        = ("text/markdown", "mdx"),

        // Plain text
        [".txt"]        = ("text/plain", "text"),
        [".text"]       = ("text/plain", "text"),
        [".log"]        = ("text/plain", "log"),
        [".readme"]     = ("text/plain", "text"),
        [".license"]    = ("text/plain", "text"),
        [".changelog"]  = ("text/plain", "text"),

        // Data / config
        [".json"]       = ("application/json", "json"),
        [".jsonl"]      = ("application/x-ndjson", "jsonl"),
        [".ndjson"]     = ("application/x-ndjson", "ndjson"),
        [".json5"]      = ("application/json5", "json5"),
        [".yaml"]       = ("text/yaml", "yaml"),
        [".yml"]        = ("text/yaml", "yaml"),
        [".toml"]       = ("text/x-toml", "toml"),
        [".xml"]        = ("text/xml", "xml"),
        [".xsd"]        = ("text/xml", "xsd"),
        [".xslt"]       = ("text/xml", "xslt"),
        [".csv"]        = ("text/csv", "csv"),
        [".tsv"]        = ("text/tab-separated-values", "tsv"),
        [".ini"]        = ("text/plain", "ini"),
        [".cfg"]        = ("text/plain", "cfg"),
        [".conf"]       = ("text/plain", "conf"),
        [".env"]        = ("text/plain", "env"),
        [".properties"] = ("text/plain", "properties"),
        [".editorconfig"] = ("text/plain", "editorconfig"),

        // .NET / C#
        [".cs"]         = ("text/x-csharp", "csharp"),
        [".csx"]        = ("text/x-csharp", "csharp-script"),
        [".csproj"]     = ("text/xml", "msbuild"),
        [".sln"]        = ("text/plain", "solution"),
        [".props"]      = ("text/xml", "msbuild"),
        [".targets"]    = ("text/xml", "msbuild"),
        [".nuspec"]     = ("text/xml", "nuspec"),
        [".razor"]      = ("text/x-cshtml", "razor"),
        [".cshtml"]     = ("text/x-cshtml", "razor"),
        [".xaml"]       = ("text/xml", "xaml"),
        [".fsproj"]     = ("text/xml", "msbuild"),
        [".fs"]         = ("text/x-fsharp", "fsharp"),
        [".fsx"]        = ("text/x-fsharp", "fsharp-script"),
        [".vb"]         = ("text/x-vb", "vb"),

        // JavaScript / TypeScript / Web
        [".js"]         = ("text/javascript", "javascript"),
        [".mjs"]        = ("text/javascript", "javascript"),
        [".cjs"]        = ("text/javascript", "javascript"),
        [".jsx"]        = ("text/javascript", "jsx"),
        [".ts"]         = ("text/typescript", "typescript"),
        [".tsx"]        = ("text/typescript", "tsx"),
        [".vue"]        = ("text/html", "vue"),
        [".svelte"]     = ("text/html", "svelte"),
        [".html"]       = ("text/html", "html"),
        [".htm"]        = ("text/html", "html"),
        [".css"]        = ("text/css", "css"),
        [".scss"]       = ("text/x-scss", "scss"),
        [".sass"]       = ("text/x-sass", "sass"),
        [".less"]       = ("text/x-less", "less"),
        [".graphql"]    = ("text/plain", "graphql"),
        [".gql"]        = ("text/plain", "graphql"),

        // Python
        [".py"]         = ("text/x-python", "python"),
        [".pyi"]        = ("text/x-python", "python-stub"),
        [".pyw"]        = ("text/x-python", "python"),
        [".ipynb"]      = ("application/json", "jupyter"),

        // Java / JVM
        [".java"]       = ("text/x-java-source", "java"),
        [".kt"]         = ("text/x-kotlin", "kotlin"),
        [".kts"]        = ("text/x-kotlin", "kotlin-script"),
        [".scala"]      = ("text/x-scala", "scala"),
        [".groovy"]     = ("text/x-groovy", "groovy"),
        [".gradle"]     = ("text/x-groovy", "gradle"),
        [".pom"]        = ("text/xml", "maven"),

        // Go
        [".go"]         = ("text/x-go", "go"),
        [".mod"]        = ("text/plain", "go-mod"),
        [".sum"]        = ("text/plain", "go-sum"),

        // Rust
        [".rs"]         = ("text/x-rust", "rust"),

        // C / C++
        [".c"]          = ("text/x-c", "c"),
        [".h"]          = ("text/x-c", "c-header"),
        [".cpp"]        = ("text/x-c++", "cpp"),
        [".cxx"]        = ("text/x-c++", "cpp"),
        [".cc"]         = ("text/x-c++", "cpp"),
        [".hpp"]        = ("text/x-c++", "cpp-header"),
        [".hxx"]        = ("text/x-c++", "cpp-header"),

        // Ruby
        [".rb"]         = ("text/x-ruby", "ruby"),
        [".erb"]        = ("text/x-ruby", "erb"),
        [".rake"]       = ("text/x-ruby", "rake"),
        [".gemspec"]    = ("text/x-ruby", "gemspec"),

        // PHP
        [".php"]        = ("text/x-php", "php"),

        // Swift / Objective-C
        [".swift"]      = ("text/x-swift", "swift"),
        [".m"]          = ("text/x-objectivec", "objective-c"),

        // Shell / scripting
        [".sh"]         = ("text/x-shellscript", "bash"),
        [".bash"]       = ("text/x-shellscript", "bash"),
        [".zsh"]        = ("text/x-shellscript", "zsh"),
        [".fish"]       = ("text/x-shellscript", "fish"),
        [".ps1"]        = ("text/x-powershell", "powershell"),
        [".psm1"]       = ("text/x-powershell", "powershell"),
        [".psd1"]       = ("text/x-powershell", "powershell"),
        [".bat"]        = ("text/x-bat", "batch"),
        [".cmd"]        = ("text/x-bat", "batch"),

        // SQL
        [".sql"]        = ("text/x-sql", "sql"),

        // Lua
        [".lua"]        = ("text/x-lua", "lua"),

        // R
        [".r"]          = ("text/x-r", "r"),
        [".rmd"]        = ("text/x-r", "r-markdown"),

        // Perl
        [".pl"]         = ("text/x-perl", "perl"),
        [".pm"]         = ("text/x-perl", "perl"),

        // Haskell / Elixir / Erlang
        [".hs"]         = ("text/x-haskell", "haskell"),
        [".ex"]         = ("text/x-elixir", "elixir"),
        [".exs"]        = ("text/x-elixir", "elixir-script"),
        [".erl"]        = ("text/x-erlang", "erlang"),

        // Dart / Flutter
        [".dart"]       = ("text/x-dart", "dart"),

        // Zig / Nim / V
        [".zig"]        = ("text/plain", "zig"),
        [".nim"]        = ("text/plain", "nim"),
        [".v"]          = ("text/plain", "vlang"),

        // Infrastructure / DevOps
        [".tf"]         = ("text/plain", "terraform"),
        [".hcl"]        = ("text/plain", "hcl"),
        [".dockerfile"] = ("text/plain", "dockerfile"),
        [".dockerignore"] = ("text/plain", "dockerignore"),
        [".gitignore"]  = ("text/plain", "gitignore"),
        [".gitattributes"] = ("text/plain", "gitattributes"),
        [".npmrc"]      = ("text/plain", "npmrc"),
        [".nvmrc"]      = ("text/plain", "nvmrc"),
        [".eslintrc"]   = ("text/plain", "eslint"),
        [".prettierrc"] = ("text/plain", "prettier"),
        [".babelrc"]    = ("text/plain", "babel"),

        // Protobuf / Thrift / IDL
        [".proto"]      = ("text/plain", "protobuf"),
        [".thrift"]     = ("text/plain", "thrift"),
        [".avsc"]       = ("application/json", "avro-schema"),

        // Makefile-like (no extension or specific names handled via CanHandle)
        [".mk"]         = ("text/plain", "makefile"),
        [".cmake"]      = ("text/plain", "cmake"),
    };

    // Well-known filenames without extensions
    private static readonly Dictionary<string, (string Mime, string Language)> FileNameMap
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Makefile"]      = ("text/plain", "makefile"),
        ["CMakeLists.txt"]= ("text/plain", "cmake"),
        ["Dockerfile"]    = ("text/plain", "dockerfile"),
        ["Jenkinsfile"]   = ("text/plain", "groovy"),
        ["Vagrantfile"]   = ("text/x-ruby", "ruby"),
        ["Rakefile"]      = ("text/x-ruby", "ruby"),
        ["Gemfile"]       = ("text/x-ruby", "ruby"),
        [".gitignore"]    = ("text/plain", "gitignore"),
        [".dockerignore"] = ("text/plain", "dockerignore"),
        [".editorconfig"] = ("text/plain", "editorconfig"),
        [".env"]          = ("text/plain", "env"),
        [".env.local"]    = ("text/plain", "env"),
        ["LICENSE"]       = ("text/plain", "text"),
        ["README"]        = ("text/plain", "text"),
        ["CHANGELOG"]     = ("text/plain", "text"),
    };

    // Content types that indicate text
    private static readonly HashSet<string> TextContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "text/html",
        "text/css",
        "text/xml",
        "text/csv",
        "text/yaml",
        "text/javascript",
        "text/typescript",
        "application/json",
        "application/xml",
        "application/x-yaml",
        "application/x-ndjson",
    };

    /// <inheritdoc />
    public bool CanHandle(string fileName, string? contentType = null)
    {
        // 1. Check content type
        if (contentType is not null && TextContentTypes.Contains(contentType))
            return true;

        // 2. Check exact filename
        var name = Path.GetFileName(fileName);
        if (FileNameMap.ContainsKey(name))
            return true;

        // 3. Check extension
        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && ExtensionMap.ContainsKey(ext))
            return true;

        return false;
    }

    /// <inheritdoc />
    public async ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken ct = default)
    {
        source.Rewind();
        using var reader = new StreamReader(source.Content, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);

        var name = Path.GetFileName(source.FileName);
        var ext = Path.GetExtension(source.FileName);

        // Resolve metadata
        var (mime, language) = ResolveInfo(name, ext);

        var meta = MetadataDefaults.FromSource(source, mime);
        meta["language"] = language;
        meta["lineCount"] = text.Split('\n').Length.ToString();

        var doc = new ExtractedDocument(
            DocumentId: MakeId(source),
            SourceName: source.FileName,
            MimeType: mime,
            Sections: new[]
            {
                new ExtractedSection(
                    SectionId: $"text:0",
                    Title: name,
                    PageNumber: null,
                    Text: text,
                    Metadata: new Dictionary<string, string>
                    {
                        ["language"] = language,
                        ["format"] = "plain-text"
                    }
                )
            },
            Metadata: meta
        );

        return MetadataDefaults.WithSha256(doc, source.Content);
    }

    // ── Private helpers ─────────────────────────────────────────────

    private static (string Mime, string Language) ResolveInfo(string fileName, string ext)
    {
        if (FileNameMap.TryGetValue(fileName, out var byName))
            return byName;

        if (!string.IsNullOrEmpty(ext) && ExtensionMap.TryGetValue(ext, out var byExt))
            return byExt;

        return ("text/plain", "text");
    }

    private static string MakeId(DocumentSource source)
        => $"{source.FileName}:{source.ComputeSha256()[..12]}";
}

