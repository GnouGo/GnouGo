using GnOuGo.Files.Server.Services;
using Xunit;

namespace GnOuGo.Files.Server.Tests;

public sealed class FileStorageServiceTests
{
    [Fact]
    public void ParseTtl_WhenValueIsMissing_ReturnsDefaultTtl()
    {
        var defaultTtl = TimeSpan.FromHours(12);

        var ttl = FileStorageService.ParseTtl(null, defaultTtl);

        Assert.Equal(defaultTtl, ttl);
    }

    [Fact]
    public void ParseTtl_WhenValueIsTimeSpan_ReturnsParsedTimeSpan()
    {
        var ttl = FileStorageService.ParseTtl("00:30:00", TimeSpan.FromHours(12));

        Assert.Equal(TimeSpan.FromMinutes(30), ttl);
    }

    [Fact]
    public void ParseTtl_WhenValueIsNumber_ReturnsHours()
    {
        var ttl = FileStorageService.ParseTtl("1.5", TimeSpan.FromHours(12));

        Assert.Equal(TimeSpan.FromMinutes(90), ttl);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-00:01:00")]
    [InlineData("not-a-ttl")]
    public void ParseTtl_WhenValueIsInvalid_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => FileStorageService.ParseTtl(value, TimeSpan.FromHours(12)));
    }
}


