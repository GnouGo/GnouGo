# Approved source-only AAX guard

The user explicitly approved the [exact source-only proposal](proposal.md) at
`8b6784fe`. This adds no funding and activates no workflow policy or business
verifier. The separate guard requires
`user-amazon-aax-source-only-20261111`; ordinary V8/V12 grants do not authorize it.

The isolated diagnostic uses the production `BrowserTools` and
`PlaywrightBrowserHost`, with the actual configured Chromium settings loaded from
the same built MCP appsettings/environment. It initially narrows navigation to
the previously approved Amazon.fr search/product/store destinations. Only after
a complete successful owned search with consistent original provenance can the
guard admit fragment-free AAX entries in the observed, unclassified empty-ASIN
heading contexts. It retains original source order and duplicate href occurrences.

The disposable host's existing navigation policy receives exact escaped original
entry strings, not a wildcard AAX route. Consequently its normal redirect guard
checks each hop before contact and rejects unobserved AAX URLs. The source driver
admits only the next occurrence and requires a distinct complete capture for
each result. Fragments remain unnavigable; embedded advertised destinations grant
no authority. Close, new search, incomplete/conflicting results or reused captures
expire the scope. Saved KeyVault settings and all financial journals are unchanged.

All **20** affected Release tests pass with `-warnaserror`, no skips. They cover
original order/repetition, fragment rejection, exact route intersection, wrong
approval, inconsistent source provenance, failed/truncated observations, new
searches, expired/reused proof and actual production navigation-policy rejection
of unobserved entries. The guard is deliberately absent from workflow capture,
delegated execution and oracle version dispatch. All previous versions remain.

The planned real read is a source diagnostic through the production Browser
component, not an MCP-transport or full-workflow qualification. It must preserve
its source/build/profile hashes, exact reads/hops, raw observations, cleanup and
cold analysis. No AAX destination result is claimed at this commit. Any later
workflow/verifier activation requires a separate amendment based on that evidence.
