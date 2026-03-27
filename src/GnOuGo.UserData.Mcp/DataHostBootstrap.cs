using Microsoft.Extensions.Hosting;

namespace GnOuGo.UserData.Mcp;

public static class DataHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

