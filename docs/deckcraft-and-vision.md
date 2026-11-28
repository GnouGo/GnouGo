# DeckCraft presentations and workflow vision

GnOuGo uses an external DeckCraft process for PowerPoint creation, editing, export and slide rendering. A run owns its process and a private workspace. Dispose the run's ConfiguredMcpClientFactory to close the stdio client and child process. Create a fresh factory and workspace for each run, including parallel runs and separate tenants. Keep the approved executable outside writable workspaces. Use the service account or container to constrain environment and file permissions.

## Installation

Install Git, Python 3.11 or later and the Rust toolchain for your host. From the repository root run the following in a terminal with the native C/C++ linker available. The output directory must be new.

```
rustup toolchain install 1.100.1 --profile minimal
rustup override set 1.100.1
python packaging/deckcraft/build-cli.py qualified-cli
```

Set RUSTUP_TOOLCHAIN=1.100.1 if a Rust override applies to the source directory outside your checkout. The build uses the pinned Cargo.lock and the source revision 4a980c33ddf37eb64157777fcaeab6d19baa5878 (v0.19.1). It applies the patch in packaging/deckcraft and builds only deckcraft-cli. The installed directory contains the executable, installation.json with exact toolchain and binary digest, components.json with the resolved dependency graph for that platform, and the corresponding notices. The PR qualification matrix exercises Windows x64 (MSVC), macOS ARM64 and Linux x64 (GNU). For upstream release diagnostics, scripts/install-deckcraft.py verifies the release archive digest and extracts the stock CLI. Its platform table lists the other published archives. The stock CLI omits the embedded workbook for the native column-chart edit in this scenario. [Upstream release](https://github.com/storytold/deckcraft/releases/tag/v0.19.1) · [Chart export issue](https://github.com/storytold/deckcraft/issues/1047)

To repeat the distribution checks on a clean checkout, set `GNOUGO_DECKCRAFT_CLI` to the qualified binary and run `python packaging/deckcraft/test-distribution.py <empty-output-directory>`. The script packs the three GnOuGo libraries, installs their assembly payloads in the isolated test hosts and records each package digest, assembly digest and test result. Set `GNOUGO_DECKCRAFT_EVIDENCE` to preserve the native PowerPoint control files.

## MCP configuration

Use DeckCraftMcp.ForWorkspace with the absolute executable path and an existing, run-owned directory. Pass the resulting McpServerOptions to ConfiguredMcpClientFactory. An equivalent single-run server entry is shown below. Use absolute paths appropriate for the host.

```json
{
  "Type": "stdio",
  "Command": "/opt/gnougo/qualified-cli/deckcraft-cli",
  "Args": ["mcp", "--automation-read-root", "/work/run-123", "--automation-write-root", "/work/run-123"],
  "DiscoveryTimeoutSeconds": 30,
  "CallTimeoutSeconds": 60
}
```

Use mcp.list for the approved server and mcp.call for the specialized operations. Both sides negotiated 2025-06-18 in the Windows release smoke. After insert_chart, discover chart.data with list_commands and its exact filter. A batch stops on its first error. Inspect the current document before retrying a mutation. After cancellation or transport loss, reconcile the file and action receipt before granting another attempt. Save, reopen the exact file, then call inspect_slide and render_slide for final verification. Use verify-chart.py to follow the chart relationship into the embedded workbook and compare its cells with chart caches and formulas. The workbook patch handles ordinary single-level native column charts with aligned data. [DeckCraft MCP contract](https://github.com/storytold/deckcraft/blob/4a980c33ddf37eb64157777fcaeab6d19baa5878/docs/mcp.md)

## Image inputs and outputs

llm.call and the business runner for agent.run accept an images array alongside text. Each element can be an inline PNG or JPEG object with mimeType and base64 data, or a previously observed reference containing sha256, mimeType, width, height and size. The runtime validates headers, bounds and digest, stores bytes under the tenant and run, and transmits image parts through a provider that declares vision capability. A file name in a prompt supplies text only. A durable host uses EncryptedWorkflowRunStore for image storage. A custom host can supply IWorkflowImageStore. Current limits are eight input images, 4 MiB per image, 8192 pixels per dimension and 16 megapixels per image.

mcp.call exposes an images array when the result contains images. Bind the entire array to the next vision step, for example `images: ${data.steps.render.images}`. Each distinct digest appears once in provider order. The step also keeps the structured response and projects mixed blocks into content_blocks. An image block contains an image reference. An embedded image resource carries its reference in resource.image. The business agent can request a previously observed image by digest. A successful llm.call returns inspected references under meta.images. For visual review, request a structured result with the slide or region, observed problem, severity, proposed correction and uncertainty. Match that receipt to the rendered slide digest. Check native content and public output separately.

For a finite token or cost budget, the runtime and campaign journal use the same pre-dispatch reservation. OpenAI models with implemented, published patch or tile rules use the validated dimensions of each transmitted image and a conservative UTF-8 text bound. For other models, including the qualified `gpt-6.1-terra` route, the reservation uses the declared input allowance. Provider-reported input and output usage settle a completed call. [OpenAI image token rules](https://developers.openai.com/api/docs/guides/images-vision#calculating-costs)

The qualification evidence includes native round trips for three OS targets, a Microsoft PowerPoint edit and a blind two-image review. [PR 120 evidence](evidence/pr120-deckcraft-vision-20261128/README.md)

## Distribution notices

DeckCraft code is MIT OR Apache-2.0. The packaged NOTICE and ATTRIBUTION.md list original artwork, Lucide ISC and Feather-derived MIT icon paths. The headless source build omits the optional craft-fonts checkout. The dependency font bundle includes Ubuntu Light (Ubuntu Font Licence), Hack (MIT/Bitstream Vera), Noto Emoji (OFL) and the egui/emoji icon fonts. Platform font availability can change glyphs and wrapping. The PDF dependency includes CC0 ICC profiles. Official upstream release archives can embed additional OFL fonts and carry their own font notices. [Pinned asset inventory](https://github.com/storytold/deckcraft/blob/4a980c33ddf37eb64157777fcaeab6d19baa5878/ATTRIBUTION.md)

The resolved Symphonia crates are MPL-2.0. Each components.json entry includes the exact corresponding source archive and its Cargo.lock SHA-256. Distribute the inventory and license notices with the executable and keep the corresponding source available to recipients. The chart change applies to DeckCraft's writer. The headless package excludes the desktop application and ArtCraft brand artwork. The separate terms for ArtCraft marks travel with the distribution. [Symphonia source](https://github.com/pdeljanov/Symphonia) · [MPL FAQ](https://www.mozilla.org/en-US/MPL/2.0/FAQ/) · [ArtCraft brand terms](https://github.com/storytold/deckcraft/blob/4a980c33ddf37eb64157777fcaeab6d19baa5878/docs/brand/LICENSE-brand.txt)
