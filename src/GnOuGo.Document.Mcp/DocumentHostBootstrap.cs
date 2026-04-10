using Microsoft.Extensions.Hosting;

namespace GnOuGo.Document.Mcp;


public static class DocumentHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

