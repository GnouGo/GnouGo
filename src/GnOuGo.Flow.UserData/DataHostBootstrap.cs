using Microsoft.Extensions.Hosting;

namespace GnOuGo.Flow.UserData;

public static class DataHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
        => new(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
}

