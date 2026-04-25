using GnOuGo.DocsIngestor.Mcp;

var app = DocsIngestorMcpWebHost.Build(args);
await app.RunAsync();

