#!/usr/bin/env bash
set -euo pipefail

version_tag="${1:-}"
output_file="${2:-CHANGELOG.md}"

if [[ -z "$version_tag" ]]; then
  echo "Usage: scripts/generate-changelog.sh <version-tag> [output-file]" >&2
  exit 1
fi

python3 - "$version_tag" "$output_file" <<'PY'
from __future__ import annotations

import datetime as dt
import re
import subprocess
import sys
from pathlib import Path

version_tag = sys.argv[1].strip()
output_file = Path(sys.argv[2])

if not re.fullmatch(r"v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", version_tag):
    raise SystemExit(f"Unsupported version tag format: {version_tag!r}")


def git(*args: str, check: bool = True) -> str:
    result = subprocess.run(
        ["git", *args],
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout.strip()


previous_tag = git("describe", "--tags", "--abbrev=0", "--match", "v[0-9]*", check=False)
range_spec = f"{previous_tag}..HEAD" if previous_tag else "HEAD"

raw_commits = git(
    "log",
    range_spec,
    "--no-merges",
    "--pretty=format:%s%x1f%h",
    check=False,
)

entries: list[str] = []
seen: set[str] = set()
for raw_line in raw_commits.splitlines():
    if not raw_line.strip() or "\x1f" not in raw_line:
        continue

    subject, short_sha = raw_line.split("\x1f", 1)
    subject = subject.strip()
    short_sha = short_sha.strip()
    if not subject:
        continue

    lowered = subject.lower()
    if lowered.startswith("docs: update changelog") or lowered.startswith("chore: update changelog"):
        continue

    line = f"- {subject} ({short_sha})"
    if line not in seen:
        seen.add(line)
        entries.append(line)

if not entries:
    entries.append("- Maintenance release.")

release_date = dt.date.today().isoformat()
new_section = "\n".join([
    f"## {version_tag} - {release_date}",
    "",
    *entries,
    "",
])

if output_file.exists():
    existing = output_file.read_text(encoding="utf-8")
else:
    existing = "# Changelog\n\nAll notable changes to this project are documented in this file.\n\n"

if not existing.startswith("# Changelog"):
    existing = "# Changelog\n\n" + existing.lstrip()

section_pattern = re.compile(
    rf"^## {re.escape(version_tag)} - \d{{4}}-\d{{2}}-\d{{2}}\n(?:.*?)(?=^## v\d+\.\d+\.\d+|\Z)",
    flags=re.MULTILINE | re.DOTALL,
)

if section_pattern.search(existing):
    updated = section_pattern.sub(new_section, existing).rstrip() + "\n"
else:
    heading_match = re.match(r"(?s)^(# Changelog\n(?:\n.*?\n)?\n?)", existing)
    if heading_match:
        insert_at = heading_match.end()
        updated = existing[:insert_at] + new_section + "\n" + existing[insert_at:].lstrip("\n")
    else:
        updated = "# Changelog\n\n" + new_section + "\n" + existing.lstrip("\n")
    updated = updated.rstrip() + "\n"

output_file.write_text(updated, encoding="utf-8")
print(f"Updated {output_file} for {version_tag} from range {range_spec}.")
PY

