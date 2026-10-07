# GnOuGo.Browser.Mcp

`GnOuGo.Browser.Mcp` is a **stdio** MCP server based on **Playwright.NET**. It exposes a set of web navigation tools usable from `GnOuGo.Flow` or any compatible MCP client.

Build and publish install Chromium through the Playwright CLI. Installation succeeds
only when that command exits with zero; its recovered download errors remain visible
without being misclassified as compiler errors. A nonzero installer exit fails the build.

## MCP protocol compatibility

This stdio server uses the stable C# MCP SDK `2.0.0` with automatic protocol negotiation: clients prefer `2026-07-28` discovery and can initialize with stable `2025-11-25`. Launch the built apphost, or use `dotnet GnOuGo.Browser.Mcp.dll`; do not put `dotnet run` on an MCP stdio transport because CLI output can corrupt the JSONL stream. The GnOuGo progress stream remains a stderr side channel and does not alter the MCP wire contract.

## Exposed Tools

- `browser_get_content`: reads visible text or rendered HTML; can also open a URL and return content in the same call
- `browser_click`: clicks a CSS selector
- `browser_fill`: fills a field and can submit with Enter
- `browser_click_text`: clicks a button or link by its visible text
- `browser_press`: sends a keyboard key (`Enter`, `Tab`, `Escape`, etc.) on a targeted element
- `browser_select`: selects a value in a `<select>` element
- `browser_wait`: waits for a selector (`visible`, `hidden`, etc.) and/or a fixed delay
- `browser_screenshot`: returns a base64 screenshot
- `browser_close`: closes the current Playwright session

All tools return structured content. Playwright, policy, timeout, cancellation, and unexpected failures are returned in the advertised result type with `success: false`, `ok: false`, `error_code`, and `error_message`.

These tools allow building MCP scenarios such as:

- opening a page via `browser_get_content(url: ...)`
- waiting for a form to load
- filling text fields / textareas
- selecting options from a dropdown
- clicking buttons by CSS selector or visible text
- submitting and reading the result

Practical rule for `browser_get_content`:

- for a "one-shot" page read, prefer `browser_get_content(url: ..., format: ...)` so a single call handles both navigation and extraction
- use `format: text` to summarize a page, read visible content, extract readable text, or feed a synthesis
- use `format: html` whenever you need to preserve the DOM structure or attributes, e.g., to extract menu/navigation links, retrieve `href`/`src`, inspect buttons, forms, tables, or decide which element to click on the MCP client side
- 
- `format: html` strips `<script>` elements by default before returning content, which keeps pages such as Amazon compact and avoids sending large inline JavaScript/state blobs to MCP clients
- set `includeScriptContent: true` only when debugging raw page scripts or when script tags are explicitly needed
- for a menu, header, or cookie banner, it is generally better to target a specific selector (`nav`, `header`, `form`, etc.) with `format: html` rather than `text`, otherwise useful URLs and attributes will be lost
- robustness note: when a requested content selector is temporarily unavailable, `browser_get_content` falls back to `body` then `html` and returns `resolvedSelector` / `fallbackApplied` metadata in the result

### Goal â†’ Recommended Format

| MCP client goal | Recommended format | Why |
|---|---|---|
| Summarize a page or read its visible content | `text` | simpler, more compact, closer to readable rendering |
| Extract menu, header, footer, or navigation links | `html` | plain text loses `<a>` tags and `href` values |
| Decide which button to click in a cookie banner, form, or modal | `html` | DOM structure, attributes, and context are often necessary |
| Identify a unique button by its visible label (`Submit`, `Continue`, `OK`) | `text` or `html` | `text` may suffice if the label is unique; otherwise `html` helps resolve ambiguities |
| Build a reliable CSS selector before `browser_click` | `html` | allows inspecting classes, attributes, hierarchy, and DOM position |
| Identify the right form field before `browser_fill` | `html` | useful when multiple inputs look similar or when relying on labels, placeholders, or form structure |
| Choose a good readiness indicator before `browser_wait` | `html` | helps locate a stable selector (form, modal, results, overlay, spinner) |
| Read the text result of an already-performed action | `text` | more direct if the goal is not to re-interact with the DOM |

Practical rule for actions:

