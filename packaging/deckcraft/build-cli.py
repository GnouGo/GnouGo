"""Build the patched external CLI and collect notices for this target (Python 3.11+)."""
import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import tempfile
import tomllib
from pathlib import Path


SOURCE = "4a980c33ddf37eb64157777fcaeab6d19baa5878"
REPO = "https://github.com/storytold/deckcraft.git"
PATCH = Path(__file__).resolve().with_name("editable-column-chart.patch")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def notices(source, stage, target, env):
    def cargo(*args):
        return subprocess.check_output(["cargo", *args], cwd=source, env=env, text=True, encoding="utf-8")

    meta = json.loads(cargo("metadata", "--format-version", "1", "--locked", "--filter-platform", target))
    tree = cargo("tree", "--locked", "-p", "deckcraft-cli", "--target", target,
                 "--edges", "normal,build", "--prefix", "none", "--format", "{p}")
    selected = set()
    for line in tree.splitlines():
        match = re.match(r"([^ ]+) v([^ ]+)(?: \(([^)]+)\))?", line.strip().removesuffix(" (*)"))
        if not match:
            continue
        name, version, origin = match.groups()
        matches = [p for p in meta["packages"] if p["name"] == name and p["version"] == version]
        if len(matches) > 1:
            matches = [p for p in matches if origin and ((p["source"] or "").endswith(origin.split("#")[-1])
                       or (p["source"] or "").split("#")[-1].startswith(origin.split("#")[-1])
                       or str(Path(p["manifest_path"]).parent) == origin)]
        if len(matches) != 1:
            raise ValueError(f"Unresolved Cargo package identity: {line}")
        selected.add(matches[0]["id"])
    lock = tomllib.loads((source / "Cargo.lock").read_text(encoding="utf-8"))["package"]
    out = stage / "notices"
    out.mkdir()
    inventory = []
    for p in sorted((p for p in meta["packages"] if p["id"] in selected), key=lambda p: p["id"]):
        root = Path(p["manifest_path"]).parent
        origin = p["source"] or f"git+{REPO}#{SOURCE}"
        registry = origin.startswith("registry+")
        tarball = next((x.get("checksum") for x in lock if x["name"] == p["name"] and x["version"] == p["version"] and x.get("source") == origin), None)
        url = f"https://static.crates.io/crates/{p['name']}/{p['name']}-{p['version']}.crate" if registry else origin
        files = set()
        if p["license_file"]:
            files.add((root / p["license_file"]).resolve())
        for directory in (root, root / "licenses", root / "LICENSES"):
            if directory.is_dir():
                files.update(f for f in directory.iterdir() if f.is_file() and
                             f.name.upper().startswith(("LICENSE", "LICENCE", "COPYING", "NOTICE", "OFL", "COPYRIGHT")))
        if not files and not registry:
            parent = next((d for d in (root, *root.parents) if (d / ".git").exists()), None)
            if parent is not None:
                files.update(f for f in parent.glob("LICENSE*") if f.is_file())
        entry = {"name": p["name"], "version": p["version"], "license": p["license"], "source": url,
                 "crate_sha256": tarball, "notices": []}
        for f in sorted(files):
            key = digest(f)
            dest = out / key
            if not dest.exists():
                shutil.copyfile(f, dest)
            entry["notices"].append({"file": f.name, "sha256": key})
        inventory.append(entry)
    (stage / "components.json").write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")
    for name in ("LICENSE-MIT", "LICENSE-APACHE", "NOTICE", "ATTRIBUTION.md", "docs/brand/LICENSE-brand.txt", "licenses/pdf-writer/LICENSE-ICC.txt"):
        shutil.copyfile(source / name, stage / Path(name).name)


def build(target_dir):
    target_dir = target_dir.resolve()
    if target_dir.exists():
        raise FileExistsError(target_dir)
    target_dir.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target_dir.parent) as work:
        root = Path(work)
        source = root / "source"
        source.mkdir()
        def git(*args):
            subprocess.run(["git", *args], cwd=source, check=True)
        git("init", "--quiet")
        git("fetch", "--quiet", "--depth=1", REPO, SOURCE)
        git("checkout", "--quiet", "--detach", "FETCH_HEAD")
        git("apply", "--check", str(PATCH))
        git("apply", str(PATCH))
        env = dict(os.environ)
        env.pop("CRAFT_FONTS_DIR", None)
        env.pop("CRAFT_FONTS_REQUIRED", None)
        rust = subprocess.check_output(["rustc", "-vV"], text=True)
        host = re.search(r"(?m)^host: (\S+)", rust).group(1)
        cargo_target = Path(env.get("CARGO_TARGET_DIR", source / "target")).resolve()
        env["CARGO_TARGET_DIR"] = str(cargo_target)
        subprocess.run(["cargo", "build", "--locked", "--release", "--target", host, "-p", "deckcraft-cli"],
                       cwd=source, env=env, check=True)
        stage = root / "distribution"
        stage.mkdir()
        name = "deckcraft-cli.exe" if platform.system() == "Windows" else "deckcraft-cli"
        binary = stage / name
        shutil.copy2(cargo_target / host / "release" / name, binary)
        notices(source, stage, host, env)
        receipt = {"source": SOURCE, "patch_sha256": digest(PATCH), "cargo_lock_sha256": digest(source / "Cargo.lock"),
                   "rustc": rust.strip(), "target": host, "cli_sha256": digest(binary), "components_sha256": digest(stage / "components.json")}
        (stage / "installation.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        os.rename(stage, target_dir)
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    build(parser.parse_args().directory)
