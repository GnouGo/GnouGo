using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SidebarConversationGroupingTests
{
    [Fact]
    public void Group_UsesEnglishRelativeDatesAndOrdersNewestFirst()
    {
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var sessions = new[]
        {
            Session("old", "Old chat", now.AddDays(-12)),
            Session("today-older", "Earlier today", now.AddHours(-4)),
            Session("yesterday", "Yesterday chat", now.AddDays(-1)),
            Session("today-new", "Newest chat", now.AddMinutes(-5)),
            Session("two-days", "Two days ago", now.AddDays(-2))
        };

        var groups = SidebarConversationGrouping.Group(sessions, now, TimeZoneInfo.Utc);

        Assert.Equal(
            ["Today", "Yesterday", "The day before yesterday", "12 days ago"],
            groups.Select(static group => group.Label));
        Assert.Equal(
            ["today-new", "today-older"],
            groups[0].Sessions.Select(static session => session.Id));
    }

    [Fact]
    public void Group_PutsSessionsWithoutATimestampInOlder()
    {
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        var group = Assert.Single(SidebarConversationGrouping.Group(
            [new ChatSessionDto("missing-date", "Imported chat", 0, [])],
            now,
            TimeZoneInfo.Utc));

        Assert.Equal("Older", group.Label);
        Assert.Equal("missing-date", Assert.Single(group.Sessions).Id);
    }

    private static ChatSessionDto Session(
        string id,
        string title,
        DateTimeOffset updatedAt)
        => new(id, title, updatedAt.ToUnixTimeMilliseconds(), []);
}
