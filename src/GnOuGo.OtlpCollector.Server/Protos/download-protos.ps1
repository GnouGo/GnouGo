<#
.SYNOPSIS
  Downloads official OpenTelemetry .proto files from the opentelemetry-proto GitHub repository.
  Idempotent: skips download if the correct version is already present.

.EXAMPLE
  .\download-protos.ps1                   # uses default version
  .\download-protos.ps1 -Version v1.5.0   # specific version
  .\download-protos.ps1 -Force            # force re-download
#>
param(
    [string]$Version = "v1.4.0",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$outDir      = Join-Path $PSScriptRoot "otlp-proto"
$versionFile = Join-Path $outDir "VERSION"

# --- Check if already up to date ---
if (!$Force -and (Test-Path $versionFile)) {
    $current = (Get-Content $versionFile -Raw).Trim()
    if ($current -eq $Version) {
        Write-Host "OpenTelemetry Proto $Version already present — skipping download."
        exit 0
    }
    Write-Host "Version mismatch: have '$current', want '$Version' — re-downloading."
}

# --- Download ---
$baseUrl = "https://raw.githubusercontent.com/open-telemetry/opentelemetry-proto/$Version"

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

$files = @(
    "opentelemetry/proto/common/v1/common.proto",
    "opentelemetry/proto/resource/v1/resource.proto",
    "opentelemetry/proto/trace/v1/trace.proto",
    "opentelemetry/proto/logs/v1/logs.proto",
    "opentelemetry/proto/metrics/v1/metrics.proto",
    "opentelemetry/proto/collector/trace/v1/trace_service.proto",
    "opentelemetry/proto/collector/logs/v1/logs_service.proto",
    "opentelemetry/proto/collector/metrics/v1/metrics_service.proto"
)

Write-Host "Downloading OpenTelemetry Proto $Version ..."
foreach ($file in $files) {
    $url  = "$baseUrl/$file"
    $dest = Join-Path $outDir $file
    $dir  = Split-Path $dest -Parent
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Write-Host "  $file"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

Set-Content -Path $versionFile -Value $Version -NoNewline
Write-Host "Done — OpenTelemetry Proto $Version ready in Protos/otlp-proto/"
