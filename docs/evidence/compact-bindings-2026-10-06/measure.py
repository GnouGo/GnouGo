"""Run the deterministic before/after fixture; does not access a provider or saved runs."""
import argparse
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape
parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
args = parser.parse_args()
with tempfile.TemporaryDirectory(prefix='gnougo-compact-measure-') as temporary:
    project = Path(temporary)
    reference = args.root.resolve() / 'src/GnOuGo.Flow.Planning/GnOuGo.Flow.Planning.csproj'
    project.joinpath('Measure.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="' + escape(str(reference), {'"': '&quot;'}) + '" /></ItemGroup></Project>')
    project.joinpath('Program.cs').write_text(Path(__file__).with_name('metrics.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(project / 'Measure.csproj')], check=True)
