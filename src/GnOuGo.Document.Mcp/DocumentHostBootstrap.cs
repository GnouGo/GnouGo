using Microsoft.Extensions.Hosting;

<<<<<<<< HEAD:src/GnOuGo.Document.Mcp/DocumentHostBootstrap.cs
namespace GnOuGo.Document.Mcp;
========
namespace GnOuGo.Agent.Mcp;
>>>>>>>> main:src/GnOuGo.Agent.Mcp/DataHostBootstrap.cs

public static class DocumentHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

