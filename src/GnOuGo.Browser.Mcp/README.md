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
- a permitted `url` opens the owned browser/page when needed; no existing tab is required. Omitting `url` reads an already active page. The tool description states this lifecycle in its opening sentence so discovery can distinguish the two forms.
- use `format: text` to summarize a page, read visible content, extract readable text, or feed a synthesis
- use `format: html` whenever you need to preserve the DOM structure or attributes, e.g., to extract menu/navigation links, retrieve `href`/`src`, inspect buttons, forms, tables, or decide which element to click on the MCP client side
- 
- `format: html` strips `<script>` elements by default before returning content, which keeps pages such as Amazon compact and avoids sending large inline JavaScript/state blobs to MCP clients
- set `includeScriptContent: true` only when debugging raw page scripts or when script tags are explicitly needed
- for a menu, header, or form, it is generally better to target a specific selector (`nav`, `header`, `form`, etc.) with `format: html` rather than `text`, otherwise useful URLs and attributes will be lost
- robustness note: when a requested content selector is temporarily unavailable, `browser_get_content` falls back to `body` then `html` and returns `resolvedSelector` / `fallbackApplied` metadata in the result

### Goal â†’ Recommended Format

| MCP client goal | Recommended format | Why |
|---|---|---|
| Summarize a page or read its visible content | `text` | simpler, more compact, closer to readable rendering |
| Extract menu, header, footer, or navigation links | `html` | plain text loses `<a>` tags and `href` values |
| Decide which button to click in a form or modal | `html` | DOM structure, attributes, and context are often necessary |
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
- `Browser:AllowedNavigationUrlPatterns`: optional full-URL regular expressions, combined with the host allowlist; each explicit read and redirect destination must match one pattern. The same pattern must authorize both the supplied absolute URL and its transmitted URI form: removing an explicit default port or collapsing path segments cannot grant route authority. An omitted HTTP path retains its ordinary `/` request target. Absolute redirect spelling is checked before canonicalization and before contact. These are trusted host settings, never model-authored permission grants.
  Canonically escaped literal patterns use an equivalent ordinal string comparison, so long exact URLs do not require a regex automaton. Other patterns retain the bounded nonbacktracking engine. This changes no URL spelling, host restriction, redirect rule or permission.
- `Browser:ObservedNavigationSourceUrlPatterns` / `ObservedNavigationEntryUrlPatterns`: optional trusted pattern lists, disabled by default and validated together. A successful complete whole-document observation of a matching source may authorize explicit content reads of exact, fragment-free observed link hrefs matching an entry pattern, including a different host. Every redirect still requires a base-policy route or exact current observed entry. Interaction tools gain no additional authority. This per-Browser scope expires on close/disconnect, a new matching source navigation or failed read; another Browser cannot borrow it. Partial, selector-scoped and failed observations grant nothing. The original records, base allowlists and URL spelling remain unchanged. A configuration change requires the host's ordinary permission review; model arguments cannot enable this capability.
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

Links and headings retain visible text, with their explicit `aria-label` as a
fallback when that text is empty. Explicit ARIA `heading` elements are observed
alongside native headings. Hidden elements remain excluded. Labels describe the
observed element; they do not establish product identity, destination provenance
or authority. Link URLs, nearest requested ancestor attributes and DOM order retain
their existing meaning.

Semantic repeated containers retain their observed tag, explicit ARIA role and
snapshot-local first-occurrence index in each record's `groupContext`. Native
`article`, `li`, `tr` and ARIA `listitem`, `article`, `row` containers take precedence
over broad regions; visible dialog ownership remains first. Repeated links inside
one occurrence remain associated, while distinct occurrences with identical URLs
remain distinct. DOM order, original resolved hrefs, exact selectors and existing
snapshot-bound record references are preserved. These facts do not identify a
business product or establish that a link's eventual destination was visited.
Each record also exposes its outer semantic `occurrence` (selector, tag, role and snapshot-local index), bounded by the nearest landmark/dialog. This retains the relationship between nested variant links and their containing item. `heading` identifies an enclosing or linked heading selector when present. Neither field assigns business meaning to that item.

