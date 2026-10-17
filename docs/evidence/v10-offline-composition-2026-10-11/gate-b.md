# Gate B — exact nullable fields

Seven zero-inference executions cover complete facts, a missing description, all
permitted facts absent, unknown and unoffered IDs, duplicate original identities,
and shortened IDs. Each supplied non-null selection is checked against offered
candidates before original lookup. Complete original strings, Unicode, newlines,
tabs and formula-looking text survive unchanged. Missing fields remain null in a
non-null product; no interface text becomes an error. Invalid selection publishes
no result. The complete retained counterpart also compiles: eight checks pass.

The first composition attempt correctly failed `SET_OUTPUT_INVALID`: flattening
optional singleton results does not prove the accepted ten-product bound. The
necessary checked product projection was therefore moved forward from Gate D and
implemented as an existing bounded typed foreach. No compiler rule was changed.
The rejected attempt is retained in `gate-b-bound-failure.txt`.

The corrected intermediate has 27 tasks, 67 steps, 33 sets and 288,976 YAML bytes.
These additions supply the previously missing deterministic nullable-field checks,
not a claim of simplification. Three guarded lookups, singleton projections and
two scalar captures retain null semantics and scope boundaries. Gate D compares
equivalently checked compositions before removing redundant plumbing. The only
remaining learned extraction stages are search candidates and per-product facts;
typed copying and field reconnection require no inference.
