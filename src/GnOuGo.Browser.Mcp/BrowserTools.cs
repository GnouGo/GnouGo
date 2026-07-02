using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ModelContextProtocol;
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


    [McpServerTool(Name = "browser_get_content", UseStructuredContent = true, OutputSchemaType = typeof(BrowserContentResult)), Description("Reads rendered content from the current page or from a CSS selector. If url is provided, this tool first navigates to that absolute http/https URL, waits for the requested load state, then returns the content in the same call. Prefer this one-shot tool when the goal is simply to open a page and inspect or extract its content. Prefer waitUntil='domcontentloaded' or 'load' for search/e-commerce pages such as Amazon; avoid 'networkidle' unless the page is known to become idle. Use format='text' for readable visible text, summaries, and plain content extraction (example: summarize an article or read a confirmation message). Use format='html' when you need DOM structure, links, href/src attributes, button labels, form fields, menu/navigation markup, or when the client must decide what element to click based on the rendered HTML (example: extract menu links from nav/header, inspect a consent banner, or build a reliable CSS selector). Script elements are stripped from returned HTML by default to keep responses compact and useful for MCP clients.")]
    public async Task<BrowserContentResult> GetContentAsync(
        [Description("Optional absolute URL to open before reading content. Prefer setting this for one-shot page reads so the tool both navigates and returns content in a single call. When omitted, the tool reads from the current page.")] string? url = null,
        [Description("Navigation wait mode used when url is provided: load, domcontentloaded, or networkidle. Prefer domcontentloaded/load for dynamic shopping/search pages; networkidle can time out on Amazon-like pages.")] string waitUntil = "load",
        [Description("Optional navigation timeout in milliseconds used when url is provided.")] int? timeoutMs = null,
        [Description("Optional CSS selector. Defaults to the body element. Prefer scoping to nav/header/menu/form containers when inspecting links or interactive elements. Example: selector='nav' with format='html' for menu links.")] string? selector = null,
        [Description("Return format: text or html. Example text => article summary, success message, visible page copy. Example html => nav/header links, href/src attributes, forms, buttons, tables, selectors, and any task where the client must inspect the rendered html markup before acting.")] string format = "html",
        [Description("Maximum number of characters returned.")] int? maxCharacters = null,
        [Description("Include <script> elements and inline script content in HTML responses. Defaults to false because scripts are usually noisy and very large.")] bool includeScriptContent = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.GetContentAsync(url, waitUntil, timeoutMs, selector, format, maxCharacters, includeScriptContent, cancellationToken);
        }
        catch (Exception ex)
        {
            var correlation = BrowserToolCorrelation.Current;
            _logger.LogError(
                ex,
                "browser_get_content failed for url={Url}, correlationId={CorrelationId}, runId={RunId}, traceparent={TraceParent}",
                url,
                correlation.CorrelationId,
                correlation.RunId,
                correlation.TraceParent);

            return BrowserToolFailure.Content(url, selector, format, ex);
        }
    }

    [McpServerTool(Name = "browser_click", UseStructuredContent = true, OutputSchemaType = typeof(BrowserActionResult)), Description("Clicks the first element matching a CSS selector on the current page. Use this when the client already knows a reliable selector. If the goal is to inspect menus, links, buttons, forms, or consent banners first, call browser_get_content with format='html' before clicking so the client can inspect the rendered DOM and attributes.")]
    public async Task<BrowserActionResult> ClickAsync(
        [Description("CSS selector to click. Prefer selectors derived from rendered HTML inspection rather than visible text alone when links, hrefs, or nested markup matter.")] string selector,
        [Description("Load-state wait mode after the click: load, domcontentloaded, or networkidle.")] string waitUntil = "domcontentloaded",
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.ClickAsync(selector, waitUntil, timeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_click failed for selector={Selector}", selector);
            return BrowserToolFailure.Action("click", selector, ex);
        }
    }

    [McpServerTool(Name = "browser_fill", UseStructuredContent = true, OutputSchemaType = typeof(BrowserActionResult)), Description("Fills an input or textarea, with optional Enter submission. Use this when the client already knows a reliable selector for the target field. If the client must first identify which field corresponds to a label, placeholder, form section, or custom markup, inspect browser_get_content with format='html' first and derive a selector from the rendered DOM.")]
    public async Task<BrowserActionResult> FillAsync(
        [Description("CSS selector of the target input or textarea element. Example: input[name='email'], textarea[name='message'], #search-box. Prefer selectors derived from rendered HTML when forms contain multiple similar fields.")] string selector,
        [Description("Value to type into the field. Use the exact text the client wants to enter.")] string value,
        [Description("Press Enter after filling the field. Example: submit=true for a known search box or simple form field that submits on Enter.")] bool submit = false,
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.FillAsync(selector, value, submit, timeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_fill failed for selector={Selector}", selector);
            return BrowserToolFailure.Action(submit ? "fill_submit" : "fill", selector, ex);
        }
    }

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

    [McpServerTool(Name = "browser_press", UseStructuredContent = true, OutputSchemaType = typeof(BrowserKeyActionResult)), Description("Presses a keyboard key on the first element matching a CSS selector. Use this when keyboard interaction is intentional, for example Enter to submit a focused field, Tab to move focus, Escape to close a dialog, or ArrowDown to navigate a list. If the client does not yet know the right target element, inspect browser_get_content with format='html' first and derive a selector from the rendered DOM.")]
    public async Task<BrowserKeyActionResult> PressAsync(
        [Description("CSS selector of the target element. Prefer selectors derived from rendered HTML when focus order or element type matters.")] string selector,
        [Description("Keyboard key to press, for example Enter, Tab, Escape, ArrowDown. Example: key='Enter' to submit a known input field.")] string key,
        [Description("Load-state wait mode after the key press: load, domcontentloaded, or networkidle.")] string waitUntil = "domcontentloaded",
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.PressAsync(selector, key, waitUntil, timeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_press failed for selector={Selector}, key={Key}", selector, key);
            return BrowserToolFailure.KeyAction("press", selector, key, ex);
        }
    }

    [McpServerTool(Name = "browser_select", UseStructuredContent = true, OutputSchemaType = typeof(BrowserSelectResult)), Description("Selects an option value in a <select> element. Use this only for real HTML <select> controls. If the client must first inspect available options, labels, or determine whether the control is a native <select> or a custom widget, inspect browser_get_content with format='html' first.")]
    public async Task<BrowserSelectResult> SelectAsync(
        [Description("CSS selector of the target select element. Example: select[name='country'] or #my-select.")] string selector,
        [Description("Option value to select. This is the HTML option value, not the visible label. Inspect HTML first if the client only knows the label.")] string value,
        [Description("Optional timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _browserHost.SelectAsync(selector, value, timeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "browser_select failed for selector={Selector}, value={Value}", selector, value);
            return BrowserToolFailure.Select(selector, ex);
        }
    }

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
        return new BrowserContentResult(
            Url: url ?? string.Empty,
            Title: null,
            StatusCode: null,
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
            ErrorMessage: message);
    }

    public static BrowserActionResult Action(string action, string selector, Exception exception)
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
            ErrorMessage: message);
    }

    public static BrowserKeyActionResult KeyAction(string action, string selector, string key, Exception exception)
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
            ErrorMessage: message);
    }

    public static BrowserSelectResult Select(string selector, Exception exception)
    {
        var (code, message) = Describe(exception);
        return new BrowserSelectResult(
            Url: string.Empty,
            Title: null,
            Selector: selector,
            SelectedValues: [],
            Success: false,
            ErrorCode: code,
            ErrorMessage: message);
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
        => (Classify(exception), exception is OperationCanceledException
            ? "The operation was cancelled by the client."
            : $"{exception.GetType().Name}: {exception.Message}");

    private static string Classify(Exception exception)
        => exception switch
        {
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
