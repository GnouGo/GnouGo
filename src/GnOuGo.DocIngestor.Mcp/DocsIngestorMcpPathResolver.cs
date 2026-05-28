using GnOuGo.Workspace;

namespace GnOuGo.DocIngestor.Mcp;

public static class DocsIngestorMcpPathResolver
{
    public static string Resolve(string? configuredPath, string baseDirectory, string defaultRelativePath)
        => GnOuGoWorkspace.ResolveDatabasePath(configuredPath, baseDirectory, defaultRelativePath);
}
