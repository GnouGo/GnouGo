using GnOuGo.KeyVault.Mcp;

var app = KeyVaultMcpWebHost.Build(args);
await app.RunAsync();

