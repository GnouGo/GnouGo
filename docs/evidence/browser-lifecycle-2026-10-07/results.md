# Browser lifecycle correction

The retained `follow` failure is a lost lifecycle notification, not an incompatible target or failed navigation. Two local probes reproduced the 30-second timeout on baseline `e9934813`. In the second, `/help` was reached, DOMContentLoaded and load both fired, and the current document was complete. Another readiness wait returned immediately without another click. [Sanitized reproduction](reproduction.json) retains both failures and the original CI job.

The existing navigation observation now subscribes before interaction, retains lifecycle events and checks the current document after subscribing. Version checks discard readiness read across navigation. One unchanged deadline bounds all rechecks. The implementation does not repeat clicks, change target identity or relax follow/activate compatibility. Explicit networkidle still uses Playwright's network-quiescence condition. Public MCP contracts, planner guidance, mapping and historical workflows are unchanged.

The Browser suite passes **109 tests**, including 21 added cases for lifecycle ordering, delayed readiness, reloads, redirects, same-document and non-navigation actions, cancellation, closure, disposal, genuine timeout, network idle and all affected action entrypoints. The original external probe passes 80/80 after the correction; this supports, rather than replaces, controlled regression tests. Host tests also cover actual reference-based follow, encrypted receipts and restart without another interaction.

Full solution and published-binary validation are recorded below when complete. CI and the authorized single Amazon execution are separate gates; deterministic success does not establish live success.

[Configured readiness](provider-readiness.json) passed without inference. The existing ECB quote is fresh under the unchanged policy. [Campaign accounting](accounting-before.json) retains EUR 96.942719 / 150, including EUR 2.606753 reserved for two historical unknown completions. No reservation was released or uncertain invocation resumed.

Reproduce the focused gate with:

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true \
  --filter 'FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~BenchmarkAdmissionTests'
```

Browser supports a self-contained managed publish; Native AOT is not supported by its Playwright dependency. PR #117 remains draft until its full execution gates pass.
