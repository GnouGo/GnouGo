using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public class DataHostBootstrapTests
{
    [Fact]
    public void CreateBuilder_UsesAppContextBaseDirectoryAsContentRoot()
    {
        var builder = DataHostBootstrap.CreateBuilder([]);

        Assert.Equal(AppContext.BaseDirectory, builder.Environment.ContentRootPath);
    }

    [Fact]
    public void CreateBuilder_PassesArgsToBuilder()
    {
        var builder = DataHostBootstrap.CreateBuilder(["--environment", "Testing"]);

        Assert.Equal("Testing", builder.Environment.EnvironmentName);
    }
}

