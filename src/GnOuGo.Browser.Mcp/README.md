# GnOuGo.Browser.Mcp

`GnOuGo.Browser.Mcp` is a **stdio** MCP server based on **Playwright.NET**. It exposes a set of web navigation tools usable from `GnOuGo.Flow` or any compatible MCP client.

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
dotnet run --project .\src\GnOuGo.Browser.Mcp\GnOuGo.Browser.Mcp.csproj
```

## Publish and release-size notes

Published desktop/server bundles install Playwright browsers beside the executable under `ms-playwright/`.
To keep GitHub release archives small, the publish target installs only the Chromium headless shell by default:

```powershell
dotnet publish .\src\GnOuGo.Browser.Mcp\GnOuGo.Browser.Mcp.csproj -c Release -r win-x64 --self-contained true
```

This matches the default MCP configuration (`Browser:Headless=true`) and avoids shipping the full headed Chromium bundle.
If a package explicitly needs headed Chromium, opt back in at publish time:

```powershell
dotnet publish .\src\GnOuGo.Browser.Mcp\GnOuGo.Browser.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishedPlaywrightChromiumInstallArgs=chromium
```

Use the executable self-test to verify that the bundled Playwright driver and Chromium headless shell still launch correctly:

```powershell
.\bin\Release\net10.0\win-x64\publish\GnOuGo.Browser.Mcp.exe --self-test
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

- Navigations are restricted to `http` and `https`.
- `file://`, `data:`, and other non-web schemes are rejected.
- If `AllowedHosts` is empty, any HTTP/HTTPS destination is allowed.
- The command `dotnet run --project src/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp.csproj` assumes launch from the repository root in development.
- `KeepBrowserOpen=true` is a local debug mode; do not leave it enabled in normal automated runs.
