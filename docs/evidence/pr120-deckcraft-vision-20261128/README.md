# PR 120 DeckCraft and vision qualification

DeckCraft v0.19.1 uses upstream commit `4a980c33ddf37eb64157777fcaeab6d19baa5878`. The stock Windows portable executable (`49ca3a3d2f5d7f668cd730e5fe65528af12cfef035748f8a3eb465a2a6721d0c`) negotiated MCP 2025-06-18 and exposed 25 tools. Editing native chart data removed the embedded workbook. The source patch in this PR restores the worksheet, formulas and relationships for the tested native column-chart layout. [Upstream issue](https://github.com/storytold/deckcraft/issues/1047) · [Patch and build](../../../packaging/deckcraft)

## Native PowerPoint controls

Each OS directory contains the executable fingerprint, transport receipt, initial and edited PPTX, independent OOXML checks and a PNG rendered by DeckCraft after reopening the edited file. Both the chart caches and embedded workbook contain A=4 and B=7 initially, then A=4 and B=11. The French title also changed. The files each contain one slide. [Hosted build and tests](https://github.com/GnouGo/GnouGo/actions/runs/46134383566) · [Distributable CLI packages](https://github.com/GnouGo/GnouGo/actions/runs/46135297812)

| Runner | Architecture | Retained files |
| --- | --- | --- |
| Windows Server 2025 | x86-64 MSVC | [Windows](windows-2025) |
| macOS 15 | arm64 | [macOS](macos-15) |
| Ubuntu 24.04 | x86-64 GNU | [Linux](ubuntu-24.04) |

On Windows, Microsoft PowerPoint 16.0 build 20625 opened the edited presentation, exposed the native chart’s worksheet, saved an edit to B3 and read back 13 after reopening. [Desktop receipt](powerpoint.json) · [PowerPoint-edited copy](powerpoint-edited.pptx)

The Linux dependency inventory covers 185 resolved components. Distribution packages produced by the current build include the exact source archives for 16 MPL-licensed Symphonia crates, with Cargo.lock SHA-256 verification, and the content-addressed third-party notices. [Linux component inventory](linux-components.json) · [Distribution documentation](../../deckcraft-and-vision.md)

## Blind visual control

Two run-owned images went through the workflow runtime, encrypted artifact store and OpenAI adapter. The prompt asked for the upper-right code and visible problems without giving the expected results. The model read `N7K-492` in the unobstructed panel. For the other panel it returned null and identified a major header overlap. Each successful receipt identifies the bytes sent by SHA-256. [Model output and image receipts](blind-vision.json) · [Control and fixtures](../../../tests/GnOuGo.Agent.Planning.Benchmark/DeckCraft)

## Campaign history

The retained Amazon form reached a first-attempt budget refusal before its capture assessment. The encrypted HTTP journal contains zero physical attempts for that request. The reconciliation retained its accounting and the workflow remains separately traceable. [Refusal evidence](amazon-zero-attempt.json) · [Opening and control accounting](accounting.json)

The previously completed SlimFaas review remains available with its independent verification. [Server execution](../pr120-planner-improvement-20261127/server-code-9.json) · [SlimFaas verification](../pr120-planner-improvement-20261127/slimfaas-review-verification.json)
