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

The final packaged artifacts passed the native workflow on all three runners. Independent readback verified each executable, each inventory, every referenced notice and all sixteen corresponding MPL source archives by digest. The distributions contain 185 components on Linux, 185 on Windows and 187 on macOS. [Package fingerprints and counts](distribution-verification.json) · [Download the qualified distributions](https://github.com/GnouGo/GnouGo/actions/runs/46137668704)

The Linux dependency inventory covers 185 resolved components. Distribution packages produced by the current build include the exact source archives for 16 MPL-licensed Symphonia crates, with Cargo.lock SHA-256 verification, and the content-addressed third-party notices. [Linux component inventory](linux-components.json) · [Distribution documentation](../../deckcraft-and-vision.md)

## GnOuGo package and runtime checks

Windows x86-64, Linux x86-64 and macOS ARM64 each passed eight tests against DLLs extracted from the actual AI.Core, Flow.Core and Flow.Integrations NuGet packages. Independent readback verified the packages, assembly digests, native workbooks and test reports on all three targets. Each downloaded CLI distribution also contained its referenced notices and the sixteen matching MPL source archives. [Matrix receipts](packaged-runtime-matrix.json) · [Download the tested packages and presentations](https://github.com/GnouGo/GnouGo/actions/runs/46144498783)

The package check verified provider capability rejection, image bytes in the HTTP request, mixed MCP content, two distinct images across business-agent decisions, model-specific budget reservations and the native presentation round trip. The local Release build completed with zero warnings. A self-contained, trimmed Windows Server executable passed encrypted planning persistence and exact receipt recovery. Its publish log contained EF experimental-feature diagnostics and one IL2101 from the ASP.NET Core framework. [Suite counts, package digests and smoke evidence](local-validation.json)

The hosted planner validation on `4160c8ec` completed successfully. Its five Server test reports covered every discovered case once, with 2,843 passes and one browser smoke skipped because the runner had no configured Playwright module for that test. The same browser test subsequently passed on Windows against a disposable Server and real Chromium. [Hosted planner and Server workflow](https://github.com/GnouGo/GnouGo/actions/runs/46144498707) · [Coverage and Windows browser receipt](local-validation.json)

The full Windows Server run reported 2,840 passes and three opt-in skips in 1 h 52 min. Both local Copilot retry cases and the Chromium clarification case passed when rerun with their fixtures enabled. The three-OS matrix and the hosted planner suite also passed on evidence commit `8863293c`. [Windows results and binary hashes](local-validation.json) · [Hosted planner checks](https://github.com/GnouGo/GnouGo/actions/runs/46147374991) · [Packaged native checks](https://github.com/GnouGo/GnouGo/actions/runs/46147374969)

## Blind visual control

Two run-owned images went through the workflow runtime, encrypted artifact store and OpenAI adapter. The prompt asked for the upper-right code and visible problems without giving the expected results. The model read `N7K-492` in the unobstructed panel. For the other panel it returned null and identified a major header overlap. Each successful receipt identifies the bytes sent by SHA-256. [Model output and image receipts](blind-vision.json) · [Control and fixtures](../../../tests/GnOuGo.Agent.Planning.Benchmark/DeckCraft)

## Eight-slide Server attempt

The normal Server planner stopped at revision 275 after 104 physical requests. It retained 47 top-level draft tasks. During planning, the model could discover the renderer but could not establish from its published runner description that a subsequent model decision receives the rendered pixels and their SHA-256 references. The runner description now exposes that behavior. The workflow had not reached approval or form execution. The eight-slide before/after presentations and their visual findings are outstanding. [Session and draft task IDs](deckcraft-planning-attempt-a.json)

The final request received HTTP 200 headers and part of the native Chat Completions stream. The 270-second timeout expired before a completion or usage receipt arrived. The captured policy permits zero uncertain retries. An authenticated GET for the exact completion ID on the original deployment returned HTTP 404. The journal retains the attempt and its EUR 0.388440 reservation. The Amazon assessment was subsequently refused before dispatch because the campaign shares this unresolved request. [Transport and retrieval evidence](deckcraft-planning-attempt-a.json)

On 2026-11-28 at 16:16 UTC the operator permanently closed this exact planning attempt. A fresh preview matched the previously inspected session, revision, request, HTTP journal, failure, retry policy and accounting. Its run-record hash had changed, so the closure bound the freshly verified evidence hash. A separate read found exactly one matching authorization. The closure keeps the draft and the EUR 0.388440 reservation, forbids replay and permanently ends continuation of this session. The post-closure financial audit matched the pre-closure audit. [Closure receipt](deckcraft-planning-attempt-a.json)

This continuation added 106 physical attempts and EUR 3.730175 of known usage. Across all runs the campaign recorded 1,782 physical attempts, 27,070,266 known input tokens and 892,591 known output tokens. Reported usage at the captured tariffs accounts for EUR 65.729950. Three HTTP attempts retain EUR 0.863472 in total. The separate application hold is EUR 0.046355. The remaining balance under the original EUR 70 ceiling is EUR 3.360223. The additional EUR 30 authorization has not been applied. [Exact accounting](accounting.json)

## Campaign history

The retained Amazon form reached a first-attempt budget refusal before its capture assessment. The encrypted HTTP journal contains zero physical attempts for that request. The reconciliation retained its accounting and the workflow remains separately traceable. [Refusal evidence](amazon-zero-attempt.json) · [Opening and control accounting](accounting.json)

The previously completed SlimFaas review remains available with its independent verification. [Server execution](../pr120-planner-improvement-20261127/server-code-9.json) · [SlimFaas verification](../pr120-planner-improvement-20261127/slimfaas-review-verification.json)

The encrypted form journal retained the exact refused prompt. Its SHA-256 matches the request identity. The complete text projection covers all 72 pages and 1543 records, reducing 1675315 UTF-8 bytes to 43082 bytes. This inspection dispatched zero model calls. [Projection receipt](amazon-retained-projection.json)
