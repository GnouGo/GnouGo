using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace GnOuGo.Browser.Mcp;

public sealed partial class PlaywrightBrowserHost : IAsyncDisposable
{
    private readonly BrowserServerSettings _settings;
    private readonly ILogger<PlaywrightBrowserHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _disposed;

    public PlaywrightBrowserHost(
        IOptions<BrowserServerSettings> settings,
        ILogger<PlaywrightBrowserHost> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task<BrowserContentResult> GetContentAsync(
        string? selector,
        string format,
        int? maxCharacters,
        CancellationToken cancellationToken)
        => GetContentAsync(
            url: null,
            waitUntil: "load",
            timeoutMs: null,
            selector: selector,
            format: format,
            maxCharacters: maxCharacters,
            includeScriptContent: false,
            cancellationToken: cancellationToken);

    public async Task<BrowserContentResult> GetContentAsync(
        string? url,
        string waitUntil,
        int? timeoutMs,
        string? selector,
        string format,
        int? maxCharacters,
        bool includeScriptContent,
        CancellationToken cancellationToken, string? cursor = null, int? maxRecords = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var contentFormat = NormalizeFormat(format);
            if (cursor is not null)
            {
                if (contentFormat is not ("observation" or "observation_pages") || url is not null || selector is not null)
                    throw new InvalidOperationException("A continuation requires an observation format and no new URL or selector.");
                return ContinueObservation(cursor, maxCharacters, maxRecords, cancellationToken);
            }
            if (contentFormat == "observation_complete")
                return await AcquireCompleteObservationAsync(url, waitUntil, timeoutMs, selector, maxCharacters, maxRecords, cancellationToken);
            var limit = maxCharacters.GetValueOrDefault(_settings.MaxContentCharacters);
            var selectorTimeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
            if (limit <= 0)
                throw new InvalidOperationException("maxCharacters must be greater than zero.");

            int? statusCode = null;
            IPage page;
            if (url is null)
            {
                page = GetRequiredPage();
            }
            else
            {
                InvalidateObservation("navigation");
                var targetUri = BrowserNavigationPolicy.ValidateNavigationTarget(url, _settings);
                page = await EnsurePageAsync();
                IResponse? response = null;
                try
                {
                    response = await page.GotoAsync(targetUri.ToString(), new PageGotoOptions
                    {
                        Timeout = NormalizeTimeout(timeoutMs, _settings.NavigationTimeoutMs),
                        WaitUntil = ParseWaitUntil(waitUntil)
                    });
                }
                catch (TimeoutException ex) when (string.Equals(waitUntil, "networkidle", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        ex,
                        "Navigation to {Url} did not reach networkidle before timeout; continuing with loaded DOM content.",
                        targetUri);

                    try
                    {
                        await page.WaitForLoadStateAsync(
                            LoadState.DOMContentLoaded,
                            new PageWaitForLoadStateOptions { Timeout = Math.Min(5_000, NormalizeTimeout(timeoutMs, _settings.NavigationTimeoutMs)) });
                    }
                    catch (Exception waitEx) when (waitEx is TimeoutException or PlaywrightException)
                    {
                        _logger.LogDebug(waitEx, "DOM content fallback wait also timed out for {Url}; continuing with current page state.", targetUri);
                    }
                }
                statusCode = response?.Status;
            }

            var locatorResolution = await ResolveContentLocatorAsync(page, selector, selectorTimeout);
            var locator = locatorResolution.Locator;
            if (contentFormat is "observation" or "observation_pages")
                return await CaptureObservationAsync(page, locator, locatorResolution, selector, statusCode, maxCharacters, maxRecords, cancellationToken, contentFormat == "observation_pages");
            var rawContent = contentFormat switch
            {
                "html" => await locator.EvaluateAsync<string>("element => element.outerHTML"),
                _ => await locator.InnerTextAsync()
            };

            var normalized = contentFormat == "text"
                ? NormalizeText(rawContent)
                : includeScriptContent ? rawContent : BrowserDomHeuristics.RemoveScriptElements(rawContent);

            var truncated = normalized.Length > limit;
            var content = truncated ? normalized[..limit] : normalized;

            return new BrowserContentResult(
                Url: page.Url,
                Title: await page.TitleAsync(),
                StatusCode: statusCode,
                Selector: selector,
                ResolvedSelector: locatorResolution.ResolvedSelector,
                FallbackApplied: locatorResolution.FallbackApplied,
                FallbackReason: locatorResolution.FallbackReason,
                Format: contentFormat,
                Content: content,
                Truncated: truncated,
                MaxCharacters: limit);
        }
        finally
        {
            await ReleaseBrowserGateAsync();
        }
    }

