"""Install the pinned upstream headless CLI into a new directory (Python 3.11+)."""

import argparse
import hashlib
import json
import os
import platform
import shutil
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath


VERSION = "0.19.1"
ARCHIVES = {
    ("Windows", "x86_64"): ("windows-x64-portable.zip", "f2b46bf227b389c3e5443080b5c5380d70351706cc5137c1be7014e71152d52d"),
    ("Windows", "aarch64"): ("windows-arm64-portable.zip", "75384a7c32e6f50e07adac2135d01868687aa1b24cf9a3009a0540680e8277e9"),
    ("Linux", "x86_64"): ("linux-x86_64.tar.gz", "ec75aaaef10a6dc4813f7ebf0f7fd8f980520c5b3d051ba3d76204c9e5d76fea"),
    ("Linux", "aarch64"): ("linux-aarch64.tar.gz", "20eb8d82847e1a89cfc26eb5b804c8406e66e0c64e2bfc601236f4ad046f6b21"),
    ("Linux", "riscv64"): ("linux-riscv64.tar.gz", "a8264b04be654f43099560edee55e34f7d8e2501120de368e12d1e53b45e688f"),
    ("Darwin", "universal"): ("macos-universal.zip", "1285b14823cd413ee2a666b501f8ba5076ce2ba043938143bc04bf26ec5bf0e8"),
}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def install(target, cached):
    system = platform.system()
    arch = platform.machine().lower()
    arch = {"arm64": "aarch64", "amd64": "x86_64"}.get(arch, arch)
    key = (system, "universal" if system == "Darwin" and arch in ("aarch64", "x86_64") else arch)
    suffix, expected = ARCHIVES[key]
    package = "deckcraft-cli" if system == "Darwin" else "deckcraft"
    name = f"{package}-{VERSION}-{suffix}"
    url = f"https://github.com/storytold/deckcraft/releases/download/v{VERSION}/{name}"
    executable = "deckcraft-cli.exe" if system == "Windows" else "deckcraft-cli"
    target = target.resolve()
    if target.exists():
        raise FileExistsError(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target.parent) as scratch:
        stage = Path(scratch) / "install"
        stage.mkdir()
        archive = cached or Path(scratch) / name
        if not cached:
            with urllib.request.urlopen(url, timeout=120) as response, archive.open("xb") as out:
                shutil.copyfileobj(response, out)
        if digest(archive) != expected:
            raise ValueError(f"SHA-256 mismatch for {name}")

        def copy_member(member, stream):
            base = PurePosixPath(member).name
            if base == executable or base.startswith(("LICENSE", "NOTICE", "OFL-", "ATTRIBUTION")):
                with stream() as source, (stage / base).open("xb") as dest:
                    shutil.copyfileobj(source, dest)

        if name.endswith(".zip"):
            with zipfile.ZipFile(archive) as files:
                for member in files.infolist():
                    if not member.is_dir() and "__MACOSX" not in member.filename:
                        copy_member(member.filename, lambda: files.open(member))
        else:
            with tarfile.open(archive) as files:
                for member in files:
                    if member.isfile():
                        copy_member(member.name, lambda: files.extractfile(member))
        binary = stage / executable
        binary.chmod(0o755)
        receipt = {"source": "4a980c33ddf37eb64157777fcaeab6d19baa5878",
                   "url": url, "archive_sha256": expected, "cli_sha256": digest(binary),
                   "system": system, "architecture": arch}
        (stage / "installation.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        os.rename(stage, target)
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", type=Path)
    parser.add_argument("--archive", type=Path, help="use an already downloaded archive with the pinned hash")
    args = parser.parse_args()
    print(json.dumps(install(args.target, args.archive), indent=2))
