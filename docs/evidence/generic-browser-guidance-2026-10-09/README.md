# Generic Browser instructions

Continue PR #117 from `8efd78a8`. The retained live stopped after an interpretation classified observed notice text as a blocker. Inspection also found consent instructions in the Browser tool description, the evaluation harness, the planning skill and runnable examples. A tool description participates in discovery and model context even when the Browser implementation performs no automatic click.

## Removed active instructions

- `browser_get_content` describes observations, references and explicit authorized interactions without recommending a consent sequence. The published argument/result schemas and runtime behavior are unchanged.
- The live harness keeps the original business request, selected product bound, fixed workbook destination, truthful data/errors and cleanup. It no longer adds acceptance/refusal/customization instructions, information-link exceptions or a notice-classification step. The prompt-building method used by the real harness is tested directly at one and ten products.
- The planning skill and current compilation/Browser documentation no longer make consent handling a standard responsibility. Operations follow the user request and available contracts.
- The three CLI research/navigation examples no longer suggest banner clicks, classify banners as access failures or exclude links by the cookie/privacy/legal category. Existing navigation relevance and host/URL constraints remain.

There is no keyword prohibition in the planner or validator. Page observations remain complete, including notices, and explicit user-requested interactions remain supported. The existing consent fixtures are retained as realistic data and explicit-action tests. HTTP cookie protections in ProxyCopilot are unchanged.

## Regression boundaries

Actual MCP stdio discovery must return the updated description. The same transport tests continue enforcing `activate` versus `follow`, structured rejection before interaction, durable receipts and recovery. The local Browser/Document fixture adds two informational notices with different text: a cookie notice and a generic service notice. Both remain visible in the acquired content while the unchanged business plan visits the product pages, writes a real workbook and passes exact independent cell/path/cleanup checks. Captured MCP calls must contain no click, fill, press or select; the notice action endpoint must never be visited.

These deterministic adapters establish contract and execution behavior. They do not claim that a future provider response will necessarily choose the correct business actions; newly generated artifacts still require review.

## Validation results

**193 tests passed, zero failures or skips**, with `-warnaserror`: all 136 Browser tests and 57 affected host tests covering live prompt/approval, real MCP discovery, action compatibility, encrypted receipt recovery and the complete local product matrix. The two new notice variants each reach review in one deterministic planning call with zero repairs and two discovery reads. Both execute four deterministic inference-adapter calls, visit both product pages, produce the exact three-row workbook (header plus two products), verify its returned path and close the Browser. The captured notice remains visible; no interaction tool is called. The notice-cookie execution takes 759.4 ms and notice-generic 702.1 ms after planning. The host selection completes in 13 min 28 s; the full Browser suite in 17 s. See [individual test results](tests.json).

The planning skill validator passes. The CLI build completes with zero warnings/errors; the host build also completes its existing frontend build. No full-solution rerun, package or AOT campaign is claimed for this instruction-only production change.

Commands:

```sh
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj --no-restore -m:1 -warnaserror --filter 'FullyQualifiedName~LiveWorkflowReviewTests|FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~RealStdioContractsAndLocalBrowserExecutionProduceIndependentlyVerifiedWorkbook'
dotnet test tests/GnOuGo.Browser.Mcp.Tests/GnOuGo.Browser.Mcp.Tests.csproj --no-restore -m:1 -warnaserror -p:BuildProjectReferences=false
dotnet build src/GnOuGo.Flow.Cli/GnOuGo.Flow.Cli.csproj --no-restore -m:1 -warnaserror -p:BuildProjectReferences=false
```

The host build first rebuilt the changed Browser project; the parallel Browser test/CLI builds reused those compiled references. An initial dependency download was cancelled before tests started, then the exact pinned Copilot 1.0.88 build cache was reused after SHA-256 verification. No dependency version, timeout or inference allowance changed.

## Historical and live state

No paid call, campaign mutation, old invocation replay, saved artifact change, permission expansion or limit increase is part of this correction. Refresh Browser discovery for new planning sessions; changed descriptions follow existing contract fingerprinting. Historical requests, receipts, schemas, approvals and journals retain their original contents. The prior failed run remains failed and its reservations remain retained. PR #117 stays draft.

## Example validation limitations

All three edited YAML examples parse. The current CLI reports exactly the same validation diagnostics as the baseline at `8efd78a8`: five for the homepage example, six for the safe research example and seven for the research agent. They lack the required top-level `skill` block and strict object-schema `additionalProperties: false` declarations. This cleanup does not migrate those examples or weaken validation. The CLI currently returns exit code zero despite printing these errors; validation was assessed from the diagnostics, not that exit code. See `examples.json` for the before/after comparison.

The previous remote CI failures are recorded separately in [the retained execution evidence](../typed-repair-vocabulary-2026-10-09/ci-after-execution.json): six host fixture variants exceeded their shared elapsed inference allowance during planning. No timeout or test assertion is relaxed here.
