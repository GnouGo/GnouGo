# Gate A — typed originals and compact views

The retained revision-5 proposal is linked as a read-only test fixture. Its six
accepted requirements, public outputs and four external operations remain intact.
The disposable counterpart replaces the two learned record-copy stages with typed
per-page projections, retaining complete original records rather than recreating
their identity or action data. Independent extraction retains only the declared
decision fields and processes complete pages.

The owning regression passes with `-warnaserror`. On the same locally discovered
contracts, steps fall from 46 to 42 and sets from 24 to 22; learned copy stages fall
from two to zero. YAML grows from 120,290 to 126,493 bytes because the typed originals
retain their full authoritative record contract. The recorded live YAML is 120,555
bytes with its original catalog metadata. These are composition measurements, not
a change to v10 lowering or a claim of end-to-end acceptance.

No provider or external tool was called by this compilation gate. Local execution,
exact field reconnection and workbook acceptance follow in subsequent gates. The
initial test build exposed a nullable annotation in the new assertion; it was
corrected and the warning-free result is retained in `gate-a.txt`.
