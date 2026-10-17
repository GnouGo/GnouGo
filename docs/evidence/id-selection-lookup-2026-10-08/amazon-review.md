# Review of lookupselection20261008a-amazon-1

Candidate: 3fc9c51dec5176066772802798e560ef61d5a046. Revision: 13. Artifact: 11acd07b7b8f615bf7522d4e9fcf6ce593deb57f6786f9a25315343862235ae7.

The user authorized this one fresh validation. This review applies only to this artifact; historical approvals and invocations are untouched.

- search_amazon_fr: one public query; complete landing observation; optional consent activation from an observed reference; fresh observation afterward; exact observed fill target and submitted query.
- collect_products: every complete source page is extracted, then explicitly flattened. The global decision receives reference and observed decision text, not href/selector/raw pages. Producer references remain identities. All offered candidates are reconnected to originals; auxiliary original_index is assigned in complete offered-product order before selection, never used as a replacement identity. The 1,000-item host ceiling is retained; at most ten selected products are visited.
- visit_each_product: sequential visits use the original record href with a nonnull guard, followed by complete product acquisition and independent extraction over every page.
- handle_absence_and_captcha: nullable observed fields and explicit interpretation status are retained; no source-based value is invented by lookup. Classification/omission correctness requires the unchanged independent oracle; compilation is not evidence of correct extraction. A blocked or empty-result run does not satisfy the nominal oracle.
- save_excel: actual document_write follows product collection and TSV rendering, with the returned filePath exported. Exact cells are checked independently.
- cleanup_browser: root always contains browser_close; unknown completion safety is unchanged.

Seven deterministic lookup bindings check offered selections and reconnect original data. Repeated IDs remain repeated; unknown/ambiguous IDs fail. All extraction bindings disclose adaptive inference under the existing shared runtime/campaign budget. Request limits and all execution oracles remain unchanged. The candidate still needed five explicit planning revisions (eight calls), so minimal-call acceptance has not been achieved.

Remote failures found an omitted guidance phrase; the new AOT smoke found unsupported literal-ID unions. Their corrections are tested separately. This frozen artifact uses typed string ID arrays and compiled successfully; neither issue broadens its authority. No inference or external action has started for this run before this review.