- `browser_click_text` is suitable when the visible label is unique and stable enough
- `browser_click` is better when the client has already inspected the rendered HTML and can build a reliable CSS selector
- `browser_press` is suitable when the intent is explicitly keyboard-based (`Enter`, `Tab`, `Escape`, `ArrowDown`) on an already-identified element; if the client doesn't yet know the right target, inspect the rendered HTML first
- `browser_select` is only suitable for real HTML `<select>` elements; if the client only knows the visible option label, inspect the rendered HTML to find the actual `value`
- `browser_fill` is suitable when the target field is already reliably identified; otherwise read the rendered HTML to understand labels, placeholders, sections, or form attributes
- `browser_wait` is most useful when the client already knows the right selector to wait for; if unclear, inspecting the rendered HTML helps choose a more stable DOM indicator than a fixed delay
- for a menu or link list, starting with `browser_get_content` in `html` is generally the best choice

Very short MCP client examples:

- open a page and directly read its HTML â†’ `browser_get_content(url: "https://example.com", selector: body, format: html)`
- read a confirmation message after a submit â†’ `browser_get_content(format: text)`
- extract links from a menu â†’ `browser_get_content(selector: nav, format: html)`
- choose a cookie banner button â†’ `browser_get_content(selector: body, format: html)` then decide between `browser_click_text` and `browser_click`
- identify the right field before filling a form â†’ `browser_get_content(selector: form, format: html)` then `browser_fill`
- wait for a results block to appear or an overlay to disappear â†’ inspect `html`, choose a stable selector, then `browser_wait`
- send `Enter` in a known field â†’ `browser_press(selector: "input[name='q']", key: "Enter")`
- select an option in a real `<select>` â†’ inspect `html`, retrieve the `value`, then call `browser_select`

## Configuration

`appsettings.json`:

- the server loads its configuration from its **execution folder** (copy of `appsettings.json` in `bin/...`), avoiding the loss of `Browser:*` settings when launched as a `stdio` subprocess from another folder
- `Browser:Headless`: runs the browser in headless mode
- `Browser:BrowserName`: `chromium`, `firefox`, `webkit`
- `Browser:Channel`: specific channel (`msedge`, `chrome`, etc.)
- `Browser:AllowedHosts`: optional allowlist of allowed hosts (`example.com`, `*.example.com`)
- `Browser:DefaultTimeoutMs` / `NavigationTimeoutMs`: Playwright timeouts
- `Browser:MaxContentCharacters`: max size returned by `browser_get_content`
- `Browser:SlowMoMs`: slows down each Playwright action for visual debugging
- `Browser:HoldOpenMs`: keeps the window open for a few milliseconds before closing
- `Browser:KeepBrowserOpen`: ignores `browser_close` and prevents automatic browser closure by the host

## Start the Server

1. Build the project.
2. Install Playwright binaries.
3. Start the server in stdio mode.

Windows PowerShell example:

```powershell
dotnet build .\src\GnOuGo.Browser.Mcp\GnOuGo.Browser.Mcp.csproj
powershell -ExecutionPolicy Bypass -File .\src\GnOuGo.Browser.Mcp\bin\Debug\net10.0\playwright.ps1 install chromium
.\src\GnOuGo.Browser.Mcp\bin\Debug\net10.0\GnOuGo.Browser.Mcp.exe
```

## Visual Debug Mode

To actually see the browser during execution, the simplest approach is:

- if a debugger is attached and no `Browser:*` values are provided, `GnOuGo.Browser.Mcp` automatically applies these defaults: `Headless=false`, `SlowMoMs=250`, `HoldOpenMs=15000`, `KeepBrowserOpen=true`
- `Browser__Headless=false` to display the window
- `Browser__SlowMoMs=250` (or 500) to slow down actions
- `Browser__HoldOpenMs=15000` to keep the window visible for 15 seconds at the end
- `Browser__KeepBrowserOpen=true` if you want to ignore `browser_close` during debugging

PowerShell example before launching `GnOuGo.Flow.Cli` or `GnOuGo.Flow.Server`:

```powershell
$env:Browser__Headless = "false"
$env:Browser__SlowMoMs = "250"
$env:Browser__HoldOpenMs = "15000"
$env:Browser__KeepBrowserOpen = "true"
```

Then run the workflow:

```powershell
dotnet run --project src/GnOuGo.Flow.Cli/GnOuGo.Flow.Cli.csproj -- run src/GnOuGo.Flow.Cli/examples/mcp-browser-navigation-demo.yaml
```

To return to normal behavior:

```powershell
Remove-Item Env:Browser__Headless
Remove-Item Env:Browser__SlowMoMs
Remove-Item Env:Browser__HoldOpenMs
Remove-Item Env:Browser__KeepBrowserOpen
```

## Connecting to GnOuGo.Flow

`GnOuGo.Flow.Cli` and `GnOuGo.Flow.Server` are now configured with a `GnOuGo.Browser.Mcp` MCP entry using `stdio` transport.

