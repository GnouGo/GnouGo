# Authorized exact review-fragment proof references

The user explicitly approved the [proposal at fc353ebe](../pr120-amazon-v9-2026-11-11/fragment-alias-proposal.md).
Policy `amazon-observed-review-proof-context-v6`, grant
`user-amazon-workflow-review-proof-20261111`, and oracle `amazon-business-v10`
capture this verifier-only amendment. V5/v9 retain their existing refusal.

Navigation uses the exact unchanged v5 allowlist. A retained source record may
reference completed product evidence only when its original href equals a separately
observed permitted source href plus literal `#customerReviews`, its empty-ASIN context
matches exactly, and it has no heading or semantic occurrence. No URL is stripped,
decoded, normalized or created. Every navigable occurrence still requires its own
ordered GET, even when hrefs repeat. All context links must be accounted for,
identities consistent and exactly one heading present. Store evidence cannot qualify
this product exception. Distinct contexts retain distinct occurrences.

The report separates `source_proof_aliases` from `source_resolution_evidence` and
physical source-read counts. Each alias identifies the actual source records and
event indices whose evidence it references; it creates no HTTP call, receipt or
business visit. Original records remain unchanged. Later first-N business visits,
fields, XLSX, writer receipt, execution and cleanup remain mandatory.

The sanitized regression first failed with an empty authorized-entry set under
the new policy before alias binding was implemented. **389 affected tests pass**, with zero failures/skips and `-warnaserror`;
[controlled evidence](controlled-validation.json) retains the red/green log hashes.
A fresh actual-profile source preflight will be recorded separately. No model call is
admitted before the source prerequisite qualifies. No campaign funds are added.
