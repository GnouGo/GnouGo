from pathlib import Path
import re,sys,hashlib,json
path=Path(sys.argv[1]);text=path.read_text(errors='replace');known=[
 'Query precompilation is an experimental feature and should be used with caution.',
 'NativeAOT support is experimental and can change in the future.']
notices=[];unexpected=[]
for line in text.splitlines():
 if not re.search(r'(?i)\b(?:warning|avertissement|error|erreur)\s+(?:[a-z]+\d+\s*)?:',line):continue
 if any(n in line for n in known) and 'microsoft.entityframeworkcore.tasks/10.0.12/buildTransitive/Microsoft.EntityFrameworkCore.Tasks.targets(98,5)' in line and 'GnOuGo.Flow.Persistence.csproj]' in line and re.search(r'(?i)\b(?:warning|avertissement)\s*:',line):notices.append(line)
 else:unexpected.append(line)
policy=Path('scripts/verify-warning-free-publishes.ps1')
print(json.dumps({'policy':str(policy),'policy_sha256':hashlib.sha256(policy.read_bytes()).hexdigest(),'scope':'Existing pinned EFCORETASKS exact notices; all other warning/error diagnostics rejected','accepted_exact_experimental_notices':len(notices),'unexpected_diagnostics':unexpected},indent=2))
if unexpected:raise SystemExit(1)
