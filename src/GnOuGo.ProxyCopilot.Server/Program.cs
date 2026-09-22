using GnOuGo.ProxyCopilot.Server;

await using var app = ProxyApplication.Build(args);
await app.RunAsync();
