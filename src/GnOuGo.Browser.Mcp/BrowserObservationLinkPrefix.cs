using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserIndexedLink(int Page, int Offset, int? Ancestor, int? Occurrence, bool Heading,
    [property: Description("Observed destination without its query. Descriptive evidence only. Navigate only to a literal original href.")]
    string? Route,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: Description("Exact observed href for each link with an empty captured attribute, each heading in a populated group, or its first link when it has no heading. For other links read pages[page].records[offset].href.")]
    string? Href);

public sealed record BrowserObservationLinkPrefix(string Attribute, bool ReachedEnd,
    IReadOnlyList<BrowserObservationAncestorAttribute> Ancestors, IReadOnlyList<BrowserIndexedLink> Links)
{
    internal static BrowserObservationLinkPrefix Create(IReadOnlyList<BrowserObservation> pages, string attribute, int count)
    {
        var links = new List<(int Page, int Offset, BrowserObservationRecord Record, BrowserObservationAncestorAttribute? Ancestor)>();
        var groups = new Dictionary<(int, int?), (int Last, bool HasHeading)>();
        int? boundary = null;
        var populated = 0;
        for (var page = 0; page < pages.Count; page++)
        {
            for (var offset = 0; offset < pages[page].Records.Count; offset++)
            {
                var record = pages[page].Records[offset];
                if (record.Kind != "link" || record.Href is null) continue;
                var index = links.Count;
                var ancestor = record.AncestorAttributes?.GetValueOrDefault(attribute);
                links.Add((page, offset, record, ancestor));
                if (ancestor is null) continue;
                var key = (ancestor.Index, record.Occurrence?.Index);
                var seen = groups.TryGetValue(key, out var group);
                groups[key] = (index, group.HasHeading || record.Heading is { Length: > 0 });
                if (!seen && ancestor.Value.Length != 0 && ++populated == count) boundary = index;
            }
        }

        // Close the prefix over every attributed group it touches, even across transport pages
        // or nested/interleaved groups. Missing attributes do not establish a shared DOM group.
        var end = boundary ?? links.Count - 1;
        var descriptors = new List<BrowserIndexedLink>();
        var ancestors = new Dictionary<int, BrowserObservationAncestorAttribute>();
        var emitted = new HashSet<(int, int?)>();
        for (var i = 0; i <= end; i++)
        {
            var (page, offset, record, ancestor) = links[i];
            var heading = record.Heading is { Length: > 0 };
            var includeHref = false;
            if (ancestor is not null)
            {
                var key = (ancestor.Index, record.Occurrence?.Index);
                var group = groups[key];
                end = Math.Max(end, group.Last);
                ancestors.TryAdd(ancestor.Index, ancestor);
                var first = emitted.Add(key);
                includeHref = ancestor.Value.Length == 0 || heading || first && !group.HasHeading;
            }
            var route = Uri.TryCreate(record.Href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri.GetLeftPart(UriPartial.Path) + uri.Fragment : null;
            descriptors.Add(new(page, offset, ancestor?.Index, record.Occurrence?.Index, heading, route, includeHref ? record.Href : null));
        }
        return new(attribute, end == links.Count - 1, ancestors.Values.ToArray(), descriptors);
    }
}
