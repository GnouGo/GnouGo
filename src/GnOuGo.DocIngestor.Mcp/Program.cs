using GnOuGo.DocIngestor.Mcp;

var app = DocsIngestorMcpWebHost.Build(args);
await app.RunAsync();

