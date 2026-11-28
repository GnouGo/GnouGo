"""Exercise the packaged GnOuGo assemblies and the configured headless DeckCraft CLI."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile
from xml.etree import ElementTree


PACKAGES = ("GnOuGo.AI.Core", "GnOuGo.Flow.Core", "GnOuGo.Flow.Integrations")
SUITES = {
    "GnOuGo.AI.Core.Tests": ("VisionRequiresDeclaredCapabilityAndTransmitsTheImageInTheHttpBody",),
    "GnOuGo.Flow.Tests": (
        "MixedPreviewProducesAReusableImageInputForTheNextWorkflowStep",
        "BusinessAgentSeesItsInputImageAndTheImageFromItsSelectedMcpTool",
    ),
    "GnOuGo.Flow.Integrations.Tests": ("HeadlessCliCreatesEditsReopensAndRenders", "VisionReservationUsesEachTransmittedImageAndExactModelRules"),
}


def main(destination):
    if not Path(os.environ["GNOUGO_DECKCRAFT_CLI"]).is_file():
        raise FileNotFoundError("Set GNOUGO_DECKCRAFT_CLI to the qualified CLI.")
    destination.mkdir(parents=True, exist_ok=True)
    for suite in SUITES:
        subprocess.run(["dotnet", "build", f"tests/{suite}", "-c", "Release", "-warnaserror",
                        "-p:SkipModelMetadataGeneration=true"], check=True)
    receipt = {}
    for package in PACKAGES:
        subprocess.run(["dotnet", "pack", f"src/{package}", "-c", "Release", "--no-build",
                        "--no-restore", "-o", str(destination)], check=True)
        archive, = destination.glob(f"{package}.[0-9]*.nupkg")
        with zipfile.ZipFile(archive) as payload:
            assembly = payload.read(f"lib/net11.0/{package}.dll")
        # VSTest uses the test's deps.json. Replace its local copy with the exact NuGet payload.
        consumers = []
        for suite in SUITES:
            target = Path("tests", suite, "bin", "Release", "net11.0", package + ".dll")
            if target.exists():
                target.write_bytes(assembly)
                consumers.append(suite)
        if not consumers:
            raise RuntimeError(f"No test host references {package}")
        receipt[package] = {"package_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                            "assembly_sha256": hashlib.sha256(assembly).hexdigest(), "test_hosts": consumers}
    (destination / "assemblies.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    for suite, tests in SUITES.items():
        selection = "|".join("FullyQualifiedName~" + test for test in tests)
        subprocess.run(["dotnet", "test", f"tests/{suite}", "-c", "Release", "--no-build", "--no-restore",
                        "--filter", selection, "--results-directory", str(destination),
                        "--logger", f"trx;LogFileName={suite}.trx"], check=True)
        report = destination / f"{suite}.trx"
        results = ElementTree.parse(report)
        counts = results.find(".//{*}Counters")
        names = [r.get("testName", "") for r in results.findall(".//{*}UnitTestResult")]
        if (counts is None or counts.get("passed") != counts.get("total")
                or any(not any(test in name for name in names) for test in tests)):
            raise RuntimeError(f"The selected tests did not all execute successfully in {suite}")


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve())