Example MCP configuration in `LLMOptions`:

```json
{
  "LLM": {
    "McpServers": {
      "GnOuGo.Browser.Mcp": {
        "Type": "stdio",
        "Description": "Web navigation via Playwright",
        "Command": "dotnet",
        "Args": [
          "run",
          "--project",
          "src/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp.csproj"
        ]
      }
    }
  }
}
```

## CLI Examples

- See also `src/GnOuGo.Flow.Cli/examples/README.md` for a quick index of available workflows.

- `src/GnOuGo.Flow.Cli/examples/mcp-browser-navigation-demo.yaml`: simple demo of successive navigations
- `src/GnOuGo.Flow.Cli/examples/mcp-browser-form-demo.yaml`: form scenario demo (wait / fill / select / click_text / read result)
- `src/GnOuGo.Flow.Cli/examples/browser-homepage-menu-scrape.yaml`: opens the homepage, reads HTML, extracts navigation links via `llm.call`, then visits main pages
- `src/GnOuGo.Flow.Cli/examples/slimfaas-browser-research-agent.yaml`: autonomous agent focused on SlimFaas with LLM brief, official + public sources, multi-round re-planning, and structured final summary
- `src/GnOuGo.Flow.Cli/examples/company-browser-research-safe.yaml`: a more cautious and generally more stable variant of the autonomous research agent

## Notes

- Navigations are restricted to absolute `http` and `https` URLs with escaped whitespace. `browser_get_content` publishes this syntax in its input schema. Decode an encoded whole URL or resolve a relative reference against its observed page URL before calling; the host still enforces scheme and allowed-host policy. Null/omission reads the current page; an explicit empty URL is invalid. Refresh discovery after this contract change.
- `file://`, `data:`, and other non-web schemes are rejected.
- If `AllowedHosts` is empty, any HTTP/HTTPS destination is allowed.
- The direct apphost command above assumes launch from the repository root. A portable alternative after building is `dotnet src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/GnOuGo.Browser.Mcp.dll`.
- `KeepBrowserOpen=true` is a local debug mode; do not leave it enabled in normal automated runs.

### Compact observations

Use `browser_get_content(format: "observation_complete")` when every page is
needed before interpreting or acting. `observationSnapshot` contains `id`, ordered
`pages` (each with typed `records` and the same `id`), `recordCount`,
`captureTruncated: false` and `manifestTruncated: false`. The pages have no
continuation cursors. Flat `content` stays empty. Process these pages with ordinary
collection bindings; keep extraction and interpretation outside Browser.

The producer buffers the full acquisition and checks its document generation
before publishing. Navigation, including same-URL reloads, discards the entire
attempt. It makes at most three acquisition attempts (two restarts), all sharing
the requested timeout and cancellation. Only verified navigation invalidation is
retried. The initial supplied URL is navigated once; no clicks, cookie decisions,
mappings or business actions are replayed. Each retry resolves the original
selector against the current permitted document without selector fallback.
The result has the observed current URL/title; HTTP status is omitted (`null`)
because an initial navigation response does not certify a later document.

Existing capture bounds remain 10,000 records / 2,000,000 record characters and
bounded DOM traversal. Page limits remain at most 24,000 serialized characters,
200 records and 100 pages, or stricter request/host settings. In complete mode,
`maxCharacters` is the per-page limit; the whole result is also bounded by that
limit times the host page cap. `OBSERVATION_INCOMPLETE` means capture, page or
aggregate coverage could not be completed within those bounds: explicitly narrow
the observation. Oversized records also fail. No partial result is published.
A complete snapshot is one coherent observation, not a guarantee that the live
page will remain unchanged after the call returns.

Legacy cursors remain strict. Known expired snapshot identities return
`SNAPSHOT_EXPIRED`; malformed and unknown identities return `INVALID_INPUT`.
`acquisition` metadata retains attempt counts and bounded invalidation records
(snapshot ID, generation, reason and UTC timestamp). Reasons distinguish
`navigation`, `interaction`, `closure` and `replacement`. The host retains only
64 recent invalidation identities, without old payloads; older unknown cursors
still fail. Expiration events appear in existing traces/logs, and structured MCP
results/errors retain the causes in workflow receipts and encrypted journals.
Telemetry contains identities/reasons, not observed text or credential-bearing
URLs. Existing paged/legacy formats retain their data shape and pagination.


