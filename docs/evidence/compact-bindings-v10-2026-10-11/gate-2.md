# Gate 2: approval-fingerprinted v10 lowering

The same 18-task composition produces 65 steps and 38 sets under v9 and v10.
Actual local MCP YAML falls from 195,783 to 189,133 UTF-8 bytes. The static-contract
comparison is 195,627 -> 188,977 characters; maximum line length remains 268.
No task/composition change is counted as a compiler gain.

Pure copy projections retain only declared exports when all observers select those
exports. A single export is the result item; multiple exports share an object.
Whole-envelope and presence observers keep their original representation. Eager
structural capture copies inline only when each value is used once in the original
order. Independent checked locals, intermediate schemas and object wrappers remain.
Formatting follows syntax; callback parameters and ordinary arguments stay together.
No runtime, public compiler overload, permission, mapping semantics or allowance changes.

Validation: 88 owning checks, then 34 final boundary/projection checks and five
local composition/Browser/Document checks pass with -warnaserror. Three successful
local cases visit ten products, process 29 pages, retain all 12 offered candidates,
check exact XLSX cells (including missing descriptions and repeated choices), close
Browser and recover without repeated calls. The unoffered case fails before visits
or writing and still cleans up. Typed row/status assembly uses zero inference.

An isolated 1,602-record run measures total managed allocations for execution:
- Single export: 218,509,184 -> 156,667,432 bytes; YAML 9,122 -> 8,108.
- Multiple exports: 188,604,720 -> 173,237,600 bytes; YAML 10,621 -> 9,885.
Both use one set invocation with exact original values and positions. These totals
include execution/recovery infrastructure and are not the sandbox's memory ceiling.
Measurements collected alongside parallel tests varied; the isolated measurements
are retained separately. Neither profiling nor tests increase runtime limits.

Retained v9 replay through public encrypted storage reproduces 75,217 bytes and
SHA-256 86de0c717fb8ed177d5bab3ec16b1fae77ea4bb16889e49d99f469482ae3730b.
No historical state was changed, and provider/MCP calls are zero. Older internal
and public compiler overloads remain available. Skill validation passes.

Rejected test-development attempts are retained here as findings: the whole-result
fixture initially declared a closed contract over an open producer and was correctly
rejected. A bare-evaluator test omitted expression_contracts and was correctly denied
access to m; the final test runs the actual checked step boundary. The first disposable
replay lacked its native SQLite dependency; after copying the existing published
dependency it passed unchanged. None of these failures was fixed by weakening validation.

Full solution, publishing and CI gates follow after the remaining sequential fixes.
No paid work or historical replay was authorized or dispatched by this gate.