    public Task<BrowserActionResult> ClickAsync(
        string selector,
        string waitUntil,
        int? timeoutMs,
        CancellationToken cancellationToken) => ClickTargetAsync(selector, waitUntil, timeoutMs, cancellationToken, null, null);

    public async Task<BrowserActionResult> ClickTargetAsync(
        string? selector,
        string waitUntil,
        int? timeoutMs,
        CancellationToken cancellationToken, string? reference, string? requestedAction)
    {
        await _gate.WaitAsync(cancellationToken);
        IElementHandle? observed = null;
        try
        {
            var page = reference is null ? GetRequiredPage() : null;
            var timeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
            if (reference is not null && requestedAction is not ("activate" or "follow") || reference is null && requestedAction is not null)
                throw new BrowserActionReferenceException("ACTION_MISMATCH", "Reference-based clicks require activate or follow; legacy selector calls omit requestedAction.");
            ILocator? locator = null;
            if (reference is null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(selector);
                locator = await ResolveLocatorAsync(page!, selector, timeout);
            }
            else
            {
                var target = await ResolveObservedActionAsync(selector, reference, requestedAction!, cancellationToken);
                observed = target.Element; selector = target.Record.Selector;
            }
            page ??= GetRequiredPage();
            cancellationToken.ThrowIfCancellationRequested();
            InvalidateObservation("interaction");
            var submitted = observed is null ? await DetermineSubmittedAsync(locator!) : await DetermineSubmittedAsync(observed);
            var beforeUrl = page.Url;
            var navigationObservation = ObserveMainFrameNavigation(page);

            try
            {
                if (observed is null) await locator!.ClickAsync(new LocatorClickOptions { Timeout = timeout });
                else await observed.ClickAsync(new ElementHandleClickOptions { Timeout = timeout });
                await navigationObservation.WaitForLoadStateAsync(ParseLoadState(waitUntil), timeout, cancellationToken);
            }
            finally
            {
                navigationObservation.Dispose();
            }

            var triggeredNavigation = BrowserActionSemantics.TriggeredNavigation(
                navigationObservation.Triggered,
                beforeUrl,
                page.Url);
            var navigationType = BrowserActionSemantics.NavigationType(
                navigationObservation.Triggered,
                beforeUrl,
                page.Url);

            return new BrowserActionResult(
                Action: "click",
                Url: page.Url,
                Title: await page.TitleAsync(),
                Selector: selector!,
                Submitted: submitted,
                TriggeredNavigation: triggeredNavigation,
                NavigationType: navigationType) { Target = ObservedTarget(reference, requestedAction!) };
        }
        finally
        {
            await ReleaseBrowserGateAsync(observed);
        }
    }

    public Task<BrowserActionResult> FillAsync(
        string selector,
        string value,
        bool submit,
        int? timeoutMs,
        CancellationToken cancellationToken) => FillTargetAsync(selector, value, submit, timeoutMs, cancellationToken, null);

    public async Task<BrowserActionResult> FillTargetAsync(
        string? selector,
        string value,
        bool submit,
        int? timeoutMs,
        CancellationToken cancellationToken, string? reference)
    {
        await _gate.WaitAsync(cancellationToken);
        IElementHandle? observed = null;
        try
        {
            var page = reference is null ? GetRequiredPage() : null;
            var timeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
            ILocator? locator = null;
            if (reference is null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(selector);
                locator = await ResolveLocatorAsync(page!, selector, timeout);
            }
            else
            {
                var target = await ResolveObservedActionAsync(selector, reference, "fill", cancellationToken);
                observed = target.Element; selector = target.Record.Selector;
            }
            page ??= GetRequiredPage();
            cancellationToken.ThrowIfCancellationRequested();
            InvalidateObservation("interaction");
            var beforeUrl = page.Url;
            var triggeredNavigation = false;
            var navigationType = BrowserActionSemantics.NavigationTypeNone;

            if (observed is null) await locator!.FillAsync(value, new LocatorFillOptions { Timeout = timeout });
            else await observed.FillAsync(value, new ElementHandleFillOptions { Timeout = timeout });
            if (submit)
            {
                var navigationObservation = ObserveMainFrameNavigation(page);
                try
                {
                    if (observed is null) await locator!.PressAsync("Enter", new LocatorPressOptions { Timeout = timeout });
                    else await observed.PressAsync("Enter", new ElementHandlePressOptions { Timeout = timeout });
                    await navigationObservation.WaitForLoadStateAsync(LoadState.DOMContentLoaded, timeout, cancellationToken);
                }
                finally
                {
                    navigationObservation.Dispose();
                }

                triggeredNavigation = BrowserActionSemantics.TriggeredNavigation(
                    navigationObservation.Triggered,
                    beforeUrl,
                    page.Url);
                navigationType = BrowserActionSemantics.NavigationType(
                    navigationObservation.Triggered,
                    beforeUrl,
                    page.Url);
            }

            return new BrowserActionResult(
                Action: "fill",
                Url: page.Url,
                Title: await page.TitleAsync(),
                Selector: selector!,
                Submitted: submit,
                TriggeredNavigation: triggeredNavigation,
                NavigationType: navigationType) { Target = ObservedTarget(reference, "fill") };
        }
        finally
        {
            await ReleaseBrowserGateAsync(observed);
        }
    }

