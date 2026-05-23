#!/usr/bin/env bash
set -euo pipefail

version_tag="${1:-}"
output_file="${2:-CHANGELOG.md}"
max_tags="${CHANGELOG_MAX_TAGS:-60}"

if [[ -z "$version_tag" ]]; then
  echo "Usage: scripts/generate-changelog.sh <version-tag> [output-file]" >&2
  exit 1
fi

python3 - "$version_tag" "$output_file" "$max_tags" <<'PY'
from __future__ import annotations

import datetime as dt
import re
import subprocess
import sys
from pathlib import Path

version_tag = sys.argv[1].strip()
output_file = Path(sys.argv[2])
max_tags_raw = sys.argv[3].strip()

version_tag_pattern = re.compile(r"v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?")
release_tag_pattern = re.compile(r"v\d+\.\d+\.\d+")

if not version_tag_pattern.fullmatch(version_tag):
    raise SystemExit(f"Unsupported version tag format: {version_tag!r}")

if max_tags_raw.lower() in {"", "all", "0"}:
    max_tags: int | None = None
else:
    try:
        parsed_max_tags = int(max_tags_raw)
    except ValueError as exc:
        raise SystemExit(f"Unsupported CHANGELOG_MAX_TAGS value: {max_tags_raw!r}") from exc

    if parsed_max_tags < 0:
        raise SystemExit("CHANGELOG_MAX_TAGS must be greater than or equal to 0.")

    max_tags = parsed_max_tags


def git(*args: str, check: bool = True) -> str:
    result = subprocess.run(
        ["git", *args],
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout.strip()


def run_git(*args: str) -> None:
    subprocess.run(
        ["git", *args],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )


def ensure_tag_history() -> None:
    is_shallow = git("rev-parse", "--is-shallow-repository", check=False).lower() == "true"
    if not is_shallow:
        return

    run_git("fetch", "--tags", "--force", "--prune", "--unshallow")
    if git("rev-parse", "--is-shallow-repository", check=False).lower() == "true":
        run_git("fetch", "--tags", "--force", "--prune", "--depth=500")


def ref_exists(ref: str) -> bool:
    return subprocess.run(
        ["git", "rev-parse", "--verify", "--quiet", ref],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        text=True,
    ).returncode == 0


def get_release_tags() -> list[str]:
    tags = [
        tag.strip()
        for tag in git("tag", "--list", "--sort=-creatordate", check=False).splitlines()
        if release_tag_pattern.fullmatch(tag.strip())
    ]
    tags = [tag for tag in tags if tag != version_tag]
    if max_tags is not None:
        tags = tags[:max_tags]
    return tags


def get_release_date(ref: str) -> str:
    if ref == version_tag and not ref_exists(ref):
        return dt.date.today().isoformat()

    date_text = git(
        "for-each-ref",
        f"refs/tags/{ref}",
        "--format=%(creatordate:short)",
        check=False,
    )
    if date_text:
        return date_text.splitlines()[0].strip()

    commit_date = git("log", "-1", "--date=format:%Y-%m-%d", "--format=%ad", ref, check=False)
    if commit_date:
        return commit_date.splitlines()[0].strip()

    return dt.date.today().isoformat()


def get_commit_entries(range_spec: str | None, end_ref: str) -> list[str]:
    log_args = ["log"]
    if range_spec:
        log_args.append(range_spec)
    else:
        log_args.append(end_ref)

    raw_commits = git(
        *log_args,
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

    return entries


ensure_tag_history()
historical_tags = get_release_tags()
current_ref = version_tag if ref_exists(version_tag) else "HEAD"


def build_sections() -> list[str]:
    sections: list[str] = []

    previous_tag = historical_tags[0] if historical_tags else None
    current_range = f"{previous_tag}..{current_ref}" if previous_tag else None
    current_entries = get_commit_entries(current_range, current_ref)
    sections.append(
        "\n".join(
            [
                f"## {version_tag} - {get_release_date(version_tag)}",
                "",
                *current_entries,
            ]
        )
    )

    for index, tag in enumerate(historical_tags):
        previous = historical_tags[index + 1] if index + 1 < len(historical_tags) else None
        tag_range = f"{previous}..{tag}" if previous else None
        entries = get_commit_entries(tag_range, tag)
        sections.append(
            "\n".join(
                [
                    f"## {tag} - {get_release_date(tag)}",
                    "",
                    *entries,
                ]
            )
        )

    return sections

updated = "\n".join(
    [
        "# Changelog",
        "",
        "All notable changes to this project are documented in this file.",
        "",
        "\n\n".join(build_sections()),
        "",
    ]
).rstrip() + "\n"

output_file.write_text(updated, encoding="utf-8")
history_scope = "all available tags" if max_tags is None else f"up to {max_tags} historical tags"
print(f"Updated {output_file} for {version_tag} using {history_scope}.")
PY

