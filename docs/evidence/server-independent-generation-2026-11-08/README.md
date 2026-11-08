# Server independent generation — 2026-11-08

The first fresh Amazon run on `79ea6ff0` stopped on its initial intent call,
without an accepted contribution, review, approval or execution. Its actual
6,925-byte HTTP request contained no tools. The native stream delivered 745 bytes,
then ended 299.56 seconds after the last bytes, before the captured 600-second
attempt deadline. [Transport and component measurements](amazon01-transport.json)
establish the boundary; they do not identify the responsible upstream hop or show
that request size caused the failure.

Exact receipt inspection found no completed response. The standing user authority
was applied through the existing [retention preview](amazon01-retention-preview.json)
and [immutable retention](amazon01-retention.json). The entire EUR
5.4795342588446036721898790863 reservation and all original partial evidence remain.
The old request/run cannot replay or continue.

The generic fix adds an explicit version-2 captured planning transport policy.
The existing AI.Core retry owner may create a fresh physical identity after an
observed tool-free transport loss. Every journal must admit and reserve that
attempt first. Response assembly starts afresh. The earlier attempt, partial bytes
and unknown usage hold remain; only the final complete response reaches planning.
Saved completion recovery retains precedence, and a loaded unresolved attempt
cannot independently dispatch after restart. Cancellation, permanent/provider or
protocol errors, received final usage and model output exhaustion do not use this
retry path. No TaskPlan, execution engine, model, endpoint or business oracle was
replaced.

The opt-in `server-independent-generation.json` profile permits three physical
attempts total, at most one independent attempt, existing backoff/jitter and
Retry-After, 600-second attempt deadlines and a hard 21-minute total generation
duration. Historical captures and the default confirmed-only policy are unchanged.
New Server captures bind the policy to encrypted tenant/session host authority.
The same shared campaign ceiling, funding identity, tariffs, known consumption,
external hold and every earlier reservation remain enforced.

Initial validation before the new live trial: AI.Core **366 passed**, focused Server
**241 passed**, transport-policy **16 passed**, zero failures/skips/compiler
warnings. These are deterministic regression results, not live qualification.
