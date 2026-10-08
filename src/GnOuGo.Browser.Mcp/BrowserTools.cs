using GnOuGo.Mcp.Core;
using ModelContextProtocol;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ModelContextProtocol.Server;

namespace GnOuGo.Browser.Mcp;

[McpServerToolType]
public sealed class BrowserTools
{
    private readonly PlaywrightBrowserHost _browserHost;
    private readonly ILogger<BrowserTools> _logger;

    public BrowserTools(PlaywrightBrowserHost browserHost, ILogger<BrowserTools> logger)
    {
        _browserHost = browserHost;
        _logger = logger;
    }


    // Existing C# entrypoints retain their selector semantics; MCP advertises the additive target form.
    public Task<BrowserActionResult> ClickAsync(string selector, string waitUntil = "domcontentloaded", int? timeoutMs = null, CancellationToken cancellationToken = default)
        => ClickTargetAsync(selector: selector, waitUntil: waitUntil, timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    public Task<BrowserActionResult> FillAsync(string selector, string value, bool submit = false, int? timeoutMs = null, CancellationToken cancellationToken = default)
        => FillTargetAsync(value: value, selector: selector, submit: submit, timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    public Task<BrowserKeyActionResult> PressAsync(string selector, string key, string waitUntil = "domcontentloaded", int? timeoutMs = null, CancellationToken cancellationToken = default)
        => PressTargetAsync(key: key, selector: selector, waitUntil: waitUntil, timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    public Task<BrowserSelectResult> SelectAsync(string selector, string value, int? timeoutMs = null, CancellationToken cancellationToken = default)
        => SelectTargetAsync(value: value, selector: selector, timeoutMs: timeoutMs, cancellationToken: cancellationToken);

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Read)]
    [McpServerTool(Name = "browser_get_content", UseStructuredContent = true, OutputSchemaType = typeof(BrowserContentResult)), Description("Reads rendered content from the current page or from a CSS selector. Prefer format=observation_complete when all observation pages are needed: returns observationSnapshot.pages with typed records from one complete generation, no continuation calls. Internally discards navigation-invalidated attempts, with at most two restarts sharing one timeout. An explicit successful GET acquisition may reload one wholly empty document within those same bounds; current-page reads and interactions never trigger reload. OBSERVATION_EMPTY is an unusable acquisition, not evidence of CAPTCHA or missing business data. Incomplete captures or exhausted bounds fail explicitly; narrow the selector yourself. Acquisition metadata preserves attempts, invalidation/recovery reasons and generation-specific navigation status. Page limits and capture limits remain enforced. Legacy cursors fail with SNAPSHOT_EXPIRED after invalidation and never switch generations. Use format=observation_pages for a bounded immutable snapshot manifest in observationManifest.pages (cursor, recordCount). Read every listed cursor with either observation format, no URL/selector or limit overrides, before navigation. Each page returns observation.records without duplicate flat content. Only a fully consumed manifest with captureTruncated=false and manifestTruncated=false establishes snapshot coverage; otherwise narrow the selector. An empty complete manifest establishes an empty snapshot. Legacy format=observation returns incremental typed visible text, links, headings and controls. A truncated observation or nextCursor is partial, never evidence of an empty or complete result set. captureTruncated requires a narrower read even when nextCursor is null. Visible dialog controls retain their DOM group; handle authorized cookie consent with an explicit conditional click on an observed control and then read again. This read never clicks consent. It preserves observed DOM grouping and resolved link URLs without interpreting business fields. Records provide an opaque reference and supported actions: let decisions select observed references, then pass them to action tools instead of generating selectors. A follow link cannot satisfy activate. References expire after interaction/navigation; read again before the next observed action. Truncation is explicit: use the returned observation.nextCursor with format=observation and no URL/selector to continue the same snapshot, or narrow the selector. Navigation or interaction invalidates continuation. If url is provided, this tool first navigates to that absolute http/https URL, waits for the requested load state, then returns the content in the same call. Prefer this one-shot tool when the goal is simply to open a page and inspect or extract its content. Prefer waitUntil='domcontentloaded' or 'load' for pages with background requests; avoid 'networkidle' unless the page is known to become idle. Use format='text' for readable visible text, summaries, and plain content extraction (example: summarize an article or read a confirmation message). Use format='html' when you need DOM structure, links, href/src attributes, button labels, form fields, menu/navigation markup, or when the client must decide what element to click based on the rendered HTML (example: extract menu links from nav/header, inspect a consent banner, or build a reliable CSS selector). Script elements are stripped from returned HTML by default to keep responses compact and useful for MCP clients.")]
    public async Task<BrowserContentResult> GetContentAsync(
        [RegularExpression(BrowserNavigationPolicy.HttpUrlPattern), Description("Optional absolute HTTP/HTTPS URL to open before reading content. Decode percent-encoded whole URLs and resolve relative references against their observed page URL before calling. Escape whitespace. When omitted, reads the current page.")] string? url = null,
        [Description("Navigation wait mode used when url is provided: load, domcontentloaded, or networkidle. Prefer domcontentloaded/load for dynamic shopping/search pages; networkidle can time out on pages with continuous background requests.")] string waitUntil = "load",
        [Description("Optional navigation timeout in milliseconds. For observation_complete, one shared deadline covers navigation, capture and at most two restarts.")] int? timeoutMs = null,
        [Description("Optional CSS selector. Defaults to the body element. Prefer scoping to nav/header/menu/form containers when inspecting links or interactive elements. Example: selector='nav' with format='html' for menu links.")] string? selector = null,
        [RegularExpression("^(text|html|observation|observation_pages|observation_complete)$"), Description("Return format: observation_complete (complete bounded pages in one acquisition), observation_pages (bounded page manifest, then compact records via cursors), observation (legacy incremental records), text or html. Example text => article summary, success message, visible page copy. Example html => nav/header links, href/src attributes, forms, buttons, tables, selectors, and any task where the client must inspect the rendered html markup before acting.")] string format = "html",
        [Description("Maximum characters returned. Observation mode limits each serialized page to the host allowance (default 24000); observation_complete additionally bounds its whole result by page allowance times the host page cap. HTML/text retain existing behavior.")] int? maxCharacters = null,
        [Description("Include <script> elements and inline script content in HTML responses. Defaults to false because scripts are usually noisy and very large.")] bool includeScriptContent = false,
        [Description("Opaque cursor from observation.nextCursor or observationManifest.pages. Either observation format reads its original layout. Omit URL/selector and frozen paged limits. Navigation or interaction invalidates it.")] string? cursor = null,
        [Range(1, 200), Description("Maximum observation records per page, capped by host policy; default 200.")] int? maxRecords = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.GetContentAsync(url, waitUntil, timeoutMs, selector, format, maxCharacters, includeScriptContent, cancellationToken, cursor, maxRecords);
        }
        catch (Exception ex)
        {
            var correlation = BrowserToolCorrelation.Current;
            _logger.LogError(
                "browser_get_content failed: {ErrorType}, correlationId={CorrelationId}, runId={RunId}, traceparent={TraceParent}",
                ex is BrowserObservationException observation ? observation.Code : ex.GetType().Name,
                correlation.CorrelationId,
                correlation.RunId,
                correlation.TraceParent);

            return BrowserToolFailure.Content(url, selector, format, ex);
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Execute)]
    [McpServerTool(Name = "browser_click", UseStructuredContent = true, OutputSchemaType = typeof(BrowserActionResult)), Description("Clicks a target on the current page. Prefer reference from an observation record plus requestedAction=activate for a control or follow for a link; only the Browser resolves its selector and validates identity and action compatibility. An information link cannot satisfy control activation. References expire on navigation, interaction, replacement or closure. Acquire a fresh observation before later actions. Legacy selector calls click the first matching element; HTML remains available for legacy DOM inspection.")]
    public async Task<BrowserActionResult> ClickTargetAsync(
        [Description("Load-state wait mode after the click: load, domcontentloaded, or networkidle.")] string waitUntil = "domcontentloaded",
        [Description("Legacy CSS selector. Specify either selector or an observed reference, never both. Prefer reference for observed actions.")] string? selector = null,
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default,
        [Description("Opaque reference from a current observation record. The Browser resolves the exact observed element and rejects expired, changed or incompatible targets.")] string? reference = null,
        [RegularExpression("^(activate|follow)$"), Description("Required with reference: activate for an observed native/ARIA control; follow for an observed link. Omit for legacy selector calls. A label alone never makes a link an activation control.")] string? requestedAction = null)
    {
        try
        {
            return await _browserHost.ClickTargetAsync(selector, waitUntil, timeoutMs, cancellationToken, reference, requestedAction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_click failed for selector={Selector}", selector);
            return BrowserToolFailure.Action("click", selector ?? "", ex, reference, requestedAction ?? "click");
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Execute)]
    [McpServerTool(Name = "browser_fill", UseStructuredContent = true, OutputSchemaType = typeof(BrowserActionResult)), Description("Fills an input or textarea, with optional Enter submission. Prefer an observed reference supporting fill; omit selector. References identify the exact current element and expire after interaction; acquire a fresh observation for later actions. Legacy selector calls retain first-match semantics; HTML remains available for legacy DOM inspection.")]
    public async Task<BrowserActionResult> FillTargetAsync(
        [Description("Value to type into the field. Use the exact text the client wants to enter.")] string value,
        [Description("Press Enter after filling the field. Example: submit=true for a known search box or simple form field that submits on Enter.")] bool submit = false,
        [Description("Legacy CSS selector. Specify either selector or an observed reference, never both. Prefer reference for observed actions.")] string? selector = null,
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default,
        [Description("Opaque reference from a current observation record. The Browser resolves the exact observed element and rejects expired, changed or incompatible targets.")] string? reference = null)
    {
        try
        {
            return await _browserHost.FillTargetAsync(selector, value, submit, timeoutMs, cancellationToken, reference);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_fill failed for selector={Selector}", selector);
            return BrowserToolFailure.Action(submit ? "fill_submit" : "fill", selector ?? "", ex, reference, "fill");
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Execute)]
    [McpServerTool(Name = "browser_click_text", UseStructuredContent = true, OutputSchemaType = typeof(BrowserActionResult)), Description("Clicks the first visible element matching a text label. Use this when the client knows the visible label but not a stable selector. If multiple matching elements may exist, or if links/menu items must be distinguished by href or DOM position, inspect browser_get_content with format='html' first and prefer browser_click with a selector derived from the rendered HTML.")]
    public async Task<BrowserActionResult> ClickTextAsync(
        [Description("Visible text to match. Best for unique button labels like Submit, Next, Continue, OK, Accept, etc.")] string text,
        [Description("Require an exact text match. Set to true when multiple similar labels may exist.")] bool exact = false,
        [Description("Load-state wait mode after the click: load, domcontentloaded, or networkidle.")] string waitUntil = "domcontentloaded",
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.ClickTextAsync(text, exact, waitUntil, timeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_click_text failed for text={Text}", text);
            return BrowserToolFailure.Action("click_text", text, ex);
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Execute)]
    [McpServerTool(Name = "browser_press", UseStructuredContent = true, OutputSchemaType = typeof(BrowserKeyActionResult)), Description("Presses a keyboard key. Prefer an observed reference supporting press; omit selector. Legacy selectors retain first-match semantics. Use this when keyboard interaction is intentional, for example Enter to submit a focused field, Tab to move focus, Escape to close a dialog, or ArrowDown to navigate a list. Observe the target before selecting its reference; acquire a fresh observation after any interaction.")]
    public async Task<BrowserKeyActionResult> PressTargetAsync(
        [Description("Keyboard key to press, for example Enter, Tab, Escape, ArrowDown. Example: key='Enter' to submit a known input field.")] string key,
        [Description("Load-state wait mode after the key press: load, domcontentloaded, or networkidle.")] string waitUntil = "domcontentloaded",
        [Description("Legacy CSS selector. Specify either selector or an observed reference, never both. Prefer reference for observed actions.")] string? selector = null,
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default,
        [Description("Opaque reference from a current observation record. The Browser resolves the exact observed element and rejects expired, changed or incompatible targets.")] string? reference = null)
    {
        try
        {
            return await _browserHost.PressTargetAsync(selector, key, waitUntil, timeoutMs, cancellationToken, reference);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_press failed for selector={Selector}, key={Key}", selector, key);
            return BrowserToolFailure.KeyAction("press", selector ?? "", key, ex, reference);
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Execute)]
    [McpServerTool(Name = "browser_select", UseStructuredContent = true, OutputSchemaType = typeof(BrowserSelectResult)), Description("Selects an option value in a <select> element. Prefer an observed reference supporting select; omit selector. Use this only for real HTML <select> controls. If the client must first inspect available options, labels, or determine whether the control is a native <select> or a custom widget, inspect browser_get_content with format='html' first.")]
    public async Task<BrowserSelectResult> SelectTargetAsync(
        [Description("Option value to select. This is the HTML option value, not the visible label. Inspect HTML first if the client only knows the label.")] string value,
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        [Description("Legacy CSS selector. Specify either selector or an observed reference, never both. Prefer reference for observed actions.")] string? selector = null,
        CancellationToken cancellationToken = default,
        [Description("Opaque reference from a current observation record. The Browser resolves the exact observed element and rejects expired, changed or incompatible targets.")] string? reference = null)
    {
        try
        {
            return await _browserHost.SelectTargetAsync(selector, value, timeoutMs, cancellationToken, reference);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_select failed for selector={Selector}, value={Value}", selector, value);
            return BrowserToolFailure.Select(selector ?? "", ex, reference);
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Read)]
    [McpServerTool(Name = "browser_wait", UseStructuredContent = true, OutputSchemaType = typeof(BrowserWaitResult)), Description("Waits for a selector state and/or a fixed delay before continuing a scenario. Use selector waiting when the client already knows the element or container that should appear/disappear. If the client does not yet know what DOM element indicates readiness, inspect browser_get_content with format='html' first, choose a stable selector, then wait on that selector.")]
    public async Task<BrowserWaitResult> WaitAsync(
        [Description("Optional CSS selector to wait for. Example: form, nav a, .modal, [data-testid='results']. Prefer selectors chosen after HTML inspection when readiness is ambiguous.")] string? selector = null,
        [Description("Selector state: attached, detached, visible, or hidden. Example: visible for a form or modal, hidden for a spinner or overlay.")] string? state = null,
        [Description("Optional fixed delay in milliseconds. Use this only when no reliable selector exists or when a short debounce is needed after an action.")] int? delayMs = null,
        [Description("Optional timeout in milliseconds for selector waiting.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.WaitAsync(selector, state, delayMs, timeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_wait failed for selector={Selector}", selector);
            return BrowserToolFailure.Wait(selector, state, delayMs, ex);
        }
    }

    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Read)]
    [McpServerTool(Name = "browser_screenshot", UseStructuredContent = true, OutputSchemaType = typeof(BrowserScreenshotResult)), Description("Captures the current page as base64-encoded PNG or JPEG.")]
    public async Task<BrowserScreenshotResult> ScreenshotAsync(
        [Description("Capture the full page instead of only the viewport.")] bool fullPage = true,
        [Description("Image type: png or jpeg.")] string type = "png",
        [Description("JPEG quality from 0 to 100. Ignored for PNG.")] int? quality = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.ScreenshotAsync(fullPage, type, quality, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_screenshot failed");
            return BrowserToolFailure.Screenshot(fullPage, type, ex);
        }
    }


    [McpMeta("gnougo", JsonValue = McpEffectMetadata.Lifecycle)]
    [McpServerTool(Name = "browser_close", UseStructuredContent = true, OutputSchemaType = typeof(BrowserCloseResult)), Description("Closes the current browser page and context.")]
    public async Task<BrowserCloseResult> CloseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.CloseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_close failed");
            return BrowserToolFailure.Close(ex);
        }
    }
}

