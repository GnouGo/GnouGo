# Gate A — ordered direct-value checks

Validated from baseline `884bf783`, without inference or MCP dispatch.

- 49 Flow expression/input-check tests and 87 Planning fusion/projection tests pass with `-warnaserror`.
- The actual compiler-generated multiple-input example has two direct-value declarations instead of four object/alias declarations. All four original checks remain ordered in `expression_contracts`.
- Runtime validates `source` as an available local name and `order` as a complete sequence; metadata cannot execute code. `instance_path` preserves the original wrapped diagnostic path. Retained object checks carry `object_only` to preserve their original object precondition and error.
- Singleton wrappers are removed only for a closed, understood construction/schema pair. Coupled `oneOf` and referenced schemas retain their wrappers. Historical metadata and compiler overloads keep their previous behavior.
- Exact large integers, decimal representations, nulls, arrays, invalid unused aliases, missing sources, and malformed metadata are covered. A 1.2 MB unrelated observation is not imported.

These are focused results; complete local workbook, recovery, package/AOT and CI gates follow separately.

A final identity probe reproduced a v12-only mismatch: comparing the same whole wrapper returned false after reconstruction. The optimizer now retains whole wrappers used by comparisons/calls and keeps container alias declarations when their isolation can be observed. Four whole/contained-value and same/aliased-value cases preserve v11 truth values; all 55 fusion cases pass. These are additional conservative proof barriers, not changed equality semantics.