Explicit content reads return `navigationEvidence` only for a response bound to
the actual committed main document. It records the requested URL, actual final
URL and ordered observed HTTP response hops, including raw Location headers.
Each GET is sent once without transport retries or automatic redirects. The host
checks a resolved observed redirect destination before contacting it and starts
the next browser navigation under the same deadline. Loops and more than twenty
redirects stop before sending the next request; failures retain partial hops and
never claim a final destination. A Playwright interruption also retains only the
read's already observed hops, with `complete: false` and no final URL, through
both ordinary content and complete-snapshot failure results. The existing error
code and acquisition failure category remain; this evidence authorizes no retry
or acceptance. A history-only address change retains the same document's actual method, status and response chain and reports the changed address separately as `documentUrl`; it never invents an HTTP redirect. If the page replaces its document with another guarded GET response for the identical terminal URL during that read, the host verifies the current request's already observed response. Earlier responses appear separately in `supersededResponses`, preserving any real redirect prefix. This metadata lookup dispatches no navigation or interaction; a different URL or POST cannot borrow an earlier response. A new explicit fragment-only navigation cannot borrow an earlier response. An eligible wholly empty-document reload remains a distinct acquisition
attempt with its own response identity and the existing shared bounds.

This uses Playwright's existing request transport because its
[routing callback does not intercept every automatic redirect](https://playwright.dev/dotnet/docs/network#redirects).
Service workers are blocked so they cannot evade this interception. With a host
navigation policy, other navigations are checked too; an interaction's redirect
stops and requires an explicit content read rather than replaying that interaction.
Subresource reads remain ordinary browser requests. No scenario-specific route,
business interpretation, destination rewrite or permission expansion is introduced.
Denied child-frame navigations remain blocked before contact and do not replace the main document's acquisition outcome. External delegated route guards still apply before any Browser dispatch.

A main-frame request does not replace the current document until it commits.
Denied or aborted requests retain that document's actual response evidence and
clear only their own pending request. A later history address change remains
bound to the same committed response. New document commits still replace the
evidence and invalidate snapshots; explicit reads retain their own independent
navigation outcome. This grants no authority to contact a denied destination.

Use `browser_get_content(format: "observation_complete")` when every page is
needed before interpreting or acting. `observationSnapshot` contains `id`, ordered
`pages` (each with typed `records` and the same `id`), `recordCount`,
`captureTruncated: false` and `manifestTruncated: false`. The pages have no
continuation cursors. Flat `content` stays empty. Process these pages with ordinary
collection bindings; keep extraction and interpretation outside Browser.

The producer buffers the full acquisition and checks its document generation
before publishing. Navigation, including same-URL reloads, discards the entire
attempt. It makes at most three acquisition attempts (two restarts), all sharing
the original timeout and cancellation. Every attempt resolves the original
selector against the current permitted document without selector fallback.

An explicit URL acquisition can reload **once** when its captured document has
zero records, an empty title and no meaningful body content. This requires the
current document's verified successful GET response (excluding 204/205), with no
intervening unsafe navigation. The discarded snapshot is invalidated completely;
reload consumes an acquisition attempt and resets no allowance. Reads of the
current page, submitted documents, HTTP errors and unknown navigation methods
never trigger this recovery. Clicks, fills, submissions and business actions are
never replayed. Empty selected regions in populated documents and non-text content
remain valid observations; emptiness is not inferred from missing business fields.

If the document remains unusable, or reload is ineligible, `OBSERVATION_EMPTY`
returns a structured MCP error without a usable snapshot. This does not establish
CAPTCHA, missing products or successful zero-result search. Normal failure handling
and cleanup apply after the error receipt is durable; downstream interpretation
and normal output writing must not run. Historical receipts are not reclassified.

Optional `acquisition.navigation` identifies the current observed document and its
verified HTTP status; status stays unknown when no matching response was observed.
`lastResponse` retains the latest main-navigation HTTP response, including responses
that never committed a document (for example 204). They are not interchangeable.
`recoveries` records the discarded snapshot/generation, `empty_document` reason,
`reloadRequested` flag and the navigation evidence at that time. A requested reload
is not a claim that it completed. The top-level URL/title/status describe the final
observed document, including on failure when available. Old acquisition records
without these optional fields remain readable; refresh discovery for the additions.

Complete acquisitions also retain optional `phase`, `elapsedMilliseconds` and
`timingsMilliseconds` diagnostics. The finite phases distinguish startup,
navigation, readiness, selector resolution, capture (including transfer and
document metadata), pagination, publication and the one eligible reload. Times
accumulate across attempts under the same deadline. A timeout identifies its
phase; a navigation invalidation is retained even when timeout/cancellation wins
before expiration handling. These fields also survive MCP error receipts and
encrypted recovery. Trace events contain phase names, attempts and elapsed times,
never observed content or URLs. They do not establish business completeness or
authorize retry. Historical receipts without this evidence cannot identify the
phase that timed out; a visibly rendered page alone does not establish that a
complete, coherent observation was acquired.

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
`navigation`, `interaction`, `closure`, `replacement` and `empty_document`. The host retains only
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

Compact observations group native and ARIA button controls within their closest visible dialog, including nested sections. Reading content never clicks controls. Interactions require an explicit authorized action followed by a fresh observation. `truncated`, `nextCursor` and `captureTruncated` indicate incomplete data; a capture limit with no cursor still requires a narrower read. See the [historical review and execution checks](../../docs/browsing-review-and-copilot-receipts.md); their scenario-specific examples are not current tool instructions.

### Observed action references

New observation records also expose an opaque `reference` and `actions` (`activate`, `follow`, `fill`, `select`, `press`). Actions follow native element semantics and explicit ARIA roles. An ordinary link cannot satisfy activation of a control, regardless of its label. Disabled or read-only state restricts the available actions. These declarations establish compatibility, not business intent: a button's role alone does not establish that it performs the requested action.

Prefer an observed reference for `browser_click`, `browser_fill`, `browser_press` and `browser_select`. Supply exactly one target: `reference` or legacy `selector`. Reference-based clicks additionally require `requestedAction: "activate" | "follow"`; the other operations imply their action. For example:

```json
{"reference":"<exact reference from the observation>","requestedAction":"activate"}
```

The producer resolves the retained DOM element, checks its captured identity and current semantics/actionability, and uses that exact element. It never executes a model-generated selector on this path or retargets a replacement with the same CSS selector. `INVALID_REFERENCE`, `REFERENCE_CHANGED` and `ACTION_MISMATCH` fail before interaction. Known invalidations return `SNAPSHOT_EXPIRED` with the existing cause. Result `target` metadata and existing trace attributes preserve the reference, snapshot, requested action and rejection cause without logging observed text.

References are usable only after their record has been delivered. Navigation, interaction, replacement capture, closure or a different Browser instance invalidates them. Reacquire before another observed DOM action; failed preflight validation does not discard a still-valid snapshot. Complete observations retain action identities but issue no continuation cursors. Handles are released before Browser shutdown. Existing capture/page limits include reference metadata and are unchanged.

Legacy selector/text calls and existing C# selector entrypoints retain their behavior. No saved workflow is rewritten; refreshed discovery exposes the additive contracts for new review and approval. Observed URLs may still be retained as business data for ordinary navigation; they are not durable DOM action references. References grant no permission and do not establish factual correctness or completeness.

Post-action `domcontentloaded` and `load` waits subscribe before the interaction and recheck the current document's lifecycle state. A fast navigation cannot lose its completion notification between a state check and event registration. Reloads invalidate previously observed readiness; same-document navigation retains the document's established lifecycle. Delayed readiness, cancellation, page closure and genuine timeouts still fail explicitly. The existing timeout is shared by readiness rechecks, and the interaction is never repeated. Explicit `networkidle` continues to require Playwright's network-quiescence condition. This does not alter target identity, `follow`/`activate` compatibility, permissions or snapshot expiration.
`Browser:ObservationAncestorAttributes` optionally names up to eight DOM attributes
to retain on observed links. Each entry identifies the nearest matching ancestor
inside the selected root (including the link), with its exact value, tag, role and
snapshot-local source index. Missing and empty values stay distinct. Different
containers retain different indexes even when their attribute values match. No
attribute is requested by default. Names are bounded to 64 characters; values over
1,024 characters or ancestry beyond 64 levels mark the capture incomplete. Existing
record/page/byte limits apply. This metadata records source DOM facts and performs
no business classification or URL construction.