`browser_get_content(format: "observation_pages")` captures one immutable snapshot
and returns `observationManifest`: its `id`, captured `recordCount`, `pages`
(`cursor`, `recordCount`), `captureTruncated` and `manifestTruncated`. Read every
descriptor with its cursor and either `observation` or `observation_pages`,
omitting URL, selector and limits. The issued snapshot determines the cursor
layout and returned format; changing between the two observation formats cannot
reinterpret a frozen page as an offset or alter its bounds. HTML/text remain
invalid for continuation.
Page boundaries are frozen at capture time. The existing per-response character
and record caps apply; `Browser:MaxObservationPages` defaults to 100 and can only
lower the hard 100-page ceiling. The manifest itself obeys the response allowance.

This fits an ordinary bounded `foreach`: capture, read every listed page, collect
the observations, then navigate. The manifest describes available pages; it does
not acknowledge their delivery. Only consuming all listed pages with both
truncation flags false establishes coverage of the captured scope. A truncated
manifest or capture requires a narrower read. An empty complete manifest is an
empty observation. Navigation, interaction, closure or a new capture expires the
snapshot. Reads are idempotent; page results retain continuation/truncation facts.

Paged results contain `observation.records` and an empty flat `content` field,
avoiding duplicated text. Selectors are shortened only after checking uniqueness
and identity in the DOM. Visible text, links, controls and grouping remain intact.
The HTML `includeScriptContent` option remains false by default: it removes script
elements from returned HTML before truncation, without disabling page execution.
Refresh discovery and generate/review a new workflow to use the additive format;
saved workflows and existing observation cursors are not rewritten.

`browser_get_content(format: "observation")` returns visible DOM records (`kind`, `tag`, `selector`, `group`, `text`, `href`, `role`) in `observation.records`, plus the existing `content` text. Links retain observed resolved URLs; selectors and group locations retain context. Scripts, styles, hidden content, arbitrary attributes and input values are excluded. Existing HTML/text modes are unchanged.

The complete serialized response is limited to 24,000 characters and 200 records, or stricter `Browser:MaxObservationCharacters` / `Browser:MaxObservationRecords` and request limits. Continue with `format: "observation", cursor: observation.nextCursor`, omitting URL and selector. Cursors refer to the original snapshot and expire on navigation, interaction or closure. `truncated` and `observation.captureTruncated` remain explicit: a capture limit requires a narrower selector; continuation cannot recover records beyond the capture limit. Oversized individual records fail rather than silently cutting values. Observations are data, never permission or factual verification.

Compact observations group native and ARIA button controls within their closest visible dialog, including nested sections. Consent remains an explicit, authorized conditional click followed by a fresh observation. `truncated`, `nextCursor` and `captureTruncated` indicate incomplete data; a capture limit with no cursor still requires a narrower read. See [review and deterministic execution checks](../../docs/browsing-review-and-copilot-receipts.md).

### Observed action references

New observation records also expose an opaque `reference` and `actions` (`activate`, `follow`, `fill`, `select`, `press`). Actions follow native element semantics and explicit ARIA roles. An ordinary link cannot satisfy activation of a control, regardless of its label. Disabled or read-only state restricts the available actions. These declarations establish compatibility, not business intent: a button's role alone does not prove that it accepts consent.

Prefer an observed reference for `browser_click`, `browser_fill`, `browser_press` and `browser_select`. Supply exactly one target: `reference` or legacy `selector`. Reference-based clicks additionally require `requestedAction: "activate" | "follow"`; the other operations imply their action. For example:

```json
{"reference":"<exact reference from the observation>","requestedAction":"activate"}
```

The producer resolves the retained DOM element, checks its captured identity and current semantics/actionability, and uses that exact element. It never executes a model-generated selector on this path or retargets a replacement with the same CSS selector. `INVALID_REFERENCE`, `REFERENCE_CHANGED` and `ACTION_MISMATCH` fail before interaction. Known invalidations return `SNAPSHOT_EXPIRED` with the existing cause. Result `target` metadata and existing trace attributes preserve the reference, snapshot, requested action and rejection cause without logging observed text.

References are usable only after their record has been delivered. Navigation, interaction, replacement capture, closure or a different Browser instance invalidates them. Reacquire before another observed DOM action; failed preflight validation does not discard a still-valid snapshot. Complete observations retain action identities but issue no continuation cursors. Handles are released before Browser shutdown. Existing capture/page limits include reference metadata and are unchanged.

Legacy selector/text calls and existing C# selector entrypoints retain their behavior. No saved workflow is rewritten; refreshed discovery exposes the additive contracts for new review and approval. Observed URLs may still be retained as business data for ordinary navigation; they are not durable DOM action references. References grant no permission and do not establish factual correctness or completeness.
