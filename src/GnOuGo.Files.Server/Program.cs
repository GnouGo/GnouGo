using GnOuGo.Files.Server;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});

builder.Services.AddGnOuGoFilesServer(builder.Configuration);
builder.Services.AddRouting();

var app = builder.Build();

await app.Services.InitializeGnOuGoFilesServerAsync();

app.MapGnOuGoFilesServer();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();





