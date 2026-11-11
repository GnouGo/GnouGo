# Long literal navigation rules

## Demonstrated cause

The approved source-only AAX diagnostic initially finds no fresh AAX entries.
The real MCP source preflight then captures 1,408 records and two unaccounted
heading-bearing empty-ASIN contexts. V13 correctly rejects that source before
dependent reads or paid planning. No observation loss is demonstrated.

An independent reproduction acquires through the configured Release MCP and keeps
that source open while the existing diagnostic guard controls direct production
Browser reads. The separate resolution browser has the same effective profile;
it is a different browser context, not a full MCP workflow qualification. Proxy
environment overrides must match before navigation. Both browsers close and
their no-active-page probes are verified.

The [retained reproduction](source-failure.json) captures 1,640 source records and
six fragment-free AAX occurrences in original order, including repetitions. The
first exact GET has a complete ordered redirect to an Amazon product. The second
fails during initial URL validation, before navigation: a 2,315-character escaped
literal causes the nonbacktracking regex compiler to require 11,580 automaton nodes,
exceeding its unchanged 10,000-node limit. The original result is retained as
`INTERNAL_ERROR`, not a complete redirect or classified product card. Authority
expires on failure; subsequent entries are not contacted.

## Generic correction

The Browser policy recognizes a canonically escaped literal only when
`Regex.Escape(Regex.Unescape(pattern)) == pattern`. That pattern denotes exactly
one string, so an ordinal comparison is equivalent to the original anchored regex.
Long literal URL rules no longer require an automaton. Other patterns retain their
original bounded nonbacktracking engine; no backtracking fallback or increased
engine limit is introduced.

The existing scheme, host and whitespace checks remain. The same pattern must
still authorize both the supplied and transmitted URL forms. Default-port or
path canonicalization cannot acquire permission, and redirect checks still occur
before contact. No URL rewriting, new route/host grant, public API, persisted
schema, protocol version, model setting or workflow/verifier activation changes.

## Validation and limits

Five sanitized regression cases first reproduce the automaton exception: one
permitted long literal and four changed spellings that should be denied. All
**197 Browser tests pass** in Release with `-warnaserror`, zero skips, after the
correction. Coverage also retains ordinary regex escapes and rejection of
backreferences by the nonbacktracking engine.

[Controlled validation of the six retained source URLs](literal-source-validation.json)
admits every original occurrence and rejects 24 query/fragment/default-port/path
variants. Source bytes remain unchanged, with zero navigation and model calls.
This does not reactivate the expired source or establish live redirect completion.

A fresh independently identified source-only attempt remains necessary to validate
all own ordered AAX reads after the correction. Prior no-entry attempts establish
neither destinations nor missing-page facts. The source-only grant still excludes
full workflow/classification activation. V9/V13 full workflows continue to reject
unaccounted AAX contexts; full Amazon and Code qualification remain **0/2** each.

The complete shared baseline at `745a12fc` is 9,110 passes and 13 existing skips.
The Browser test count overlaps that baseline and is not added as independent
coverage. Final-source CI and affected host validation are reported separately.
