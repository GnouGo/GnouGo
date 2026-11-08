# Authorized strict Amazon test-scope extension

The retained complete search contains exact observed links whose entry path is
`/sspa/click`. Existing delegated authority rejects that path before Browser
transport. The unchanged `amazon-business-v4` verifier also rejects any sponsored
redirect identity as unverified and recognizes only its older occurrence proof.
The new generic Browser metadata cannot by itself make that retained verifier pass.

The generic Browser implementation and 157 local regressions now preserve semantic
occurrences, associated links, DOM order, source references and actual redirect
responses. They reject forbidden hosts/paths before contact, retain partial chains,
stop loops and do not replay POST interactions. This grants no expanded authority.

The user explicitly approved this exact extension on 2026-11-08 with the answer **“Authorize this strict extension”**. Its captured policy is `amazon-observed-redirects-v1`, approval reference `user-strict-amazon-extension-20261108`, with opt-in flag `--amazon-observed-redirects` for fresh Amazon Server runs only. The isolated scope is limited to:

- GET content reads of the exact literal HTTPS `/sspa/click` URLs actually delivered
  in a complete search snapshot on `amazon.fr` or `www.amazon.fr`; no generated
  product URL, parsed query parameter or guessed destination establishes identity.
- Every subsequent redirect hop checked before contact against those same hosts
  and explicit approved search/product/redirect routes. Unknown paths, external
  hosts, user information, non-HTTPS schemes and non-default ports remain denied.
  No account/cart action, click tool, publication or write permission is added.
- A separately versioned independent verifier for the new observed occurrence and
  redirect evidence. Keep v4 and retained results unchanged. Require complete,
  coherent source evidence, distinct ordered occurrences, exact observed requested
  links, actual permitted final-product identity, the original fields/workbook and
  cleanup requirements, and all ordinary validation/review/approval/execution gates.
  Missing, ambiguous, stale or inconsistent evidence must still fail. Sponsored
  results remain in the original order and cannot be discarded to obtain a pass.

The opt-in pins `amazon-business-v5` and the policy in the run and artifact-bound delegation. Default v4 and all retained runs keep their original scope and verdicts. Existing saved navigation restrictions cannot be overwritten: configuration refuses an unreviewed replacement. Authorization permits implementation and qualification; it does not establish a successful workflow or provide any further funding.

V5 derives ordered outer semantic occurrences and heading-associated primary links from complete original records, with coherent occurrence contexts and contiguous snapshot references. Variant links remain in the observation. Distinct cards keep distinct occurrences even for the same ASIN. Opaque sponsored identity requires the actual requested link and complete observed HTTP chain to a product, checked before every hop. History-only document address changes remain separate from the last HTTP response URL. Workbook fields, exact name/description/price observations, writer receipts and cleanup are still independently required.
