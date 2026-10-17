# Gate 1: checked controls

The unchanged 24-task comparison compiles from 61 steps / 27 sets / 240,061 YAML bytes to 59 steps / 25 sets / 239,371 bytes. Workflows remain 13. These are measured compiler outputs; no live workflow was changed.

Removed `n_d628b41d17a62006` and `n_e63feabf14636d08`: their boolean checks execute on the respective switch expression, before selection, and retain the original origins and `/value` instance paths. The existing branch control record owns the resolved decision. Input preparation, branch bodies and external receipt boundaries are unchanged.

Validation: 97 Flow expression/output/control/recovery checks and 65 Planning fusion/export checks passed with `-warnaserror`. The real local Browser/Document one-product composition passed, including independent workbook assertions. The initial local attempt stopped at the fixture's historical fixed step-count assertion; the v13 assertion now requires a reduction while historical profiles retain exact expectations.

The remaining predicate switches, lexical branches and terminal sets are pending subsequent gates. No inference provider or external site was called.
