$ErrorActionPreference = "Stop"

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$ClientApp = Join-Path $RootDir "src" "GnOuGo.Agent.Server" "ClientApp"

Write-Host "Building GnOuGo.Agent UI with Vite..."
Push-Location $ClientApp
try {
    npm install
    npm run build
}
finally {
    Pop-Location
}

Write-Host "✅ UI built into: $(Join-Path $RootDir 'src' 'GnOuGo.Agent.Server' 'wwwroot' 'ui')"
