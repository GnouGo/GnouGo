import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "update_proxy_copilot_homebrew", Path(__file__).resolve().parents[1] / "update-proxy-copilot-homebrew.py"
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class HomebrewFormulaTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.artifacts = self.root / "artifacts"
        self.tap = self.root / "tap"
        self.destination = self.tap / "Formula/gnougo-proxy-copilot.rb"
        self.archives = {}
        for arch in ("arm64", "x64"):
            archive = self.artifacts / f"proxy-copilot-osx-{arch}" / f"GnOuGo.ProxyCopilot.Server-osx-{arch}-aot.tar.gz"
            archive.parent.mkdir(parents=True)
            archive.write_bytes(f"synthetic {arch} archive".encode())
            self.archives[arch] = archive

    def test_generates_both_architectures_and_preserves_desktop_cask(self):
        cask = self.tap / "Casks/gnougo.rb"
        cask.parent.mkdir(parents=True)
        cask.write_text("existing desktop cask", encoding="utf-8")
        self.assertTrue(module.update_formula("v0.19.3", self.artifacts, self.tap))
        formula = self.destination.read_text(encoding="utf-8")
        self.assertIn('version "0.19.3"', formula)
        for arch, archive in self.archives.items():
            self.assertIn(f"/releases/download/v#{{version}}/{archive.name}", formula)
            self.assertIn(f'sha256 "{hashlib.sha256(archive.read_bytes()).hexdigest()}"', formula)
        self.assertIn('libexec.install Dir["*"]', formula)
        self.assertIn('bin.install_symlink libexec/"GnOuGo.ProxyCopilot.Server" => "gnougo-proxy-copilot"', formula)
        self.assertEqual("existing desktop cask", cask.read_text(encoding="utf-8"))

    def test_rerun_does_not_rewrite_and_new_version_updates(self):
        module.update_formula("v0.19.3", self.artifacts, self.tap)
        original = self.destination.stat().st_mtime_ns
        self.assertFalse(module.update_formula("v0.19.3", self.artifacts, self.tap))
        self.assertEqual(original, self.destination.stat().st_mtime_ns)
        self.assertTrue(module.update_formula("v0.19.4", self.artifacts, self.tap))
        self.assertIn('version "0.19.4"', self.destination.read_text(encoding="utf-8"))

    def test_rejects_unstable_or_invalid_version_without_writing(self):
        for tag in ("v0.19.3-beta.1", "v0.19.3-dev.2", "0.19.3", "v01.2.3", "v1.2", 'v1.2.3";system("bad")', "v1.2.3\n"):
            with self.subTest(tag=tag):
                with self.assertRaisesRegex(ValueError, "stable release tag"):
                    module.update_formula(tag, self.artifacts, self.tap)
                self.assertFalse(self.destination.exists())

    def test_missing_or_duplicate_archive_preserves_existing_formula(self):
        for arch in ("arm64", "x64"):
            for duplicate in (False, True):
                with self.subTest(arch=arch, duplicate=duplicate):
                    module.update_formula("v0.19.3", self.artifacts, self.tap)
                    original = self.destination.read_bytes()
                    archive = self.archives[arch]
                    content = archive.read_bytes()
                    extra = self.artifacts / archive.name
                    if duplicate:
                        extra.write_bytes(content)
                    else:
                        archive.unlink()
                    with self.assertRaisesRegex(ValueError, "Expected exactly one release archive"):
                        module.update_formula("v0.19.4", self.artifacts, self.tap)
                    self.assertEqual(original, self.destination.read_bytes())
                    if duplicate:
                        extra.unlink()
                    else:
                        archive.write_bytes(content)


if __name__ == "__main__":
    unittest.main()
