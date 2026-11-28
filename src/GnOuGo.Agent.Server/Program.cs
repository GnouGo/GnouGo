using GnOuGo.Agent.Server.Hosting;

if (args is ["--planning-persistence-smoke", var directory])
{
    await GnOuGo.Agent.Server.Planning.PlanningPersistenceSmoke.RunAsync(Path.GetFullPath(directory));
    return;
}

if (args is ["--owner-retention-smoke", "write" or "verify", var scratch])
{
    await GnOuGo.Agent.Server.Planning.OwnerRetentionSmoke.RunAsync(Path.GetFullPath(scratch), args[1] == "write");
    return;
}

var app = GnOuGoAgentWebHost.Build(args);
app.Run();
