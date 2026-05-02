using Microsoft.Extensions.Hosting;

namespace GnOuGo.Code.Mcp;

public static class CodeHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

