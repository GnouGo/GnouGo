#!/usr/bin/env bash
# Historical inspection is isolated from the schema-5 production decoder.
set -euo pipefail
if [[ ! ( $# == 1 && ( $1 == --all-retained || $1 == --verify-fixtures ) ) && ! ( $# == 2 && $1 =~ ^[a-zA-Z0-9_-]+$ && $2 =~ ^[0-9]+$ ) ]]; then
  echo 'Usage: planner-schema4-audit.sh SESSION REVISION | --all-retained | --verify-fixtures' >&2
  exit 2
fi
repo_root=$(git rev-parse --show-toplevel)
audit_root=$(mktemp -d "${TMPDIR:-/tmp}/gnougo-schema4-audit.XXXXXX")
audit_root=$(cd "$audit_root" && pwd -P)
trap 'rm -rf "$audit_root"' EXIT
# The pinned audit binary can only be invoked through its receipt-only replay command.
git -C "$repo_root" archive 2536c277232995d9d9fac06c9fae9cfe058817a0 | tar -x -C "$audit_root"
if [[ $1 == --verify-fixtures ]]; then
  # Audit-only hook: validate current synthetic fixtures against the encrypted
  # frozen catalog in memory, within the historical read-only store boundary.
  python3 - "$audit_root" "$repo_root" <<'PY'
from pathlib import Path
import shutil, sys
audit = Path(sys.argv[1]) / 'tests/GnOuGo.Agent.Planning.Benchmark'
current = Path(sys.argv[2]) / 'tests/GnOuGo.Agent.Planning.Benchmark'
shutil.copyfile(current / 'CodeReviewExecutionFixture.cs', audit / 'CodeReviewExecutionFixture.cs')
source = audit / 'OfflineReplay.cs'
text = source.read_text()
marker = '        await using var db = await contexts.CreateDbContextAsync(ct);'
if text.count(marker) != 1:
    raise SystemExit('The pinned audit hook is ambiguous.')
source.write_text(text.replace(marker, '        await CodeReviewExecutionFixture.VerifyAsync(discovery, ct);\n' + marker))
PY
fi
dotnet build "$audit_root/tests/GnOuGo.Agent.Planning.Benchmark" -m:1 -v quiet
if [[ $1 == --all-retained ]]; then
  python3 - "$repo_root/tests/GnOuGo.Flow.Planning.Tests/Fixtures/ConvergenceEvidence.json" > "$audit_root/retained-sessions.txt" <<'PY'
import json, sys
for run in json.load(open(sys.argv[1]))['runs']:
    print(run['session'], run['revision'])
PY
elif [[ $1 == --verify-fixtures ]]; then
  printf '%s\n' 'codereview-bc72dd6 0' > "$audit_root/retained-sessions.txt"
else
  printf '%s %s\n' "$1" "$2" > "$audit_root/retained-sessions.txt"
fi
while read -r audit_session audit_revision; do
  dotnet run --no-build --project "$audit_root/tests/GnOuGo.Agent.Planning.Benchmark" -- replay "$audit_session" "$audit_revision"
done < "$audit_root/retained-sessions.txt"
