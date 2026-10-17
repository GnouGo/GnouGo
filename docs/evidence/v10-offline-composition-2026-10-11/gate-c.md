# Gate C — real structured workbooks with headers

Five checks pass with `-warnaserror`. The retained fourth response changes only the
writer's rows binding to the collected product rows, bypassing the incorrect header
producer. That exact edit is reproduced from the saved response without inference.

Actual Document MCP transport writes zero-, one- and ten-product fixtures using
`flatten([[headerRow], productRows])`. Independent OpenXML inspection verifies one,
two and eleven rows respectively, exact strings (including newlines, tabs, Unicode,
formula-looking and interpolation-looking text), blank null positions and no formula
cells. Compilation uses one operation, no set, and no inference for this binding.

The same real one-product workbook without its header fails the unchanged independent
oracle with `product_visits_do_not_match_rows` and `workbook_columns_missing`. The
corrected workbook passes. Those navigation observations are synthetic generic
evidence in this isolated writer gate; real Browser visits are required separately
in the integrated gate. A header-only workbook proves the writing contract, not a
successful live business outcome.
