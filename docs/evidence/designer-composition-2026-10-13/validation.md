# Validation and measurements

Offline gates completed before candidate publication:

| Gate | Result |
| --- | --- |
| New compiler-diagnostic, explicit revision, structural business-scope and opposite-guard regressions | 7 passed |
| Existing Planning producer/targeted revision regressions | 37 passed |
| Host ownership, receipt recovery, uncertain completion and strict nullable-ID regressions | 34 passed |
| Actual local Flow/Browser/Document execution | 8 passed |
| Exact saved r4 copied-session continuation | Final review r8; cumulative calls 2→5; zero repairs, provider calls or MCP operations; retained record unchanged |

Commands use `dotnet test … -m:1 -warnaserror`. Focused runs use existing built dependencies; the final solution run rebuilds serially. Compressed successful logs and failed-attempt evidence are retained here. Candidate full-solution/CI completion is reported on PR #117; this document does not mark pending checks as passed.

## Independent workbook evidence

[Workbook oracle](workbook-oracle.json) records independently read OpenXML cells, hashes, actual visits and complete snapshot coverage. [Local artifacts](local/) include the real XLSX files, compressed original MCP observations and measured runtime/recovery results.

Complete scenario: 10 product visits; 40 observed pages / 79 records; 12 offered candidates, including candidates outside mapping-generation examples; 11 workbook rows including the supplied header. All 44 cells retain their exact expected values and order. Formula-looking names remain literal strings. Browser closes once. The unchanged independent observed-product oracle accepts the complete/one/repeated cases and rejects a rewritten description.

Null-URL scenario: 9 visits, 10 output records, one row of null facts with an explicit not-visited status. Missing-description/price scenario: 10 visits and the corresponding blank cells. Empty selection: header-only workbook. Those outcomes are checked against fixture ground truth; the conservative live oracle does not certify unsupported absence claims or a zero-product business outcome.

Unknown ID: no product visit or successful write. Incomplete search acquisition: the valid homepage control mapping completes first, then Browser reports `OBSERVATION_INCOMPLETE`; there is no mapping of the failed search snapshot, product visit or write. Both retain normal cleanup. No acquisition failure is converted into a missing-data product record.

## Counts and costs

Corrected retained-identity artifact: **39 root/nested tasks, 62 steps, 17 sets, 23 workflows, 283,962 YAML bytes**. There are also two typed group declarations with direct exports. The retained invalid proposal has 16 tasks and nine inference transforms but cannot produce executable YAML; this is not a performance comparison against an executable baseline.

The corrected composition has three extraction declarations and three decision declarations. The complete local run uses **24 deterministic inference responses: 12 mappings and 12 decisions** (home, search and ten product pages). Typed copying, status checks, ID reconnection, text copying, row assembly and formatting use **zero inference**. Every mapped page has exactly one validated nested result; complete serialized provider requests remain within the unchanged 12,000-token fixture allowance. Compact decision data is 2,568 bytes in the complete case, with no raw snapshot or action-URL leakage.

Complete-case measurements (diagnostic, not optimization claims): approximately **9.80 GB cumulative managed allocation**, 81.26 MB logical journal, 30.20 MB physical temporary store including workbook/index, 364 invocations, 1,353 checkpoints, 1,427 check events / 522,043 event bytes. The measured execution interval is about 25.8 seconds on this machine. These are cumulative process allocations, not peak memory; separate MCP process memory is not included. Metrics retain timings and recovery measurements per case. No limit was changed.

All successful local scenarios restart from the encrypted committed terminal result with identical outputs/workbook bytes and **zero repeated inference or external calls**. No historical session or uncertain invocation was replayed.

## Remaining limitations

No paid model compliance or Amazon execution has been demonstrated by this correction. The original Designer session remains stopped and unmodified. The new artifact requires review and approval before any authorized execution.

The unchanged v15 compiler still has the two exploratory pure-branch/direct-array issues described in the main report; the delivered composition uses existing checked boundaries. The extra row/header checks preserve the accepted cardinality contract, rather than claiming that flatten alone establishes it.
