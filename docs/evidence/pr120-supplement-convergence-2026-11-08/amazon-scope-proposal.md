# Proposed Amazon test-scope extension — not authorized or active

The retained complete search contains exact observed links whose entry path is
`/sspa/click`. Existing delegated authority rejects that path before Browser
transport. The unchanged `amazon-business-v4` verifier also rejects any sponsored
redirect identity as unverified and recognizes only its older occurrence proof.
The new generic Browser metadata cannot by itself make that retained verifier pass.

The generic Browser implementation and 157 local regressions now preserve semantic
occurrences, associated links, DOM order, source references and actual redirect
responses. They reject forbidden hosts/paths before contact, retain partial chains,
stop loops and do not replay POST interactions. This grants no expanded authority.

The proposed isolated benchmark extension is limited to:

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

No such delegation or verifier change has been activated. Explicit authorization
is needed before implementing and qualifying this expanded isolated scope. Other
scenarios and generic corrections can proceed within existing authority.