internal static class BrowserToolFailure
{
    public static BrowserContentResult Content(string? url, string? selector, string format, Exception exception)
    {
        var (code, message) = Describe(exception);
        var acquisition = (exception as BrowserObservationException)?.Acquisition
            ?? exception.Data[nameof(BrowserSnapshotAcquisition)] as BrowserSnapshotAcquisition;
        return new BrowserContentResult(
            Url: acquisition?.Navigation?.Url ?? url ?? string.Empty,
            Title: acquisition?.Navigation?.Title,
            StatusCode: acquisition?.Navigation?.StatusCode,
            Selector: selector,
            ResolvedSelector: selector ?? string.Empty,
            FallbackApplied: false,
            FallbackReason: null,
            Format: format,
            Content: string.Empty,
            Truncated: false,
            MaxCharacters: 0,
            Success: false,
            ErrorCode: code,
            ErrorMessage: message) { Acquisition = acquisition };
    }

    public static BrowserActionResult Action(string action, string selector, Exception exception, string? reference = null, string? requestedAction = null)
    {
        var (code, message) = Describe(exception);
        return new BrowserActionResult(
            Action: action,
            Url: string.Empty,
            Title: null,
            Selector: selector,
            Submitted: false,
            TriggeredNavigation: false,
            NavigationType: "none",
            Success: false,
            ErrorCode: code,
            ErrorMessage: message) { Target = PlaywrightBrowserHost.ObservedTarget(reference, requestedAction ?? action, code) };
    }

