$ErrorActionPreference = "Stop"

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$ClientApp = [System.IO.Path]::Combine($RootDir.Path, "src", "GnOuGo.Agent.Server", "ClientApp")
$UiOutput = [System.IO.Path]::Combine($RootDir.Path, "src", "GnOuGo.Agent.Server", "wwwroot", "ui")

Write-Host "Building GnOuGo.Agent UI with Vite..."
Push-Location $ClientApp
try {
    corepack.cmd pnpm install --frozen-lockfile
    corepack.cmd pnpm build
}
finally {
    Pop-Location
}

Write-Host "✅ UI built into: $UiOutput"
