# Script de test de la migration TypeScript

$ErrorActionPreference = "Stop"

Write-Host "🔍 Vérification de la migration TypeScript - GnOuGo.Diff ClientApp" -ForegroundColor Cyan
Write-Host "=================================================================`n" -ForegroundColor Cyan

$clientAppPath = Split-Path -Parent $MyInvocation.MyCommand.Path

# Vérifier que nous sommes dans le bon répertoire
if (-not (Test-Path $clientAppPath)) {
    Write-Host "❌ Répertoire ClientApp non trouvé: $clientAppPath" -ForegroundColor Red
    exit 1
}

Set-Location $clientAppPath

Write-Host "📁 Répertoire de travail: $clientAppPath`n" -ForegroundColor Yellow

# 1. Vérifier les fichiers TypeScript
Write-Host "📝 Vérification des fichiers TypeScript..." -ForegroundColor Yellow
$tsFiles = @(
    "src\App.tsx",
    "src\main.tsx",
    "src\types.ts",
    "src\vite-env.d.ts",
    "vite.config.ts",
    "tsconfig.json",
    "tsconfig.node.json"
)

$allFilesExist = $true
foreach ($file in $tsFiles) {
    if (Test-Path $file) {
        Write-Host "  ✅ $file" -ForegroundColor Green
    } else {
        Write-Host "  ❌ $file - MANQUANT" -ForegroundColor Red
        $allFilesExist = $false
    }
}

if (-not $allFilesExist) {
    Write-Host "`n❌ Certains fichiers TypeScript sont manquants" -ForegroundColor Red
    exit 1
}

Write-Host ""

# 2. Vérifier qu'il n'y a plus de fichiers .jsx
Write-Host "🗑️  Vérification de la suppression des fichiers .jsx..." -ForegroundColor Yellow
$jsxFiles = Get-ChildItem -Path "src" -Filter "*.jsx" -Recurse -ErrorAction SilentlyContinue

if ($jsxFiles.Count -eq 0) {
    Write-Host "  ✅ Aucun fichier .jsx trouvé (migration complète)" -ForegroundColor Green
} else {
    Write-Host "  ⚠️  Fichiers .jsx trouvés:" -ForegroundColor Yellow
    foreach ($file in $jsxFiles) {
        Write-Host "    - $($file.FullName)" -ForegroundColor Yellow
    }
}

Write-Host ""

# 3. Vérifier que node_modules existe
Write-Host "📦 Vérification des dépendances..." -ForegroundColor Yellow
if (Test-Path "node_modules") {
    Write-Host "  ✅ node_modules présent" -ForegroundColor Green
} else {
    Write-Host "  ⚠️  node_modules manquant - Installation des dépendances..." -ForegroundColor Yellow
    corepack pnpm install --frozen-lockfile
}

Write-Host ""

# 4. Vérifier les types TypeScript
Write-Host "🔍 Vérification des types TypeScript..." -ForegroundColor Yellow
$typeCheckResult = corepack pnpm type-check 2>&1
$typeCheckExitCode = $LASTEXITCODE

if ($typeCheckExitCode -eq 0) {
    Write-Host "  ✅ Vérification des types réussie" -ForegroundColor Green
} else {
    Write-Host "  ❌ Erreurs de types détectées:" -ForegroundColor Red
    $typeCheckResult | Select-String "error" | ForEach-Object {
        Write-Host "    $_" -ForegroundColor Red
    }
}

Write-Host ""

# 5. Build de l'application
Write-Host "🏗️  Build de l'application..." -ForegroundColor Yellow
$buildResult = corepack pnpm build 2>&1
$buildExitCode = $LASTEXITCODE

if ($buildExitCode -eq 0) {
    Write-Host "  ✅ Build réussi" -ForegroundColor Green
    
    # Vérifier que wwwroot a été créé
    $wwwrootPath = "..\wwwroot"
    if (Test-Path $wwwrootPath) {
        $wwwrootFiles = Get-ChildItem $wwwrootPath -Recurse -File
        Write-Host "  ✅ wwwroot créé avec $($wwwrootFiles.Count) fichier(s)" -ForegroundColor Green
    } else {
        Write-Host "  ⚠️  wwwroot non trouvé" -ForegroundColor Yellow
    }
} else {
    Write-Host "  ❌ Erreurs de build:" -ForegroundColor Red
    $buildResult | Select-String "error" | ForEach-Object {
        Write-Host "    $_" -ForegroundColor Red
    }
}

Write-Host ""

# Résumé
Write-Host "📊 RÉSUMÉ DE LA MIGRATION" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan

if ($allFilesExist -and $jsxFiles.Count -eq 0 -and $typeCheckExitCode -eq 0 -and $buildExitCode -eq 0) {
    Write-Host "🎉 MIGRATION RÉUSSIE !" -ForegroundColor Green
    Write-Host ""
    Write-Host "✅ Tous les fichiers TypeScript sont présents" -ForegroundColor Green
    Write-Host "✅ Aucun fichier .jsx restant" -ForegroundColor Green
    Write-Host "✅ Vérification des types réussie" -ForegroundColor Green
    Write-Host "✅ Build réussi" -ForegroundColor Green
    Write-Host ""
    Write-Host "📝 L'application GnOuGo.Diff ClientApp est maintenant 100% TypeScript !" -ForegroundColor Green
    Write-Host ""
    Write-Host "🚀 Pour démarrer l'application en mode développement:" -ForegroundColor Cyan
    Write-Host "   cd $clientAppPath" -ForegroundColor Gray
    Write-Host "   corepack pnpm dev" -ForegroundColor Gray
} else {
    Write-Host "⚠️  MIGRATION PARTIELLE" -ForegroundColor Yellow
    Write-Host ""
    if (-not $allFilesExist) {
        Write-Host "❌ Certains fichiers TypeScript sont manquants" -ForegroundColor Red
    }
    if ($jsxFiles.Count -gt 0) {
        Write-Host "⚠️  Des fichiers .jsx sont encore présents" -ForegroundColor Yellow
    }
    if ($typeCheckExitCode -ne 0) {
        Write-Host "❌ Erreurs de types détectées" -ForegroundColor Red
    }
    if ($buildExitCode -ne 0) {
        Write-Host "❌ Erreurs de build" -ForegroundColor Red
    }
}

Write-Host ""

