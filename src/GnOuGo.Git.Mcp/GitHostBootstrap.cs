using Microsoft.Extensions.Hosting;

namespace GnOuGo.Git.Mcp;

public static class GitHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

