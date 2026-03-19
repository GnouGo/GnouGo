using Microsoft.Extensions.Hosting;

namespace GnOuGo.Flow.Cmd;

public static class CmdHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

