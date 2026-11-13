# V9/V13 restores source coverage without new navigation

The original obligation preserves every product occurrence and its order; it
does not permit omitting an ambiguous card because its href uses an unsupported
route. The retained four-link context has an original heading and ASIN ancestor,
but V12 excludes it from candidate classification and reports favorable source
coverage. The sanitized case reproduces that omission before and after already
identified products, including duplicate hrefs.

Policy `amazon-observed-source-coverage-v9` and oracle `amazon-business-v13`
add a fail-closed coverage check for heading-bearing original ASIN contexts whose
href is neither an observed product identity nor an existing source entry. An
unaccounted context retains its original member references and metadata, gains no
identity or non-product classification, and blocks source authority before dependent
reads. The existing coverage-analysis function supplies that evidence; no model
judgment, product filter, URL normalization or new observation field is introduced.

This corrects the original no-omission obligation; it introduces no new business
exception. Routes, hosts and the original V8 workflow grant are unchanged:
`AmazonSourceCoveragePolicy.UrlPattern` is exactly V8's pattern and its approval
remains `user-amazon-inherited-store-list-20261111`. AAX workflow navigation stays
forbidden. Its separate source-only grant is neither reused nor activated as a
workflow grant. V8/V12 and every earlier version remain unchanged.

Fresh qualification captures the new policy with `--amazon-source-coverage
--amazon-scope-approval user-amazon-inherited-store-list-20261111`. The host and
full business oracle require matching policy/version/approval. Existing own-link
resolutions, card/review/store-list proof, later ordered business visits, fields,
real workbook, cleanup and the independent oracle remain mandatory. A source
preflight must pass on the actual profile before a new full paid plan. If an
unsupported context appears again, the run must block, not silently skip it.

Two original counterexamples fail before the coverage correction. The expanded
affected suite passes **527 tests** in Release with `-warnaserror`, no skips,
including unknown contexts before/after known products, repeated hrefs, no source
authority on missing coverage, unchanged historical V12 verdicts, unchanged routes,
wrong-grant rejection, and the complete positive/negative workbook oracle suite.
These controlled checks establish the correction, not live workflow success.

The [fresh diagnostic results](source-diagnostics.json) distinguish source
acquisition from AAX destination proof. Attempt 01 acquired 1,344 records but
failed the diagnostic history-address comparator. After its tested correction,
attempt 02 acquired **1,373 complete records**, passed source admission and cold
analysis, and verified cleanup. Both contained no AAX links and made no AAX GET or
model call. The missing AAX destination proof remains explicit; there is no search
loop to obtain a preferred advertisement. The earlier ambiguous context remains
unverified and is rejected by V13. Neither diagnostic qualifies full source
resolution, business execution or the independent business oracle.