    public static BrowserKeyActionResult KeyAction(string action, string selector, string key, Exception exception, string? reference = null)
    {
        var (code, message) = Describe(exception);
        return new BrowserKeyActionResult(
            Action: action,
            Url: string.Empty,
            Title: null,
            Selector: selector,
            Key: key,
            TriggeredNavigation: false,
            NavigationType: "none",
            Success: false,
            ErrorCode: code,
            ErrorMessage: message) { Target = PlaywrightBrowserHost.ObservedTarget(reference, "press", code) };
    }

    public static BrowserSelectResult Select(string selector, Exception exception, string? reference = null)
    {
        var (code, message) = Describe(exception);
        return new BrowserSelectResult(
            Url: string.Empty,
            Title: null,
            Selector: selector,
            SelectedValues: [],
            Success: false,
            ErrorCode: code,
            ErrorMessage: message) { Target = PlaywrightBrowserHost.ObservedTarget(reference, "select", code) };
    }

    public static BrowserWaitResult Wait(string? selector, string? state, int? delayMs, Exception exception)
    {
        var (code, message) = Describe(exception);
        return new BrowserWaitResult(
            Url: string.Empty,
            Title: null,
            Selector: selector,
            State: state,
            DelayMs: delayMs ?? 0,
            Completed: false,
            Success: false,
            ErrorCode: code,
            ErrorMessage: message);
    }

