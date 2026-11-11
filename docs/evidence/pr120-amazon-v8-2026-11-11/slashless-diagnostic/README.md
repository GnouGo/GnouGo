# Authorized slashless-source diagnostic

The user explicitly approved the [exact source-only proposal](../next-source-proposal.md):
“Authorize this source-only /clk diagnostic”. This grant is recorded as
`user-amazon-source-only-slashless-20261111`. It adds no money and does not change
v4/v8 or earlier workflow navigation/verifier permissions.

This standalone runner uses the real KeyVault Browser profile and adds only the
literal `/clk?…` route in that isolated source-only host. It selects every exact
matching link occurrence from a new complete successful search, preserving order,
repeated hrefs and raw contexts. The existing Browser enforces every redirect
before contact. The diagnostic entry guard rejects substitutions, direct store
entry, new searches and reuse after failure. No product classification is changed.

It reuses the existing complete-observation and navigation-evidence validators.
Its destination report is diagnostic only: source qualification, occurrence
verification and business-oracle flags remain false, including on an observed
product destination. A heading or empty identity is never silently excluded.

Run `--self-test` for the controlled checks, or `--verify <events.json> <report.json>`
for a cold read-only verification. Live use requires a new output directory and
`<workspace> <fresh-output-directory> --approval user-amazon-source-only-slashless-20261111`.
The admission file is created exclusively, each issue is persisted before dispatch,
and complete raw events are retained before validation. No model call, artifact
review, saved host setting or active workflow policy is modified. Cleanup is always
attempted and verified separately. The runner does not replay an occupied identity.
