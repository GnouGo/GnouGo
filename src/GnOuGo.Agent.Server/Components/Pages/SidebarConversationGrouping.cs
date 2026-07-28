using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server.Components.Pages;

internal sealed record SidebarConversationGroup(
    string Label,
    IReadOnlyList<ChatSessionDto> Sessions);

internal static class SidebarConversationGrouping
{
    public static IReadOnlyList<SidebarConversationGroup> Group(
        IEnumerable<ChatSessionDto> sessions,
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var zone = timeZone ?? TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).Date);

        return sessions
            .OrderByDescending(static session => session.UpdatedAtUnixMs)
            .GroupBy(session => DaysAgo(session.UpdatedAtUnixMs, today, zone))
            .OrderBy(static group => group.Key)
            .Select(group => new SidebarConversationGroup(
                FormatLabel(group.Key),
                group.ToArray()))
            .ToArray();
    }

    private static int DaysAgo(long unixMilliseconds, DateOnly today, TimeZoneInfo timeZone)
    {
        if (unixMilliseconds <= 0)
            return int.MaxValue;

        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(updatedAt, timeZone).Date);
        return Math.Max(0, today.DayNumber - localDate.DayNumber);
    }

    private static string FormatLabel(int daysAgo) => daysAgo switch
    {
        0 => "Today",
        1 => "Yesterday",
        2 => "The day before yesterday",
        int.MaxValue => "Older",
        _ => $"{daysAgo} days ago"
    };
}
