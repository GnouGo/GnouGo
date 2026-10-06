#!/usr/bin/env python3
"""Zero-inference diagnostic; does not read campaign state or replay a live run."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape

parser = argparse.ArgumentParser()
parser.add_argument("--root", type=Path, required=True)
args = parser.parse_args()
root = args.root.resolve()
fixture = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix="gnougo-loop-diagnostic-") as directory:
    project = Path(directory)
    shutil.copyfile(fixture / "loop-amplification.cs", project / "Program.cs")
    reference = escape(str(root / "src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj"), {'"': '&quot;'})
    (project / "Probe.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
        '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
        '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
        '</PropertyGroup><ItemGroup><ProjectReference Include="' + reference + '" />'
        '</ItemGroup></Project>\n'
    )
    subprocess.run([
        "dotnet", "build", str(project / "Probe.csproj"), "-m:1", "-warnaserror",
        "-p:SkipModelMetadataGeneration=true",
    ], cwd=root, check=True)
    subprocess.run([
        "dotnet", str(project / "bin/Debug/net10.0/Probe.dll"),
        str(fixture / "loop-amplification.yaml"),
    ], cwd=root, check=True)