    public static BrowserScreenshotResult Screenshot(bool fullPage, string type, Exception exception)
    {
        var (code, message) = Describe(exception);
        return new BrowserScreenshotResult(
            Url: string.Empty,
            Title: null,
            MimeType: string.Equals(type, "jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png",
            DataBase64: string.Empty,
            FullPage: fullPage,
            Success: false,
            ErrorCode: code,
            ErrorMessage: message);
    }

    public static BrowserCloseResult Close(Exception exception)
    {
        var (code, message) = Describe(exception);
        return new BrowserCloseResult(
            Closed: false,
            Success: false,
            ErrorCode: code,
            ErrorMessage: message);
    }

    private static (string Code, string Message) Describe(Exception exception)
        => (Classify(exception), exception is BrowserObservationException or BrowserActionReferenceException ? exception.Message : exception is OperationCanceledException
            ? "The operation was cancelled by the client."
            : $"{exception.GetType().Name}: {exception.Message}");

    private static string Classify(Exception exception)
        => exception switch
        {
            BrowserObservationException observation => observation.Code,
            BrowserActionReferenceException action => action.Code,
            OperationCanceledException => "CANCELLED",
            TimeoutException => "TIMEOUT",
            PlaywrightException => "BROWSER_ERROR",
            ArgumentException or InvalidOperationException => "INVALID_INPUT",
            UnauthorizedAccessException => "ACCESS_DENIED",
            IOException => "IO_ERROR",
            _ => "INTERNAL_ERROR"
        };
}

public sealed record BrowserToolErrorResult(
    bool Success,
    string Tool,
    string ErrorType,
    string ErrorMessage,
    string? Url,
    string? Selector,
    string? CorrelationId,
    string? RunId,
    string? TraceId,
    string? SpanId,
    string? TraceParent)
{
    public static BrowserToolErrorResult FromException(
        string tool,
        string? url,
        string? selector,
        Exception exception,
        BrowserToolCorrelation correlation)
        => new(
            Success: false,
            Tool: tool,
            ErrorType: exception.GetType().FullName ?? exception.GetType().Name,
            ErrorMessage: exception.Message,
            Url: url,
            Selector: selector,
            CorrelationId: correlation.CorrelationId,
            RunId: correlation.RunId,
            TraceId: correlation.TraceId,
            SpanId: correlation.SpanId,
            TraceParent: correlation.TraceParent);
}

public sealed record BrowserToolCorrelation(
    string? CorrelationId,
    string? RunId,
    string? TraceId,
    string? SpanId,
    string? TraceParent,
    string? StepId,
    string? StepType,
    string? McpMethod)
{
    public static BrowserToolCorrelation Current => new(
        CorrelationId: Environment.GetEnvironmentVariable("GNouGo__CorrelationId"),
        RunId: Environment.GetEnvironmentVariable("GNouGo__RunId"),
        TraceId: Environment.GetEnvironmentVariable("GNouGo__TraceId"),
        SpanId: Environment.GetEnvironmentVariable("GNouGo__SpanId"),
        TraceParent: Environment.GetEnvironmentVariable("GNouGo__TraceParent"),
        StepId: Environment.GetEnvironmentVariable("GNouGo__StepId"),
        StepType: Environment.GetEnvironmentVariable("GNouGo__StepType"),
        McpMethod: Environment.GetEnvironmentVariable("GNouGo__McpMethod"));
}
