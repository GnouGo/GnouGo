using GnOuGo.Agent.Server.Hosting;

if (args is ["--planning-persistence-smoke", var directory])
{
    await GnOuGo.Agent.Server.Planning.PlanningPersistenceSmoke.RunAsync(Path.GetFullPath(directory));
    return;
}

var app = GnOuGoAgentWebHost.Build(args);
app.Run();
