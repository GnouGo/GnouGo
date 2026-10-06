using System.Globalization;
using System.Text.Json;
using System.ComponentModel;
using Microsoft.Playwright;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserObservationRecord(string Kind, string Tag, string Selector, string Group, string Text, string? Href, string? Role);
public sealed record BrowserObservation(string Id, IReadOnlyList<BrowserObservationRecord> Records, string? NextCursor, bool CaptureTruncated);
public sealed record BrowserObservationPage(string Cursor, int RecordCount);
public sealed record BrowserObservationManifest(string Id, [property: Description("Frozen page descriptors, at most 100 or the stricter host/response allowance. ManifestTruncated identifies an incomplete list.")] IReadOnlyList<BrowserObservationPage> Pages,
    int RecordCount, bool CaptureTruncated, bool ManifestTruncated);
internal sealed record BrowserObservationCapture(List<BrowserObservationRecord> Records, bool Truncated);

public sealed partial class PlaywrightBrowserHost
{
    private ObservationSnapshot? _observation;
    private sealed record ObservationSnapshot(string Id, BrowserContentResult Result, BrowserObservationCapture Capture, bool Paged = false)
    {
        internal List<BrowserContentResult> Pages { get; } = [];
    }

    private async Task<BrowserContentResult> CaptureObservationAsync(IPage page, ILocator locator, ContentLocatorResolution resolution,
        string? selector, int? status, int? characters, int? records, CancellationToken ct, bool paged = false)
    {
        ct.ThrowIfCancellationRequested();
        var json = await locator.EvaluateAsync<string>(ObservationScript).WaitAsync(ct);
        var capture = JsonSerializer.Deserialize(json, BrowserMcpJsonContext.Default.BrowserObservationCapture)
            ?? throw new InvalidOperationException("The browser returned an invalid observation.");
        var result = new BrowserContentResult(page.Url, await page.TitleAsync().WaitAsync(ct), status, selector,
            resolution.ResolvedSelector, resolution.FallbackApplied, resolution.FallbackReason, paged ? "observation_pages" : "observation", "", false, 0);
        var snapshot = new ObservationSnapshot(Guid.NewGuid().ToString("N"), result, capture, paged);
        _observation = snapshot;
        return paged ? ObservationManifest(snapshot, characters, records, ct) : ObservationPage(snapshot, 0, characters, records, ct);
    }

    private BrowserContentResult ContinueObservation(string cursor, int? characters, int? records, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parts = cursor.Split(':');
        var snapshot = _observation;
        // The issued snapshot owns the cursor layout and frozen page bounds.
        if (snapshot is { Paged: true })
        {
            if (parts.Length != 3 || parts[1] != "page" || parts[0] != snapshot.Id ||
                GetRequiredPage().Url != snapshot.Result.Url || characters is not null || records is not null ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= snapshot.Pages.Count)
                throw new InvalidOperationException("The observation page cursor is invalid or expired. Page limits are frozen; omit maxCharacters and maxRecords when reading it.");
            return snapshot.Pages[index];
        }
        if (parts.Length != 2 || snapshot is null || parts[0] != snapshot.Id || GetRequiredPage().Url != snapshot.Result.Url ||
            snapshot.Paged ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0 || offset >= snapshot.Capture.Records.Count)
            throw new InvalidOperationException("The observation cursor is invalid or expired. Capture the current page again.");
        return ObservationPage(snapshot, offset, characters, records, ct);
    }

    private BrowserContentResult ObservationManifest(ObservationSnapshot snapshot, int? characters, int? records, CancellationToken ct)
    {
        var limit = ObservationLimit(characters);
        var maximum = Math.Min(_settings.MaxObservationPages, 100);
        if (maximum < 1 || limit < 1024 || Math.Min(records ?? 200, _settings.MaxObservationRecords) < 1)
            throw new InvalidOperationException("Observation limits require at least 1024 characters, one record and one page.");
        var descriptors = new List<BrowserObservationPage>();
        var offset = 0;
        BrowserContentResult Manifest() => snapshot.Result with
        {
            MaxCharacters = limit, Truncated = snapshot.Capture.Truncated || offset < snapshot.Capture.Records.Count,
            ObservationManifest = new(snapshot.Id, descriptors.ToArray(), snapshot.Capture.Records.Count,
                snapshot.Capture.Truncated, offset < snapshot.Capture.Records.Count)
        };
        while (offset < snapshot.Capture.Records.Count && descriptors.Count < maximum)
        {
            ct.ThrowIfCancellationRequested();
            var page = ObservationPage(snapshot, offset, characters, records, ct);
            var count = page.Observation!.Records.Count;
            descriptors.Add(new(snapshot.Id + ":page:" + descriptors.Count.ToString(CultureInfo.InvariantCulture), count));
            offset += count;
            if (JsonSerializer.Serialize(Manifest(), BrowserMcpJsonContext.Default.BrowserContentResult).Length > limit)
            {
                descriptors.RemoveAt(descriptors.Count - 1); offset -= count; break;
            }
            snapshot.Pages.Add(page);
        }
        var result = Manifest();
        for (var i = 0; i < snapshot.Pages.Count; i++)
            snapshot.Pages[i] = snapshot.Pages[i] with { Observation = snapshot.Pages[i].Observation! with
                { NextCursor = i + 1 < descriptors.Count ? descriptors[i + 1].Cursor : null } };
        if (JsonSerializer.Serialize(result, BrowserMcpJsonContext.Default.BrowserContentResult).Length > limit)
            throw new InvalidOperationException("The observation manifest exceeds the response allowance. Narrow the selector.");
        return result;
    }

