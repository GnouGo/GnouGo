using System.Globalization;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserObservationRecord(string Kind, string Tag, string Selector, string Group, string Text, string? Href, string? Role)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("Nearest semantic container's tag, explicit role and snapshot-local index. Nested semantic items may have a different outer occurrence.")]
    public BrowserObservationGroup? GroupContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("Outer semantic item below the nearest landmark/dialog, with its DOM selector, tag, role and snapshot-local index. Nested variant links retain this same outer item; distinct items remain distinct even when their destination URLs match. This is DOM provenance, not a business classification.")]
    public BrowserObservationOccurrence? Occurrence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("DOM selector of an enclosing heading or a heading contained in this link. Preserves its heading association separately from other links in the item.")]
    public string? Heading { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Description("Optional host-requested link attributes from the nearest ancestor within the selected root, including the link itself. A null value means no matching ancestor; an empty attribute remains an explicit empty string. Tag, role and snapshot-local index identify the actual source element; repeated values on different elements retain different indexes. This is observed DOM evidence, not a business classification.")]
    public IReadOnlyDictionary<string, BrowserObservationAncestorAttribute?>? AncestorAttributes { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reference { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Actions { get; init; }
}
public sealed record BrowserObservationAncestorAttribute(string Value, string Tag, string? Role, int Index);
public sealed record BrowserObservationGroup(string Tag, string? Role, int Index);
public sealed record BrowserObservationOccurrence(string Selector, string Tag, string? Role, int Index);
public sealed record BrowserObservation(string Id, IReadOnlyList<BrowserObservationRecord> Records, string? NextCursor, bool CaptureTruncated);
public sealed record BrowserObservationPage(string Cursor, int RecordCount);
public sealed record BrowserObservationManifest(string Id, [property: Description("Frozen page descriptors, at most 100 or the stricter host/response allowance. ManifestTruncated identifies an incomplete list.")] IReadOnlyList<BrowserObservationPage> Pages,
    int RecordCount, bool CaptureTruncated, bool ManifestTruncated);
public sealed record BrowserObservationSnapshot(string Id, IReadOnlyList<BrowserObservation> Pages, int RecordCount,
    bool CaptureTruncated, bool ManifestTruncated);
public sealed record BrowserSnapshotInvalidation(string SnapshotId, long Generation, string Reason, DateTimeOffset AtUtc);
public sealed record BrowserSnapshotNavigation(long Generation, string Url, string? Title, string? Method, int? StatusCode);
public sealed record BrowserNavigationResponse(string Url, string Method, int StatusCode);
public sealed record BrowserSnapshotRecovery(string SnapshotId, long Generation, string Reason, bool ReloadRequested, BrowserSnapshotNavigation Navigation);
public sealed record BrowserSnapshotAcquisition(int Attempts, IReadOnlyList<BrowserSnapshotInvalidation> Invalidations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserSnapshotNavigation? Navigation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserNavigationResponse? LastResponse { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BrowserSnapshotRecovery>? Recoveries { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ElapsedMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, long>? TimingsMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureCategory { get; init; }
}
internal sealed class BrowserObservationException(string code, string message, BrowserSnapshotAcquisition acquisition) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
    internal BrowserSnapshotAcquisition Acquisition { get; } = acquisition;
}
internal sealed record BrowserObservationCapture(List<BrowserObservationRecord> Records, bool Truncated, bool DocumentEmpty = false);

public sealed partial class PlaywrightBrowserHost
{
    // Internal deterministic fault injection at acquisition boundaries; never exposed through MCP.
    internal Func<IPage, string, CancellationToken, Task>? ObservationCheckpoint { get; init; }
    private readonly object _observationLock = new();
    private readonly Dictionary<string, BrowserSnapshotInvalidation> _expiredObservations = new(StringComparer.Ordinal);
    private long _observationGeneration;
    private ObservationStamp? _capturingObservation;
    private ObservationSnapshot? _observation;
    private sealed record ObservationStamp(string Id, long Generation)
    {
        internal BrowserSnapshotInvalidation? Invalidation { get; set; }
    }

    private ObservationStamp BeginObservation()
    {
        lock (_observationLock)
        {
            InvalidateObservation("replacement");
            return _capturingObservation = new(Guid.NewGuid().ToString("N"), _observationGeneration);
        }
    }

    private void InvalidateObservation(string reason)
    {
        lock (_observationLock)
        {
            if (reason == "closure") _observedNavigation.Clear("closure");
            if (_observation is { } snapshot) { RememberInvalidation(snapshot.Stamp, reason); _retiredObservationHandles.Add(snapshot.Handle); }
            if (_capturingObservation is { } capture) RememberInvalidation(capture, reason);
            _observation = null;
            _capturingObservation = null;
            _observationGeneration++;
        }
    }

    private void RememberInvalidation(ObservationStamp stamp, string reason)
    {
        var invalidation = new BrowserSnapshotInvalidation(stamp.Id, stamp.Generation, reason, DateTimeOffset.UtcNow);
        stamp.Invalidation ??= invalidation;
        if (!_expiredObservations.TryAdd(stamp.Id, stamp.Invalidation)) return;
        // Retain reasons, never expired payloads. Unknown/evicted identities stay invalid.
        if (_expiredObservations.Count > 64) _expiredObservations.Remove(_expiredObservations.Keys.First());
        _logger.LogInformation("Browser snapshot {SnapshotId}, generation {Generation}, invalidated: {Reason}", stamp.Id, stamp.Generation, reason);
        TraceInvalidation(invalidation);
    }

    private static void TraceInvalidation(BrowserSnapshotInvalidation invalidation) => Activity.Current?.AddEvent(new ActivityEvent(
        "browser.snapshot.invalidated", invalidation.AtUtc, new ActivityTagsCollection
        {
            ["browser.snapshot.id"] = invalidation.SnapshotId, ["browser.snapshot.generation"] = invalidation.Generation,
            ["browser.snapshot.reason"] = invalidation.Reason
        }));

    private BrowserObservationException Expired(BrowserSnapshotInvalidation invalidation)
    {
        TraceInvalidation(invalidation);
        return new("SNAPSHOT_EXPIRED", "The observation snapshot expired: " + invalidation.Reason + ". Discard all its pages and acquire a fresh complete snapshot.", new(1, [invalidation]));
    }

    private void EnsureCurrent(ObservationStamp stamp)
    {
        lock (_observationLock)
        {
            if (stamp.Generation == _observationGeneration) return;
            throw Expired(stamp.Invalidation ?? new(stamp.Id, stamp.Generation, "generation_changed", DateTimeOffset.UtcNow));
        }
    }
    private sealed record ObservationSnapshot(ObservationStamp Stamp, BrowserContentResult Result, BrowserObservationCapture Capture, IJSHandle Handle, bool Paged = false)
    {
        internal string Id => Stamp.Id;
        internal List<BrowserContentResult> Pages { get; } = [];
        internal HashSet<string> DeliveredReferences { get; } = new(StringComparer.Ordinal);
        internal bool Complete { get; set; }
        internal BrowserContentResult Deliver(BrowserContentResult result)
        {
            foreach (var record in result.Observation?.Records ?? [])
                if (record.Reference is { } reference) DeliveredReferences.Add(reference);
            return result;
        }
    }

    private async Task<BrowserContentResult> CaptureObservationAsync(IPage page, ILocator locator, ContentLocatorResolution resolution,
        string? selector, int? status, int? characters, int? records, CancellationToken ct, bool paged = false, ObservationStamp? existingStamp = null,
        Action<string>? setPhase = null)
    {
        ct.ThrowIfCancellationRequested();
        var stamp = existingStamp ?? BeginObservation();
        IJSHandle? handle = null;
        try
        {
            if (ObservationCheckpoint is { } capturing) await capturing(page, "capture", ct).WaitAsync(ct);
            EnsureCurrent(stamp);
            handle = await locator.EvaluateHandleAsync(ObservationScript, new[] { stamp.Id }.Concat(_observationAncestorAttributes).ToArray()).WaitAsync(ct);
            var json = await handle.EvaluateAsync<string>("capture => capture.json").WaitAsync(ct);
            EnsureCurrent(stamp);
            var capture = JsonSerializer.Deserialize(json, BrowserMcpJsonContext.Default.BrowserObservationCapture)
                ?? throw new InvalidOperationException("The browser returned an invalid observation.");
            var title = await page.TitleAsync().WaitAsync(ct);
            EnsureCurrent(stamp);
            var result = new BrowserContentResult(page.Url, title, status, selector,
                resolution.ResolvedSelector, resolution.FallbackApplied, resolution.FallbackReason, paged ? "observation_pages" : "observation", "", false, 0);
            var snapshot = new ObservationSnapshot(stamp, result, capture, handle, paged);
            setPhase?.Invoke("pagination");
            var response = paged ? ObservationManifest(snapshot, characters, records, ct) : ObservationPage(snapshot, 0, characters, records, ct);
            lock (_observationLock)
            {
                EnsureCurrent(stamp);
                _observation = snapshot;
                handle = null; // Ownership moves to the current snapshot.
                return snapshot.Deliver(response);
            }
        }
        catch (PlaywrightException)
        {
            EnsureCurrent(stamp);
            throw;
        }
        finally
        {
            lock (_observationLock)
            {
                if (_capturingObservation == stamp) _capturingObservation = null;
                if (handle is not null) _retiredObservationHandles.Add(handle);
            }
        }
    }

    private async Task<BrowserContentResult> AcquireCompleteObservationAsync(string? url, string waitUntil, int? timeoutMs,
        string? selector, int? characters, int? records, CancellationToken ct, long navigationAuthorityGeneration)
    {
        var invalidations = new List<BrowserSnapshotInvalidation>();
        var attempt = 0;
        var recoveries = new List<BrowserSnapshotRecovery>();
        var timer = Stopwatch.StartNew();
        var phase = "startup";
        long phaseStarted = 0;
        var timings = new Dictionary<string, long>(StringComparer.Ordinal);
        void SetPhase(string next)
        {
            var elapsed = timer.ElapsedMilliseconds;
            timings[phase] = timings.GetValueOrDefault(phase) + elapsed - phaseStarted;
            phase = next;
            phaseStarted = elapsed;
            Activity.Current?.AddEvent(new ActivityEvent("browser.snapshot.phase", tags: new ActivityTagsCollection
            {
                ["browser.snapshot.phase"] = phase, ["browser.snapshot.attempt"] = attempt,
                ["browser.snapshot.elapsed_ms"] = elapsed
            }));
        }
        IPage? page = null;
        IRequest? pendingRequest = null, committedRequest = null;
        int? pendingStatus = null;
        BrowserSnapshotNavigation? navigation = null;
        BrowserNavigationResponse? lastResponse = null;
        var unsafeNavigation = false;
        var reloaded = false;
        IResponse? initialResponse = null;
        // Subscribe before navigating. A response belongs only to the request that
        // committed the current main document, never to a later same-URL reload.
        void Requested(object? sender, IRequest request)
        {
            if (!request.IsNavigationRequest || request.Frame != page!.MainFrame) return;
            lock (_observationLock)
            {
                pendingRequest = request; pendingStatus = null;
                unsafeNavigation |= request.Method != "GET";
            }
        }
        void Responded(object? sender, IResponse response)
        {
            if (!response.Request.IsNavigationRequest || response.Request.Frame != page!.MainFrame) return;
            lock (_observationLock)
            {
                lastResponse = new(response.Url, response.Request.Method, response.Status);
                if (response.Request == pendingRequest) pendingStatus = response.Status;
                if (response.Request == committedRequest && navigation is not null)
                    navigation = navigation with { StatusCode = response.Status };
            }
        }
        void Failed(object? sender, IRequest request)
        {
            lock (_observationLock)
                if (pendingRequest == request) { pendingRequest = null; pendingStatus = null; }
        }
        void Navigated(object? sender, IFrame frame)
        {
            if (frame != page!.MainFrame) return;
            lock (_observationLock)
            {
                if (pendingRequest is null && committedRequest is not null && navigation is not null &&
                    _committedMainNavigation == committedRequest && NavigationEvidenceFor(page)?.FinalUrl == committedRequest.Url)
                {
                    // A verified history-only address change keeps this document's
                    // actual method/status, independently of its current address.
                    navigation = navigation with { Generation = _observationGeneration, Url = frame.Url };
                }
                else
                {
                    committedRequest = pendingRequest?.Url == frame.Url ? pendingRequest : null;
                    navigation = new(_observationGeneration, frame.Url, null, committedRequest?.Method,
                        committedRequest is null ? null : pendingStatus);
                }
                pendingRequest = null; pendingStatus = null;
            }
        }
        BrowserSnapshotAcquisition Acquisition()
        {
            lock (_observationLock)
            {
                var elapsed = timer.ElapsedMilliseconds;
                var measured = new Dictionary<string, long>(timings, StringComparer.Ordinal);
                measured[phase] = measured.GetValueOrDefault(phase) + elapsed - phaseStarted;
                return new(attempt, invalidations.ToArray())
                {
                    Navigation = navigation, LastResponse = lastResponse,
                    Recoveries = recoveries.Count == 0 ? null : recoveries.ToArray(),
                    Phase = phase, ElapsedMilliseconds = elapsed, TimingsMilliseconds = measured
                };
            }
        }
        if (ObservationLimit(characters) < 1024 || Math.Min(records ?? 200, _settings.MaxObservationRecords) < 1 || _settings.MaxObservationPages < 1)
            throw new BrowserObservationException("INVALID_INPUT", "Observation limits require at least 1024 characters, one record and one page.", new(0, []));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = NormalizeTimeout(timeoutMs, url is null ? _settings.DefaultTimeoutMs : _settings.NavigationTimeoutMs);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeout));
        float Remaining() => Math.Max(1, timeout - (float)timer.Elapsed.TotalMilliseconds);
        var token = deadline.Token;
        try
        {
            var target = url is null ? null : _observedNavigation.Validate(url, _settings, true);
            page = target is null ? GetRequiredPage() : await EnsurePageAsync().WaitAsync(token);
            page.Request += Requested;
            page.Response += Responded;
            page.RequestFailed += Failed;
            page.FrameNavigated += Navigated;
            using var readiness = ObserveMainFrameNavigation(page);
            if (target is not null)
            {
                InvalidateObservation("navigation");
                SetPhase("navigation");
                initialResponse = await NavigateReadAsync(page, target.AbsoluteUri, ParseWaitUntil(waitUntil), Remaining(), token).WaitAsync(token);
            }
            // Navigation invalidation and one eligible empty-document reload share
            // the original deadline and three captures. No interaction is replayed.
            for (attempt = 1; attempt <= 3; attempt++)
            {
                var stamp = BeginObservation();
                var published = false;
                try
                {
                    SetPhase("readiness");
                    await readiness.WaitForLoadStateAsync(ParseLoadState(waitUntil), Remaining(), token);
                    EnsureCurrent(stamp);
                    _observedNavigation.Validate(page.Url, _settings, true);
                    // Complete acquisition must not silently broaden a requested selector.
                    var requested = string.IsNullOrWhiteSpace(selector) ? "body" : selector.Trim();
                    SetPhase("selector");
                    var locator = await ResolveLocatorAsync(page, requested, Remaining()).WaitAsync(token);
                    EnsureCurrent(stamp);
                    var resolution = new ContentLocatorResolution(locator, requested, false, null);
                    SetPhase("capture");
                    var manifestResult = await CaptureObservationAsync(page, locator, resolution, selector, null, characters, records, token, true, stamp, SetPhase);
                    lock (_observationLock)
                    {
                        EnsureCurrent(stamp);
                        navigation = navigation is not null && navigation.Url == manifestResult.Url
                            ? navigation with { Title = manifestResult.Title }
                            : new(stamp.Generation, manifestResult.Url, manifestResult.Title, null, null);
                        manifestResult = manifestResult with { StatusCode = navigation.StatusCode };
                    }
                    var manifest = manifestResult.ObservationManifest!;
                    if (manifest.CaptureTruncated || manifest.ManifestTruncated)
                        throw new BrowserObservationException("OBSERVATION_INCOMPLETE", "A complete snapshot exceeds capture or page bounds. Narrow the requested observation explicitly.", Acquisition());
                    ObservationSnapshot snapshot;
                    lock (_observationLock) { EnsureCurrent(stamp); snapshot = _observation!; }
                    if (manifest.RecordCount == 0 && snapshot.Capture.DocumentEmpty && string.IsNullOrWhiteSpace(manifestResult.Title))
                    {
                        bool reload;
                        lock (_observationLock)
                        {
                            EnsureCurrent(stamp);
                            reload = !reloaded && attempt < 3 && initialResponse is not null && !unsafeNavigation &&
                                committedRequest == initialResponse.Request && navigation!.Method == "GET" &&
                                navigation.StatusCode is >= 200 and < 300 and not (204 or 205);
                            recoveries.Add(new(snapshot.Id, stamp.Generation, "empty_document", reload, navigation!));
                            InvalidateObservation("empty_document");
                            invalidations.Add(stamp.Invalidation!);
                        }
                        Activity.Current?.AddEvent(new ActivityEvent("browser.snapshot.empty", tags: new ActivityTagsCollection
                        {
                            ["browser.snapshot.id"] = snapshot.Id, ["browser.snapshot.attempt"] = attempt,
                            ["browser.snapshot.reason"] = "empty_document", ["browser.snapshot.reload"] = reload,
                            ["http.response.status_code"] = navigation?.StatusCode
                        }));
                        _logger.LogInformation("Empty Browser snapshot {SnapshotId}, attempt {Attempt}, reload {Reload}", snapshot.Id, attempt, reload);
                        if (!reload)
                            throw new BrowserObservationException("OBSERVATION_EMPTY", "The document is empty or unusable. No business absence or blocker can be established from this acquisition.", Acquisition());
                        reloaded = true;
                        _observedNavigation.Validate(page.Url, _settings, true);
                        SetPhase("reload");
                        await NavigateReadAsync(page, page.Url, ParseWaitUntil(waitUntil), Remaining(), token).WaitAsync(token);
                        continue;
                    }
                    var pages = snapshot.Pages.Select(p => p.Observation! with { NextCursor = null }).ToArray();
                    SetPhase("publication");
                    var result = manifestResult with
                    {
                        Format = "observation_complete", ObservationManifest = null, Truncated = false,
                        ObservationSnapshot = new(snapshot.Id, pages, manifest.RecordCount, false, false),
                        Acquisition = Acquisition(), NavigationEvidence = NavigationEvidenceFor(page)
                    };
                    var aggregateLimit = checked(ObservationLimit(characters) * Math.Min(_settings.MaxObservationPages, 100));
                    if (ObservationCheckpoint is { } publishing) await publishing(page, "publish", token).WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    result = result with { Acquisition = Acquisition() };
                    if (JsonSerializer.Serialize(result, BrowserMcpJsonContext.Default.BrowserContentResult).Length > aggregateLimit)
                        throw new BrowserObservationException("OBSERVATION_INCOMPLETE", "The complete snapshot exceeds the aggregate page allowance. Narrow the requested observation explicitly.", result.Acquisition);
                    token.ThrowIfCancellationRequested();
                    lock (_observationLock)
                    {
                        EnsureCurrent(stamp);
                        // Retain action identity, but complete acquisitions issue no cursors.
                        snapshot.Complete = true;
                        foreach (var pageResult in snapshot.Pages) snapshot.Deliver(pageResult);
                        published = true;
                        _observedNavigation.Publish(result, selector, navigationAuthorityGeneration);
                        Activity.Current?.SetTag("browser.snapshot.attempts", attempt);
                        Activity.Current?.SetTag("browser.snapshot.id", snapshot.Id);
                        Activity.Current?.SetTag("browser.snapshot.pages", pages.Length);
                        return result;
                    }
                }
                catch (PlaywrightException) when (stamp.Invalidation is { Reason: not "empty_document" })
                {
                    var failure = Expired(stamp.Invalidation);
                    invalidations.AddRange(failure.Acquisition.Invalidations);
                    if (stamp.Invalidation.Reason != "navigation" || attempt == 3)
                        throw new BrowserObservationException(failure.Code, failure.Message, Acquisition());
                }
                catch (BrowserObservationException ex) when (ex.Code == "SNAPSHOT_EXPIRED")
                {
                    invalidations.AddRange(ex.Acquisition.Invalidations);
                    if (ex.Acquisition.Invalidations.Any(i => i.Reason != "navigation") || attempt == 3)
                        throw new BrowserObservationException(ex.Code, ex.Message, Acquisition());
                }
                finally
                {
                    lock (_observationLock)
                    {
                        // A deadline or cancellation can win before the expiration catch.
                        // Retain its known cause without retrying or changing the terminal error.
                        if (stamp.Invalidation is { } invalidation && !invalidations.Any(i => i.SnapshotId == stamp.Id))
                            invalidations.Add(invalidation);
                        if (_capturingObservation == stamp) _capturingObservation = null;
                        if (!published && _observation?.Stamp == stamp)
                        {
                            _retiredObservationHandles.Add(_observation.Handle);
                            _observation = null;
                        }
                    }
                }
            }
            throw new InvalidOperationException("Snapshot acquisition exhausted its attempts.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new BrowserObservationException("TIMEOUT", $"Complete snapshot acquisition exceeded its shared timeout during {phase}.", Acquisition());
        }
        catch (TimeoutException)
        {
            throw new BrowserObservationException("TIMEOUT", $"Complete snapshot acquisition exceeded its shared timeout during {phase}.", Acquisition());
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Preserve the public host's cancellation semantics; the tool boundary
            // can still retain the bounded acquisition evidence in its error receipt.
            ex.Data[nameof(BrowserSnapshotAcquisition)] = Acquisition();
            throw;
        }
        catch (PlaywrightException error)
        {
            var category = error.Message.Contains("Execution context was destroyed", StringComparison.Ordinal) ? "document_context_destroyed"
                : error.Message.Contains("interrupted by another navigation", StringComparison.Ordinal) ? "navigation_interrupted"
                : error.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ? "target_closed"
                : System.Text.RegularExpressions.Regex.Match(error.Message, @"net::ERR_[A-Z_]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)) is { Success: true } network ? network.Value : "playwright_error";
            var failure = new BrowserObservationException("BROWSER_ERROR", "The Browser could not complete snapshot acquisition.", Acquisition() with { FailureCategory = category });
            if (error.Data[nameof(BrowserNavigationEvidence)] is BrowserNavigationEvidence partialNavigation)
                failure.Data[nameof(BrowserNavigationEvidence)] = partialNavigation;
            throw failure;
        }
        catch (BrowserNavigationException ex)
        {
            ex.Data[nameof(BrowserSnapshotAcquisition)] = Acquisition();
            throw;
        }
        catch (InvalidOperationException ex) when (ex is not BrowserObservationException)
        {
            throw new BrowserObservationException("INVALID_INPUT", ex.Message, Acquisition());
        }
        finally
        {
            if (page is not null)
            {
                page.Request -= Requested;
                page.Response -= Responded;
                page.FrameNavigated -= Navigated;
                page.RequestFailed -= Failed;
            }
        }
    }

    private BrowserContentResult ContinueObservation(string cursor, int? characters, int? records, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parts = cursor.Split(':');
        var paged = parts.Length == 3 && parts[1] == "page";
        if ((!paged && parts.Length != 2) || !Guid.TryParseExact(parts[0], "N", out _) ||
            !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            throw new InvalidOperationException("The observation cursor is malformed.");
        lock (_observationLock)
        {
            if (_expiredObservations.TryGetValue(parts[0], out var expired)) throw Expired(expired);
            var snapshot = _observation;
            if (snapshot is null || snapshot.Complete || parts[0] != snapshot.Id || paged != snapshot.Paged)
                throw new InvalidOperationException("The observation cursor is unknown in this Browser session.");
            if (GetRequiredPage().Url != snapshot.Result.Url)
            {
                InvalidateObservation("navigation");
                throw Expired(_expiredObservations[snapshot.Id]);
            }
            if (paged)
            {
                if (characters is not null || records is not null || index >= snapshot.Pages.Count)
                    throw new InvalidOperationException("The observation page cursor is invalid. Page limits are frozen; omit maxCharacters and maxRecords.");
                return snapshot.Deliver(snapshot.Pages[index]);
            }
            if (index >= snapshot.Capture.Records.Count) throw new InvalidOperationException("The observation offset is out of range.");
            return snapshot.Deliver(ObservationPage(snapshot, index, characters, records, ct));
        }
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
    internal static readonly string ObservationScript = """
        (root, observation) => {
          const [snapshotId, ...attributeNames] = observation, attributeContexts = new Map();
          const records = [], elements = [], states = [], groups = new Map(), occurrences = new Map(), actionState = __ACTION_STATE__, owned = 'a[href],button,[role="button"],[role="checkbox"],[role="radio"],[role="switch"],[role="tab"],[role="menuitem"],[role="menuitemcheckbox"],[role="menuitemradio"],[contenteditable="true"],h1,h2,h3,h4,h5,h6,[role="heading"],input,select,textarea';
          let visited = 0, size = 0, truncated = false;
          const text = s => (s || '').replace(/\s+/g, ' ').trim();
          const documentEmpty = () => {
            if (records.length || truncated || text(document.title) || !document.body) return false;
            let count = 0, n = document.body.firstChild;
            while (n) {
              if (++count > 20000) return false; // unknown, never evidence of emptiness
              const element = n.nodeType === 1;
              const style = element ? getComputedStyle(n) : null;
              const skip = element && (n.matches('script,style,noscript,template') || n.hidden ||
                n.getAttribute('aria-hidden') === 'true' || style.display === 'none' || style.visibility === 'hidden');
              if (!skip) {
                if (n.nodeType === 3 && text(n.textContent).length > 0 || element &&
                    (n.matches('img,svg,canvas,video,audio,iframe,object,embed,input,select,button,textarea,[contenteditable="true"],[role]') ||
                     style.backgroundImage !== 'none')) return false;
                if (n.firstChild) { n = n.firstChild; continue; }
              }
              while (n && n !== document.body && !n.nextSibling) n = n.parentNode;
              if (!n || n === document.body) break;
              n = n.nextSibling;
            }
            return true;
          };
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
          if (root.closest('script,style,noscript,template,svg,[hidden],[aria-hidden="true"]') || getComputedStyle(root).display === 'none' || getComputedStyle(root).visibility === 'hidden') return {json: JSON.stringify({records, truncated, documentEmpty: documentEmpty()}), elements, states};
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
              const kind = element.matches('button,[role="button"],[role="checkbox"],[role="radio"],[role="switch"],[role="tab"],[role="menuitem"],[role="menuitemcheckbox"],[role="menuitemradio"],input,select,textarea,[contenteditable="true"]') ? 'control' : element.matches('a[href]') ? 'link' : /^h[1-6]$/.test(tag) || element.getAttribute('role') === 'heading' ? 'heading' : 'text';
              const label = element.getAttribute('aria-label');
              const content = kind === 'control' ? text(label || (element.matches('button,[role="button"]') ? element.innerText : element.getAttribute('placeholder'))) :
                kind === 'text' ? text([...element.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join(' ')) : text(element.innerText) || text(label);
              const href = kind === 'link' ? element.href : null;
              if (content || href || kind === 'control') {
                const group = element.closest('dialog,[role="dialog"],[role="alertdialog"],[aria-modal="true"]') || element.closest('article,li,tr,[role="listitem"],[role="article"],[role="row"]') || element.closest('section,nav,header,main,form,[role="region"],[role="navigation"],[role="main"],[role="form"]') || root;
                if (!groups.has(group)) groups.set(group, { tag: group.tagName.toLowerCase(), role: group.getAttribute('role'), index: groups.size });
                const itemSelector = 'article,li,tr,[role="listitem"],[role="article"],[role="row"]';
                const boundary = element.closest('dialog,[role="dialog"],[role="alertdialog"],section,nav,header,main,form,[role="region"],[role="navigation"],[role="main"],[role="form"]') || root;
                let occurrence = element.closest(itemSelector);
                if (occurrence && !boundary.contains(occurrence)) occurrence = null;
                if (occurrence) {
                  let parent = occurrence.parentElement?.closest(itemSelector);
                  while (parent && boundary.contains(parent)) { occurrence = parent; parent = parent.parentElement?.closest(itemSelector); }
                  if (!occurrences.has(occurrence)) occurrences.set(occurrence, {selector: selector(occurrence), tag: occurrence.tagName.toLowerCase(), role: occurrence.getAttribute('role'), index: occurrences.size});
                }
                const heading = element.closest('h1,h2,h3,h4,h5,h6,[role="heading"]') || (kind === 'link' ? element.querySelector('h1,h2,h3,h4,h5,h6,[role="heading"]') : null);
                const state = actionState(element);
                const record = { kind, tag, selector: selector(element), group: selector(group), groupContext: groups.get(group), occurrence: occurrence ? occurrences.get(occurrence) : null, heading: heading ? selector(heading) : null, text: content, href, role: element.getAttribute('role'), reference: snapshotId + ':record:' + records.length, actions: state.actions };
                if (kind === 'link' && attributeNames.length) {
                  const attributes = Object.create(null);
                  for (const name of attributeNames) {
                    let ancestor = element, depth = 0;
                    while (ancestor && root.contains(ancestor) && !ancestor.hasAttribute(name)) {
                      if (++depth > 64) { truncated = true; break; }
                      if (ancestor === root) { ancestor = null; break; }
                      ancestor = ancestor.parentElement;
                    }
                    if (truncated) break;
                    if (!ancestor || !root.contains(ancestor)) attributes[name] = null;
                    else {
                      const value = ancestor.getAttribute(name);
                      if (value.length > 1024) { truncated = true; break; }
                      if (!attributeContexts.has(ancestor)) attributeContexts.set(ancestor, attributeContexts.size);
                      attributes[name] = {value, tag: ancestor.tagName.toLowerCase(), role: ancestor.getAttribute('role'), index: attributeContexts.get(ancestor)};
                    }
                  }
                  if (truncated) break;
                  record.ancestorAttributes = attributes;
                }
                const length = JSON.stringify(record).length;
                if (records.length >= 10000 || size + length > 2000000) { truncated = true; break; }
                records.push(record); elements.push(element); states.push(state.signature); size += length;
              }
            }
            element = walker.nextNode();
          }
          return {json: JSON.stringify({records, truncated, documentEmpty: documentEmpty()}), elements, states};
        }
        """.Replace("__ACTION_STATE__", ObservedActionStateScript, StringComparison.Ordinal);
}
