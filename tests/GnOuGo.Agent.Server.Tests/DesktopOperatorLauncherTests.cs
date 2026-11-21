using GnOuGo.Agent.Server.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DesktopOperatorLauncherTests
{
    [Fact]
    public void NativeWindowHandoffQueuesUntilCreatedAndUsesTheSingleUseBootstrap()
    {
        var bootstrap = new LocalOperatorBootstrap();
        var address = bootstrap.CreateLaunchAddress(new Uri("http://127.0.0.1:12345"));
        var loaded = new List<string>(); var invoked = 0;
        var launcher = new DesktopOperatorLauncher(action => { invoked++; action(); }, loaded.Add, new string('a', 32), false);
        launcher.Open(address); Assert.Empty(loaded); launcher.WindowCreated();
        var target = new Uri(Assert.Single(loaded)); Assert.Equal(1, invoked);
        Assert.Equal(address.Fragment, target.Fragment); Assert.Equal("/local-operator", target.AbsolutePath);
        Assert.Equal("?desktopToken=" + new string('a', 32), target.Query);
        Assert.True(bootstrap.Consume(target.Fragment[1..])); Assert.False(bootstrap.Consume(target.Fragment[1..]));
        launcher.WindowCreated(); Assert.Single(loaded);
        var fresh = bootstrap.CreateLaunchAddress(new Uri("http://127.0.0.1:12345"));
        launcher.Open(fresh); Assert.Equal(2, loaded.Count); Assert.Equal(2, invoked);
        Assert.Throws<ArgumentException>(() => bootstrap.CreateLaunchAddress(new Uri("https://foreign.invalid")));
    }
}