    private int ObservationLimit(int? characters) => Math.Min(characters ?? 24_000, Math.Min(_settings.MaxObservationCharacters, 24_000));

    private BrowserContentResult ObservationPage(ObservationSnapshot snapshot, int offset, int? characters, int? records, CancellationToken ct)
    {
        var limit = ObservationLimit(characters);
        var count = Math.Min(records ?? 200, Math.Min(_settings.MaxObservationRecords, 200));
        if (limit < 1024 || count < 1) throw new InvalidOperationException("Observation limits require at least 1024 characters and one record.");
        var selected = new List<BrowserObservationRecord>();
        BrowserContentResult Page() => snapshot.Result with
        {
            Content = snapshot.Paged ? "" : string.Join('\n', selected.Select(r => r.Text).Where(t => t.Length > 0)),
            MaxCharacters = limit,
            Truncated = offset + selected.Count < snapshot.Capture.Records.Count || snapshot.Capture.Truncated,
            Observation = new(snapshot.Id, selected.ToArray(), offset + selected.Count < snapshot.Capture.Records.Count
                ? snapshot.Paged ? snapshot.Id + ":page:" + (snapshot.Pages.Count + 1).ToString(CultureInfo.InvariantCulture)
                    : snapshot.Id + ":" + (offset + selected.Count).ToString(CultureInfo.InvariantCulture) : null, snapshot.Capture.Truncated)
        };
        while (offset + selected.Count < snapshot.Capture.Records.Count && selected.Count < count)
        {
            ct.ThrowIfCancellationRequested();
            selected.Add(snapshot.Capture.Records[offset + selected.Count]);
            if (JsonSerializer.Serialize(Page(), BrowserMcpJsonContext.Default.BrowserContentResult).Length <= limit) continue;
            selected.RemoveAt(selected.Count - 1);
            break;
        }
        var result = Page();
        if (selected.Count == 0 && offset < snapshot.Capture.Records.Count ||
            JsonSerializer.Serialize(result, BrowserMcpJsonContext.Default.BrowserContentResult).Length > limit)
            throw new InvalidOperationException("An observed record exceeds the response allowance. Narrow the selector or raise the requested allowance within host policy.");
        return result;
    }

    // Browser-owned deterministic observation, never a model-generated page script.
    // A capture has finite traversal/storage limits; incomplete captures remain explicitly incomplete.
    internal const string ObservationScript = """
        root => {
          const records = [], owned = 'a[href],button,[role="button"],h1,h2,h3,h4,h5,h6,input,select,textarea';
          let visited = 0, size = 0, truncated = false;
          const text = s => (s || '').replace(/\s+/g, ' ').trim();
          const selector = e => {
            const parts = [];
            const unique = s => { const matches = document.querySelectorAll(s); return matches.length === 1 && matches[0] === e; };
            for (let n = e; n && n.nodeType === 1 && parts.length < 32; n = n.parentElement) {
              if (n.id) { const candidate = ['#' + CSS.escape(n.id), ...parts].join(' > '); if (unique(candidate)) return candidate; }
              let i = 1; for (let p = n.previousElementSibling; p; p = p.previousElementSibling) if (p.tagName === n.tagName) i++;
              parts.unshift(n.tagName.toLowerCase() + ':nth-of-type(' + i + ')');
              if (unique(parts.join(' > '))) return parts.join(' > ');
            }
            return parts.join(' > ');
          };
          if (root.closest('script,style,noscript,template,svg,[hidden],[aria-hidden="true"]') || getComputedStyle(root).display === 'none' || getComputedStyle(root).visibility === 'hidden') return JSON.stringify({records, truncated});
          const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
            acceptNode: e => {
              if (++visited > 20000) { truncated = true; return NodeFilter.FILTER_REJECT; }
              if (e.matches('script,style,noscript,template,svg') || e.hidden || e.getAttribute('aria-hidden') === 'true') return NodeFilter.FILTER_REJECT;
              const s = getComputedStyle(e);
              return s.display === 'none' || s.visibility === 'hidden' ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
            }
          });
          let element = root;
          while (element && !truncated) {
            const tag = element.tagName.toLowerCase(), owner = element.closest(owned);
            if (!owner || owner === element) {
              const kind = element.matches('a[href]') ? 'link' : /^h[1-6]$/.test(tag) ? 'heading' : element.matches('button,[role="button"],input,select,textarea') ? 'control' : 'text';
              const label = element.getAttribute('aria-label');
              const content = kind === 'control' ? text(label || (element.matches('button,[role="button"]') ? element.innerText : element.getAttribute('placeholder'))) :
                kind === 'text' ? text([...element.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join(' ')) : text(element.innerText);
              const href = kind === 'link' ? element.href : null;
              if (content || href || kind === 'control') {
                const group = element.closest('dialog,[role="dialog"],[role="alertdialog"],[aria-modal="true"]') || element.closest('article,li,section,tr,nav,header,main,form') || root;
                const record = { kind, tag, selector: selector(element), group: selector(group), text: content, href, role: element.getAttribute('role') };
                const length = JSON.stringify(record).length;
                if (records.length >= 10000 || size + length > 2000000) { truncated = true; break; }
                records.push(record); size += length;
              }
            }
            element = walker.nextNode();
          }
          return JSON.stringify({records, truncated});
        }
        """;
}