    public async Task<BrowserActionResult> ClickTextAsync(
        string text,
        bool exact,
        string waitUntil,
        int? timeoutMs,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            InvalidateObservation("interaction");
            var page = GetRequiredPage();
            var timeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
            var locator = await ResolveTextLocatorAsync(page, text, exact);
            var submitted = await DetermineSubmittedAsync(locator);
            var beforeUrl = page.Url;
            var navigationObservation = ObserveMainFrameNavigation(page);

            try
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = timeout });
                await navigationObservation.WaitForLoadStateAsync(ParseLoadState(waitUntil), timeout, cancellationToken);
            }
            finally
            {
                navigationObservation.Dispose();
            }

            var triggeredNavigation = BrowserActionSemantics.TriggeredNavigation(
                navigationObservation.Triggered,
                beforeUrl,
                page.Url);
            var navigationType = BrowserActionSemantics.NavigationType(
                navigationObservation.Triggered,
                beforeUrl,
                page.Url);

            return new BrowserActionResult(
                Action: "click_text",
                Url: page.Url,
                Title: await page.TitleAsync(),
                Selector: $"text={BrowserDomHeuristics.NormalizeWhitespace(text)}",
                Submitted: submitted,
                TriggeredNavigation: triggeredNavigation,
                NavigationType: navigationType);
        }
        finally
        {
            await ReleaseBrowserGateAsync();
        }
    }

    public Task<BrowserKeyActionResult> PressAsync(
        string selector,
        string key,
        string waitUntil,
        int? timeoutMs,
        CancellationToken cancellationToken) => PressTargetAsync(selector, key, waitUntil, timeoutMs, cancellationToken, null);

    public async Task<BrowserKeyActionResult> PressTargetAsync(
        string? selector,
        string key,
        string waitUntil,
        int? timeoutMs,
        CancellationToken cancellationToken, string? reference)
    {
        await _gate.WaitAsync(cancellationToken);
        IElementHandle? observed = null;
        try
        {
            var page = reference is null ? GetRequiredPage() : null;
            var timeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
            var normalizedKey = NormalizeRequiredValue(key, nameof(key));
            ILocator? locator = null;
            if (reference is null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(selector);
                locator = await ResolveLocatorAsync(page!, selector, timeout);
            }
            else
            {
                var target = await ResolveObservedActionAsync(selector, reference, "press", cancellationToken);
                observed = target.Element; selector = target.Record.Selector;
            }
            page ??= GetRequiredPage();
            cancellationToken.ThrowIfCancellationRequested();
            InvalidateObservation("interaction");
            var beforeUrl = page.Url;
            var navigationObservation = ObserveMainFrameNavigation(page);

            try
            {
                if (observed is null) await locator!.PressAsync(normalizedKey, new LocatorPressOptions { Timeout = timeout });
                else await observed.PressAsync(normalizedKey, new ElementHandlePressOptions { Timeout = timeout });
                await navigationObservation.WaitForLoadStateAsync(ParseLoadState(waitUntil), timeout, cancellationToken);
            }
            finally
            {
                navigationObservation.Dispose();
            }

            var triggeredNavigation = BrowserActionSemantics.TriggeredNavigation(
                navigationObservation.Triggered,
                beforeUrl,
                page.Url);
            var navigationType = BrowserActionSemantics.NavigationType(
                navigationObservation.Triggered,
                beforeUrl,
                page.Url);

            return new BrowserKeyActionResult(
                Action: "press",
                Url: page.Url,
                Title: await page.TitleAsync(),
                Selector: selector!,
                Key: normalizedKey,
                TriggeredNavigation: triggeredNavigation,
                NavigationType: navigationType) { Target = ObservedTarget(reference, "press") };
        }
        finally
        {
            await ReleaseBrowserGateAsync(observed);
        }
    }

    public Task<BrowserSelectResult> SelectAsync(
        string selector,
        string value,
        int? timeoutMs,
        CancellationToken cancellationToken) => SelectTargetAsync(selector, value, timeoutMs, cancellationToken, null);

    public async Task<BrowserSelectResult> SelectTargetAsync(
        string? selector,
        string value,
        int? timeoutMs,
        CancellationToken cancellationToken, string? reference)
    {
        await _gate.WaitAsync(cancellationToken);
        IElementHandle? observed = null;
        try
        {
            var page = reference is null ? GetRequiredPage() : null;
            var timeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
            var normalizedValue = NormalizeRequiredValue(value, nameof(value));
            ILocator? locator = null;
            if (reference is null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(selector);
                locator = await ResolveLocatorAsync(page!, selector, timeout);
            }
            else
            {
                var target = await ResolveObservedActionAsync(selector, reference, "select", cancellationToken);
                observed = target.Element; selector = target.Record.Selector;
            }
            page ??= GetRequiredPage();
            cancellationToken.ThrowIfCancellationRequested();
            InvalidateObservation("interaction");
            var selectedValues = observed is null
                ? await locator!.SelectOptionAsync(new[] { normalizedValue }, new LocatorSelectOptionOptions { Timeout = timeout })
                : await observed.SelectOptionAsync(new[] { normalizedValue }, new ElementHandleSelectOptionOptions { Timeout = timeout });

            return new BrowserSelectResult(
                Url: page.Url,
                Title: await page.TitleAsync(),
                Selector: selector!,
                SelectedValues: [.. selectedValues]) { Target = ObservedTarget(reference, "select") };
        }
        finally
        {
            await ReleaseBrowserGateAsync(observed);
        }
    }

    public async Task<BrowserWaitResult> WaitAsync(
        string? selector,
        string? state,
        int? delayMs,
        int? timeoutMs,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = GetRequiredPage();
            var normalizedSelector = string.IsNullOrWhiteSpace(selector) ? null : selector.Trim();
            var normalizedDelay = delayMs.GetValueOrDefault();

            if (normalizedSelector is null && normalizedDelay <= 0)
                throw new InvalidOperationException("Specify either a selector to wait for or a positive delayMs value.");

            var appliedState = NormalizeWaitState(state);

            if (normalizedSelector is not null)
            {
                var timeout = NormalizeTimeout(timeoutMs, _settings.DefaultTimeoutMs);
                await page.Locator(normalizedSelector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = appliedState,
                    Timeout = timeout
                });
            }

            if (normalizedDelay > 0)
                await Task.Delay(normalizedDelay, cancellationToken);

            return new BrowserWaitResult(
                Url: page.Url,
                Title: await page.TitleAsync(),
                Selector: normalizedSelector,
                State: normalizedSelector is null ? null : appliedState.ToString().ToLowerInvariant(),
                DelayMs: normalizedDelay,
                Completed: true);
        }
        finally
        {
            await ReleaseBrowserGateAsync();
        }
    }

    public async Task<BrowserScreenshotResult> ScreenshotAsync(
        bool fullPage,
        string type,
        int? quality,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = GetRequiredPage();
            var screenshotType = ParseScreenshotType(type);
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = fullPage,
                Type = screenshotType,
                Quality = screenshotType == ScreenshotType.Jpeg
                    ? NormalizeQuality(quality ?? _settings.ScreenshotQuality)
                    : null
            });

            return new BrowserScreenshotResult(
                Url: page.Url,
                Title: await page.TitleAsync(),
                MimeType: screenshotType == ScreenshotType.Jpeg ? "image/jpeg" : "image/png",
                DataBase64: Convert.ToBase64String(bytes),
                FullPage: fullPage);
        }
        finally
        {
            await ReleaseBrowserGateAsync();
        }
    }

    public async Task<BrowserCloseResult> CloseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            InvalidateObservation("closure");
            await HoldOpenIfConfiguredAsync(cancellationToken);

            if (_settings.KeepBrowserOpen)
            {
                _logger.LogWarning("Ignoring browser_close because Browser:KeepBrowserOpen is enabled.");
                return new BrowserCloseResult(false);
            }

            await CloseSessionUnlockedAsync();
            return new BrowserCloseResult(true);
        }
        finally
        {
            await ReleaseBrowserGateAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            await HoldOpenIfConfiguredAsync(CancellationToken.None);

            if (_settings.KeepBrowserOpen)
            {
                _logger.LogWarning(
                    "Skipping browser/context disposal because Browser:KeepBrowserOpen is enabled. The browser may remain visible until the process exits.");
                return;
            }

            await CloseSessionUnlockedAsync();

            if (_browser is not null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }

            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _disposed = true;
            await ReleaseBrowserGateAsync();
            _gate.Dispose();
        }
    }

    private async Task<IPage> EnsurePageAsync()
    {
        if (_page is { IsClosed: false })
            return _page;

        var context = await EnsureContextAsync();
        _page = await context.NewPageAsync();
        ConfigurePage(_page);
        return _page;
    }

    private async Task<IBrowserContext> EnsureContextAsync()
    {
        if (_context is not null)
            return _context;

        var browser = await EnsureBrowserAsync();
        _context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = false,
            IgnoreHTTPSErrors = false,
            UserAgent = string.IsNullOrWhiteSpace(_settings.UserAgent) ? null : _settings.UserAgent,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
        });

        _context.SetDefaultTimeout(_settings.DefaultTimeoutMs);
        _context.SetDefaultNavigationTimeout(_settings.NavigationTimeoutMs);
        return _context;
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is not null)
            return _browser;

        try
        {
            _playwright ??= await Playwright.CreateAsync();

            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = _settings.Headless,
                Channel = string.IsNullOrWhiteSpace(_settings.Channel) ? null : _settings.Channel,
                SlowMo = _settings.SlowMoMs > 0 ? _settings.SlowMoMs : null
            };

            _browser = _settings.BrowserName.Trim().ToLowerInvariant() switch
            {
                "firefox" => await _playwright.Firefox.LaunchAsync(launchOptions),
                "webkit" => await _playwright.Webkit.LaunchAsync(launchOptions),
                _ => await _playwright.Chromium.LaunchAsync(launchOptions)
            };

            _logger.LogInformation(
                "Playwright browser launched with engine {BrowserName} (headless={Headless}, slowMoMs={SlowMoMs}, holdOpenMs={HoldOpenMs}, keepBrowserOpen={KeepBrowserOpen})",
                _settings.BrowserName,
                _settings.Headless,
                _settings.SlowMoMs,
                _settings.HoldOpenMs,
                _settings.KeepBrowserOpen);

            return _browser;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Playwright browser binaries are missing. Build the project, then run the generated playwright install script (for example: playwright.ps1 install chromium).",
                ex);
        }
    }

    private void ConfigurePage(IPage page)
    {
        page.SetDefaultTimeout(_settings.DefaultTimeoutMs);
        page.SetDefaultNavigationTimeout(_settings.NavigationTimeoutMs);
        page.Dialog += async (_, dialog) => await dialog.DismissAsync();
        page.FrameNavigated += (_, frame) => { if (page == _page && frame == page.MainFrame) InvalidateObservation("navigation"); };
        page.Close += (_, _) => { if (page == _page) InvalidateObservation("closure"); };
    }

    private IPage GetRequiredPage()
    {
        if (_page is null || _page.IsClosed)
            throw new InvalidOperationException("No active page. Call browser_get_content with url first.");

        return _page;
    }

    private async Task<ContentLocatorResolution> ResolveContentLocatorAsync(
        IPage page,
        string? selector,
        float timeoutMs)
    {
        var requestedSelector = string.IsNullOrWhiteSpace(selector) ? "body" : selector.Trim();
        var candidates = BuildContentSelectorCandidates(requestedSelector);
        var failures = new List<string>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var attempt = await TryResolveLocatorAsync(page, candidate, timeoutMs);
            if (attempt.Locator is not null)
            {
                var usedFallback = !string.Equals(candidate, requestedSelector, StringComparison.Ordinal);
                var fallbackReason = usedFallback
                    ? $"Selector '{requestedSelector}' was not available; fallback to '{candidate}'."
                    : null;
                return new ContentLocatorResolution(attempt.Locator, candidate, usedFallback, fallbackReason);
            }

            failures.Add($"{candidate}: {attempt.FailureReason}");
        }

        var title = await TryGetPageTitleAsync(page);
        var htmlSnippet = await TryGetHtmlSnippetAsync(page, 220);
        throw new InvalidOperationException(
            $"No content selector could be resolved. Tried [{string.Join("; ", failures)}]. PageUrl='{page.Url}', Title='{title ?? "<unknown>"}', HtmlSnippet='{htmlSnippet}'.");
    }

    private async Task<ILocator> ResolveLocatorAsync(IPage page, string? selector, float timeoutMs)
    {
        var normalizedSelector = string.IsNullOrWhiteSpace(selector) ? "body" : selector.Trim();
        var resolution = await TryResolveLocatorAsync(page, normalizedSelector, timeoutMs);
        if (resolution.Locator is null)
            throw new InvalidOperationException(
                $"No element matched selector '{normalizedSelector}' within {timeoutMs}ms. {resolution.FailureReason}");

        return resolution.Locator;
    }

    private static async Task<LocatorResolution> TryResolveLocatorAsync(IPage page, string selector, float timeoutMs)
    {
        try
        {
            var locator = page.Locator(selector).First;
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = timeoutMs
            });

            if (await locator.CountAsync() == 0)
                return new LocatorResolution(null, "selector resolved but no elements were found");

            return new LocatorResolution(locator, null);
        }
        catch (PlaywrightException ex)
        {
            return new LocatorResolution(null, NormalizeSelectorError(ex));
        }
        catch (TimeoutException)
        {
            return new LocatorResolution(null, "timeout while waiting for selector");
        }
    }

    private static List<string> BuildContentSelectorCandidates(string requestedSelector)
    {
        var candidates = new List<string> { requestedSelector };
        if (!string.Equals(requestedSelector, "body", StringComparison.OrdinalIgnoreCase))
            candidates.Add("body");
        if (!string.Equals(requestedSelector, "html", StringComparison.OrdinalIgnoreCase))
            candidates.Add("html");
        return candidates;
    }

    private static string NormalizeSelectorError(PlaywrightException exception)
    {
        var message = exception.Message.Trim();
        if (message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return "timeout while waiting for selector";
        if (message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unknown engine", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Invalid selector", StringComparison.OrdinalIgnoreCase))
        {
            return $"invalid selector syntax ({message})";
        }

        return message;
    }

    private static async Task<string?> TryGetPageTitleAsync(IPage page)
    {
        try
        {
            return await page.TitleAsync();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryGetHtmlSnippetAsync(IPage page, int maxCharacters)
    {
        try
        {
            var html = await page.ContentAsync();
            var normalized = NormalizeText(html);
            if (normalized.Length <= maxCharacters)
                return normalized;

            return normalized[..maxCharacters];
        }
        catch (Exception ex)
        {
            return $"<unavailable: {ex.GetType().Name}>";
        }
    }

    private static async Task<ILocator> ResolveTextLocatorAsync(IPage page, string text, bool exact)
    {
        var normalizedText = NormalizeRequiredValue(text, nameof(text));
        var locator = page.GetByText(normalizedText, new PageGetByTextOptions { Exact = exact }).First;
        if (await locator.CountAsync() == 0)
            throw new InvalidOperationException($"No element matched text '{normalizedText}'.");

        return locator;
    }

    private static async Task<bool> DetermineSubmittedAsync(ILocator locator)
    {
        var tagName = await locator.EvaluateAsync<string>("element => (element.tagName || '').toLowerCase()");
        var typeAttribute = await locator.EvaluateAsync<string>("element => typeof element.getAttribute === 'function' ? (element.getAttribute('type') || '') : ''");
        var hasAssociatedForm = await locator.EvaluateAsync<bool>("""
            element => {
                const directForm = 'form' in element ? element.form : null;
                const closestForm = typeof element.closest === 'function' ? element.closest('form') : null;
                const formAttribute = typeof element.getAttribute === 'function' ? element.getAttribute('form') : null;
                return !!directForm || !!closestForm || !!formAttribute;
            }
            """);

        return BrowserActionSemantics.LooksLikeSubmitControl(tagName, typeAttribute, hasAssociatedForm);
    }

    private static NavigationObservation ObserveMainFrameNavigation(IPage page)
    {
        var observation = new NavigationObservation(page);
        observation.Start();
        return observation;
    }

    private async Task CloseSessionUnlockedAsync()
    {
        InvalidateObservation("closure");
        await DisposeRetiredObservationHandlesAsync();
        if (_page is not null)
        {
            await _page.CloseAsync();
            _page = null;
        }

        if (_context is not null)
        {
            await _context.CloseAsync();
            _context = null;
        }
    }

    private async Task HoldOpenIfConfiguredAsync(CancellationToken cancellationToken)
    {
        if (_settings.HoldOpenMs <= 0)
            return;

        _logger.LogInformation(
            "Holding browser open for {HoldOpenMs}ms for visual inspection.",
            _settings.HoldOpenMs);

        await Task.Delay(_settings.HoldOpenMs, cancellationToken);
    }

    private static float NormalizeTimeout(int? timeoutMs, int fallback)
    {
        var value = timeoutMs.GetValueOrDefault(fallback);
        if (value <= 0)
            throw new InvalidOperationException("Timeout values must be greater than zero.");

        return value;
    }

    private static int NormalizeQuality(int quality)
    {
        if (quality is < 0 or > 100)
            throw new InvalidOperationException("Screenshot quality must be between 0 and 100.");

        return quality;
    }

    private static string NormalizeRequiredValue(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{parameterName} must not be empty.");

        return normalized;
    }

    private static string NormalizeFormat(string format)
    {
        var normalized = string.IsNullOrWhiteSpace(format) ? "text" : format.Trim().ToLowerInvariant();
        return normalized switch
        {
            "text" or "html" or "observation" or "observation_pages" or "observation_complete" => normalized,
            _ => throw new InvalidOperationException("format must be 'text', 'html', 'observation', 'observation_pages' or 'observation_complete'.")
        };
    }

    private static WaitUntilState ParseWaitUntil(string waitUntil)
    {
        var normalized = string.IsNullOrWhiteSpace(waitUntil) ? "load" : waitUntil.Trim().ToLowerInvariant();
        return normalized switch
        {
            "load" => WaitUntilState.Load,
            "domcontentloaded" => WaitUntilState.DOMContentLoaded,
            "networkidle" => WaitUntilState.NetworkIdle,
            _ => throw new InvalidOperationException("waitUntil must be one of: load, domcontentloaded, networkidle.")
        };
    }

    private static LoadState ParseLoadState(string waitUntil)
    {
        var normalized = string.IsNullOrWhiteSpace(waitUntil) ? "domcontentloaded" : waitUntil.Trim().ToLowerInvariant();
        return normalized switch
        {
            "load" => LoadState.Load,
            "domcontentloaded" => LoadState.DOMContentLoaded,
            "networkidle" => LoadState.NetworkIdle,
            _ => throw new InvalidOperationException("waitUntil must be one of: load, domcontentloaded, networkidle.")
        };
    }

    private static WaitForSelectorState NormalizeWaitState(string? state)
    {
        var normalized = string.IsNullOrWhiteSpace(state) ? "visible" : state.Trim().ToLowerInvariant();
        return normalized switch
        {
            "attached" => WaitForSelectorState.Attached,
            "detached" => WaitForSelectorState.Detached,
            "visible" => WaitForSelectorState.Visible,
            "hidden" => WaitForSelectorState.Hidden,
            _ => throw new InvalidOperationException("state must be one of: attached, detached, visible, hidden.")
        };
    }

    private static ScreenshotType ParseScreenshotType(string type)
    {
        var normalized = string.IsNullOrWhiteSpace(type) ? "png" : type.Trim().ToLowerInvariant();
        return normalized switch
        {
            "png" => ScreenshotType.Png,
            "jpeg" or "jpg" => ScreenshotType.Jpeg,
            _ => throw new InvalidOperationException("type must be either 'png' or 'jpeg'.")
        };
    }

    private static string NormalizeText(string input)
        => Regex.Replace(input, "\\s+", " ").Trim();
}

public sealed record BrowserContentResult(
    string Url,
    string? Title,
    int? StatusCode,
    string? Selector,
    string ResolvedSelector,
    bool FallbackApplied,
    string? FallbackReason,
    string Format,
    string Content,
    bool Truncated,
    int MaxCharacters,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserObservation? Observation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserObservationManifest? ObservationManifest { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserObservationSnapshot? ObservationSnapshot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserSnapshotAcquisition? Acquisition { get; init; }
}

internal sealed record LocatorResolution(ILocator? Locator, string? FailureReason);

internal sealed record ContentLocatorResolution(
    ILocator Locator,
    string ResolvedSelector,
    bool FallbackApplied,
    string? FallbackReason);

public sealed record BrowserActionResult(
    string Action,
    string Url,
    string? Title,
    string Selector,
    bool Submitted,
    bool TriggeredNavigation,
    string NavigationType,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserObservedTarget? Target { get; init; }
}

public sealed record BrowserKeyActionResult(
    string Action,
    string Url,
    string? Title,
    string Selector,
    string Key,
    bool TriggeredNavigation,
    string NavigationType,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserObservedTarget? Target { get; init; }
}

public sealed record BrowserSelectResult(
    string Url,
    string? Title,
    string Selector,
    IReadOnlyList<string> SelectedValues,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserObservedTarget? Target { get; init; }
}

public sealed record BrowserWaitResult(
    string Url,
    string? Title,
    string? Selector,
    string? State,
    int DelayMs,
    bool Completed,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

public sealed record BrowserScreenshotResult(
    string Url,
    string? Title,
    string MimeType,
    string DataBase64,
    bool FullPage,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

public sealed record BrowserCloseResult(
    bool Closed,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

internal sealed class NavigationObservation : IDisposable
{
    private readonly IPage _page;
    private readonly object _sync = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _started;
    private bool _triggered;
    private bool _domReady;
    private bool _loadReady;
    private bool _closed;
    private long _version;
    private long _navigationVersion;

    // Deterministic test checkpoint in the former check/subscribe race window.
    internal Func<Task>? ReadinessCheckpoint { get; init; }

    public NavigationObservation(IPage page)
    {
        _page = page;
    }

    public bool Triggered { get { lock (_sync) return _triggered; } }

    public void Start()
    {
        if (_started)
            return;

        _page.FrameNavigated += OnFrameNavigated;
        _page.DOMContentLoaded += OnDomContentLoaded;
        _page.Load += OnLoad;
        _page.Close += OnClosed;
        _page.Crash += OnClosed;
        _started = true;
    }

    public async Task WaitForLoadStateAsync(LoadState state, float timeoutMs, CancellationToken cancellationToken)
    {
        if (state == LoadState.NetworkIdle)
        {
            // Network quiescence remains Playwright-owned; DOM readiness is not a substitute.
            await _page.WaitForLoadStateAsync(state, new() { Timeout = timeoutMs }).WaitAsync(cancellationToken);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            while (true)
            {
                Task changed;
                long version;
                long navigationVersion;
                bool observed;
                lock (_sync)
                {
                    if (!_started) throw new ObjectDisposedException(nameof(NavigationObservation));
                    if (_closed || _page.IsClosed) throw new PlaywrightException("The page closed or crashed while waiting for lifecycle readiness.");
                    changed = _changed.Task;
                    version = _version;
                    navigationVersion = _navigationVersion;
                    observed = state == LoadState.Load ? _loadReady : _domReady;
                }

                if (ReadinessCheckpoint is { } checkpoint) await checkpoint().WaitAsync(deadline.Token);
                // Subscribe before checking. Events wake this loop even if they arrive while
                // evaluating; navigation invalidates the result before it can be accepted.
                // Timing also covers an already-loaded document and same-document navigation.
                // "interactive" alone precedes DOMContentLoaded and is not sufficient.
                bool ready;
                try { ready = await _page.EvaluateAsync<bool>("""
                    ([load, observed]) => {
                        const entry = performance.getEntriesByType('navigation')[0];
                        return load
                            ? document.readyState === 'complete' && (observed || entry?.loadEventStart > 0)
                            : document.readyState === 'complete' ||
                                (document.readyState === 'interactive' && (observed || entry?.domContentLoadedEventStart > 0));
                    }
                    """, new[] { state == LoadState.Load, observed }).WaitAsync(deadline.Token); }
                catch (PlaywrightException) when (NavigationChanged(navigationVersion))
                {
                    // Only a verified navigation can invalidate an in-flight readiness read.
                    // Recheck its new document; never repeat the interaction.
                    continue;
                }
                lock (_sync)
                {
                    if (!_started) throw new ObjectDisposedException(nameof(NavigationObservation));
                    if (_closed || _page.IsClosed) throw new PlaywrightException("The page closed or crashed while waiting for lifecycle readiness.");
                    if (ready && version == _version) return;
                }
                await changed.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout {timeoutMs}ms exceeded while waiting for {state}.");
        }
    }

    public void Dispose()
    {
        if (!_started)
            return;

        _page.FrameNavigated -= OnFrameNavigated;
        _page.DOMContentLoaded -= OnDomContentLoaded;
        _page.Load -= OnLoad;
        _page.Close -= OnClosed;
        _page.Crash -= OnClosed;
        lock (_sync) { _started = false; SignalChanged(); }
    }

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        if (frame != _page.MainFrame) return;
        lock (_sync)
        {
            _triggered = true;
            _navigationVersion++;
            _domReady = _loadReady = false;
            SignalChanged();
        }
    }

    private void OnDomContentLoaded(object? sender, IPage page)
    { lock (_sync) { _domReady = true; SignalChanged(); } }

    private void OnLoad(object? sender, IPage page)
    { lock (_sync) { _domReady = _loadReady = true; SignalChanged(); } }

    private void OnClosed(object? sender, IPage page)
    { lock (_sync) { _closed = true; SignalChanged(); } }

    private void SignalChanged()
    {
        _version++;
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool NavigationChanged(long version)
    { lock (_sync) return _started && !_closed && _navigationVersion != version; }
}
