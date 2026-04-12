[CmdletBinding()]
param(
    [string]$RootDir = "",
    [string]$TestsProjectPath,
    [string]$BlameHangTimeout = "5m",
    [string]$TestFilter = "",
    [switch]$SkipAssetInspection,
    [switch]$SkipTestDiagnostics,
    [switch]$RunFullProjectDiagnostic,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RootDir)) {
    $RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Join-PathParts {
    param([string[]]$Parts)

    if ($Parts.Count -eq 0) {
        throw "Join-PathParts requires at least one path segment."
    }

    $path = $Parts[0]
    for ($i = 1; $i -lt $Parts.Count; $i++) {
        $path = Join-Path $path $Parts[$i]
    }

    return $path
}

function Get-RelativeDisplayPath {
    param(
        [string]$BasePath,
        [string]$ChildPath
    )

    $resolvedBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $resolvedChild = [System.IO.Path]::GetFullPath($ChildPath)

    if ($resolvedChild.StartsWith($resolvedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $resolvedChild.Substring($resolvedBase.Length)
    }

    return $resolvedChild
}

function Write-Section {
    param([string]$Title)

    Write-Host ""
    Write-Host ("=" * 78)
    Write-Host $Title
    Write-Host ("=" * 78)
}

function Add-SummaryLine {
    param(
        [System.Collections.Generic.List[string]]$Summary,
        [string]$Line
    )

    $Summary.Add($Line) | Out-Null
    Write-Host $Line
}

function Get-TestClassNames {
    param([string]$TestsDirectory)

    $classes = New-Object 'System.Collections.Generic.List[string]'
    $files = Get-ChildItem $TestsDirectory -Filter '*Tests.cs' -File | Sort-Object Name

    foreach ($file in $files) {
        $content = Get-Content $file.FullName -Raw
        $matches = [regex]::Matches($content, 'class\s+([A-Za-z_][A-Za-z0-9_]*Tests)\b')
        foreach ($match in $matches) {
            $name = $match.Groups[1].Value
            if (-not [string]::IsNullOrWhiteSpace($name) -and -not $classes.Contains($name)) {
                $classes.Add($name) | Out-Null
            }
        }
    }

    return $classes.ToArray()
}

function Invoke-LoggedCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$LogPath,
        [switch]$IgnoreExitCode
    )

    Write-Host ""
    Write-Host ("> {0} {1}" -f $FilePath, ($Arguments -join ' '))

    $previousErrorActionPreference = $ErrorActionPreference

    Push-Location $WorkingDirectory
    try {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }

    if (-not $IgnoreExitCode -and $exitCode -ne 0) {
        throw "Command failed with exit code $exitCode. See log: $LogPath"
    }

    return [int]$exitCode
}

if (-not $TestsProjectPath) {
    $TestsProjectPath = Join-PathParts @($RootDir, 'tests', 'GnOuGo.Agent.Server.Tests', 'GnOuGo.Agent.Server.Tests.csproj')
}

$serverProjectDir = Join-PathParts @($RootDir, 'src', 'GnOuGo.Agent.Server')
$serverWwwRoot = Join-Path $serverProjectDir 'wwwroot'
$appRazorPath = Join-PathParts @($serverProjectDir, 'Components', 'App.razor')
$viteConfigPath = Join-PathParts @($serverProjectDir, 'ClientApp', 'vite.config.ts')
$desktopProjectPath = Join-PathParts @($RootDir, 'src', 'GnOuGo.Agent.Desktop', 'GnOuGo.Agent.Desktop.csproj')
$serverReadmePath = Join-Path $serverProjectDir 'README.md'
$testsDirectory = Split-Path $TestsProjectPath -Parent
$resultsRoot = Join-PathParts @(
    $RootDir,
    'artifacts',
    'tmp',
    'test-results',
    'agent-server-diagnostics',
    (Get-Date -Format 'yyyyMMdd-HHmmss')
)

New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null

$summary = New-Object 'System.Collections.Generic.List[string]'
$summaryPath = Join-Path $resultsRoot 'summary.txt'

$desktopOutputDirectories = New-Object 'System.Collections.Generic.List[string]'
$desktopPublishRoot = Join-PathParts @($RootDir, 'artifacts', 'publish', 'desktop')
$desktopTmpRoot = Join-PathParts @($RootDir, 'artifacts', 'tmp')

if (Test-Path $desktopPublishRoot) {
    foreach ($directory in Get-ChildItem $desktopPublishRoot -Directory | Sort-Object Name) {
        $desktopOutputDirectories.Add($directory.FullName) | Out-Null
    }
}

if (Test-Path $desktopTmpRoot) {
    foreach ($directory in Get-ChildItem $desktopTmpRoot -Directory | Where-Object { $_.Name -like 'desktop-*-check' } | Sort-Object Name) {
        if (-not $desktopOutputDirectories.Contains($directory.FullName)) {
            $desktopOutputDirectories.Add($directory.FullName) | Out-Null
        }
    }
}

if (-not $SkipAssetInspection) {
    Write-Section "Asset inspection"

    $appRazorContent = Get-Content $appRazorPath -Raw
    $viteConfigContent = Get-Content $viteConfigPath -Raw
    $desktopProjectContent = Get-Content $desktopProjectPath -Raw
    $readmeContent = Get-Content $serverReadmePath -Raw

    $referencedCss = @([regex]::Matches($appRazorContent, 'href="([^"]+\.css)"') | ForEach-Object { $_.Groups[1].Value })
    $referencedScripts = @([regex]::Matches($appRazorContent, 'src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })

    $sourceFiles = Get-ChildItem $serverWwwRoot -Recurse -File | Sort-Object FullName
    $sourceCssFiles = @($sourceFiles | Where-Object { $_.Extension -eq '.css' } | ForEach-Object { Get-RelativeDisplayPath $serverWwwRoot $_.FullName })
    $sourceJsFiles = @($sourceFiles | Where-Object { $_.Extension -eq '.js' } | ForEach-Object { Get-RelativeDisplayPath $serverWwwRoot $_.FullName })

    $sourceRootAppCss = Join-Path $serverWwwRoot 'app.css'
    $sourceUiAppCss = Join-PathParts @($serverWwwRoot, 'ui', 'app.css')
    $sourceAssetsAppCss = Join-PathParts @($serverWwwRoot, 'assets', 'app.css')

    Add-SummaryLine $summary ("App.razor CSS references      : {0}" -f ($(if ($referencedCss.Count -gt 0) { $referencedCss -join ', ' } else { '<none>' })))
    Add-SummaryLine $summary ("App.razor script references   : {0}" -f ($(if ($referencedScripts.Count -gt 0) { $referencedScripts -join ', ' } else { '<none>' })))
    Add-SummaryLine $summary ("Vite outputs into wwwroot/ui  : {0}" -f ($viteConfigContent -match 'wwwroot/ui'))
    Add-SummaryLine $summary ("Desktop copies server wwwroot : {0}" -f ($desktopProjectContent -match '\.\.\\GnOuGo\.Agent\.Server\\wwwroot\\\*\*\\\*'))
    Add-SummaryLine $summary ("Source CSS files              : {0}" -f ($(if ($sourceCssFiles.Count -gt 0) { $sourceCssFiles -join ', ' } else { '<none>' })))
    Add-SummaryLine $summary ("Source JS sample              : {0}" -f ($(if ($sourceJsFiles.Count -gt 0) { ($sourceJsFiles | Select-Object -First 8) -join ', ' } else { '<none>' })))
    Add-SummaryLine $summary ("Source has wwwroot\\app.css   : {0}" -f (Test-Path $sourceRootAppCss))
    Add-SummaryLine $summary ("Source has wwwroot\\ui\\app.css: {0}" -f (Test-Path $sourceUiAppCss))
    Add-SummaryLine $summary ("Source has wwwroot\\assets\\app.css: {0}" -f (Test-Path $sourceAssetsAppCss))
    Add-SummaryLine $summary ("README still mentions assets/app.css: {0}" -f ($readmeContent -match 'wwwroot/assets/app\.css'))

    if ($desktopOutputDirectories.Count -eq 0) {
        Add-SummaryLine $summary 'Desktop outputs inspected     : <none found under artifacts/publish/desktop or artifacts/tmp/desktop-*-check>'
    }
    else {
        foreach ($desktopOutputDirectory in $desktopOutputDirectories) {
            $displayName = Get-RelativeDisplayPath $RootDir $desktopOutputDirectory
            $desktopUiAppCss = Join-PathParts @($desktopOutputDirectory, 'wwwroot', 'ui', 'app.css')
            $desktopRootAppCss = Join-PathParts @($desktopOutputDirectory, 'wwwroot', 'app.css')
            $desktopManifestFiles = @(Get-ChildItem $desktopOutputDirectory -Filter '*.staticwebassets.*.json' -File -ErrorAction SilentlyContinue | Sort-Object Name)
            $desktopCssFiles = @(Get-ChildItem (Join-Path $desktopOutputDirectory 'wwwroot') -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -eq '.css' } |
                ForEach-Object { Get-RelativeDisplayPath $desktopOutputDirectory $_.FullName })

            Add-SummaryLine $summary ("Desktop output                : {0}" -f $displayName)
            Add-SummaryLine $summary ("  has wwwroot\\ui\\app.css    : {0}" -f (Test-Path $desktopUiAppCss))
            Add-SummaryLine $summary ("  has wwwroot\\app.css        : {0}" -f (Test-Path $desktopRootAppCss))
            Add-SummaryLine $summary ("  css files                   : {0}" -f ($(if ($desktopCssFiles.Count -gt 0) { $desktopCssFiles -join ', ' } else { '<none>' })))
            Add-SummaryLine $summary ("  static web asset manifests  : {0}" -f ($(if ($desktopManifestFiles.Count -gt 0) { ($desktopManifestFiles | ForEach-Object { $_.Name }) -join ', ' } else { '<none>' })))
        }
    }

    $desktopWithUiCss = @($desktopOutputDirectories | Where-Object { Test-Path (Join-PathParts @($_, 'wwwroot', 'ui', 'app.css')) })
    $desktopWithRootCss = @($desktopOutputDirectories | Where-Object { Test-Path (Join-PathParts @($_, 'wwwroot', 'app.css')) })

    $verdict = if ((Test-Path $sourceUiAppCss) -and -not (Test-Path $sourceRootAppCss)) {
        'Verdict: the current expected CSS path is wwwroot\\ui\\app.css. A validation expecting wwwroot\\app.css is obsolete.'
    }
    elseif (Test-Path $sourceRootAppCss) {
        'Verdict: wwwroot\\app.css exists in source, so a root-level expectation is still valid.'
    }
    else {
        'Verdict: neither wwwroot\\app.css nor wwwroot\\ui\\app.css exists in source. Rebuild the client assets before diagnosing publish validation.'
    }

    Add-SummaryLine $summary $verdict
    Add-SummaryLine $summary ("Desktop outputs with ui/app.css : {0}/{1}" -f $desktopWithUiCss.Count, $desktopOutputDirectories.Count)
    Add-SummaryLine $summary ("Desktop outputs with root app.css: {0}/{1}" -f $desktopWithRootCss.Count, $desktopOutputDirectories.Count)
}

if (-not $SkipTestDiagnostics) {
    Write-Section "Server test diagnostics"

    $testClassNames = @(Get-TestClassNames $testsDirectory)
    $suggestedFilters = @($testClassNames | ForEach-Object { 'FullyQualifiedName~' + $_ })
    $defaultFilter = $TestFilter

    if ([string]::IsNullOrWhiteSpace($defaultFilter)) {
        $hostClass = $testClassNames | Where-Object { $_ -match 'Host' } | Select-Object -First 1
        if ($hostClass) {
            $defaultFilter = 'FullyQualifiedName~' + $hostClass
        }
    }

    Add-SummaryLine $summary ("Tests project                 : {0}" -f (Get-RelativeDisplayPath $RootDir $TestsProjectPath))
    Add-SummaryLine $summary ("Disable test parallelization  : true (AssemblyInfo.cs)")
    Add-SummaryLine $summary ("Suggested test filters        : {0}" -f ($(if ($suggestedFilters.Count -gt 0) { ($suggestedFilters | Select-Object -First 8) -join ', ' } else { '<none>' })))
    Add-SummaryLine $summary ("Default targeted filter       : {0}" -f ($(if ([string]::IsNullOrWhiteSpace($defaultFilter)) { '<none>' } else { $defaultFilter })))

    if (-not $NoRestore) {
        $restoreLog = Join-Path $resultsRoot 'restore.log'
        $restoreArguments = @('restore', $TestsProjectPath, '/p:SkipClientBuild=true')
        [void](Invoke-LoggedCommand -FilePath 'dotnet' -Arguments $restoreArguments -WorkingDirectory $RootDir -LogPath $restoreLog)
        Add-SummaryLine $summary ("Restore log                   : {0}" -f (Get-RelativeDisplayPath $RootDir $restoreLog))
    }

    $listTestsLog = Join-Path $resultsRoot 'list-tests.log'
    $listTestArguments = @('test', $TestsProjectPath, '--list-tests', '/p:SkipClientBuild=true')
    $listTestArguments += '--no-restore'

    $listExitCode = Invoke-LoggedCommand -FilePath 'dotnet' -Arguments $listTestArguments -WorkingDirectory $RootDir -LogPath $listTestsLog -IgnoreExitCode
    Add-SummaryLine $summary ("List-tests exit code          : {0}" -f $listExitCode)
    Add-SummaryLine $summary ("List-tests log                : {0}" -f (Get-RelativeDisplayPath $RootDir $listTestsLog))

    $diagnosticFilter = $defaultFilter
    $diagnosticName = 'targeted-diagnostic'
    $diagnosticArguments = @(
        'test',
        $TestsProjectPath,
        '-m:1',
        '/p:SkipClientBuild=true',
        '--no-restore',
        '--blame-hang',
        '--blame-hang-timeout',
        $BlameHangTimeout,
        '--results-directory',
        $resultsRoot,
        '-l',
        'console;verbosity=minimal',
        '-l',
        'trx;LogFileName=targeted-diagnostic.trx'
    )

    if (-not [string]::IsNullOrWhiteSpace($diagnosticFilter)) {
        $diagnosticArguments += '--filter'
        $diagnosticArguments += $diagnosticFilter
    }
    else {
        $diagnosticName = 'full-project-diagnostic'
    }

    $diagnosticLog = Join-Path $resultsRoot ($diagnosticName + '.log')
    $diagnosticExitCode = Invoke-LoggedCommand -FilePath 'dotnet' -Arguments $diagnosticArguments -WorkingDirectory $RootDir -LogPath $diagnosticLog -IgnoreExitCode
    Add-SummaryLine $summary ("Primary diagnostic exit code  : {0}" -f $diagnosticExitCode)
    Add-SummaryLine $summary ("Primary diagnostic log        : {0}" -f (Get-RelativeDisplayPath $RootDir $diagnosticLog))

    $diagnosticArtifacts = @(Get-ChildItem $resultsRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.trx', '.dmp', '.xml' } |
        Sort-Object FullName |
        ForEach-Object { Get-RelativeDisplayPath $RootDir $_.FullName })

    if ($diagnosticArtifacts.Count -gt 0) {
        Add-SummaryLine $summary ("Diagnostic artifacts         : {0}" -f ($diagnosticArtifacts -join ', '))
    }

    if ($RunFullProjectDiagnostic -and -not [string]::IsNullOrWhiteSpace($diagnosticFilter)) {
        $fullProjectLog = Join-Path $resultsRoot 'full-project-diagnostic.log'
        $fullProjectArguments = @(
            'test',
            $TestsProjectPath,
            '-m:1',
            '/p:SkipClientBuild=true',
            '--no-restore',
            '--blame-hang',
            '--blame-hang-timeout',
            $BlameHangTimeout,
            '--results-directory',
            $resultsRoot,
            '-l',
            'console;verbosity=minimal',
            '-l',
            'trx;LogFileName=full-project-diagnostic.trx'
        )

        $fullProjectExitCode = Invoke-LoggedCommand -FilePath 'dotnet' -Arguments $fullProjectArguments -WorkingDirectory $RootDir -LogPath $fullProjectLog -IgnoreExitCode
        Add-SummaryLine $summary ("Full-project exit code        : {0}" -f $fullProjectExitCode)
        Add-SummaryLine $summary ("Full-project diagnostic log   : {0}" -f (Get-RelativeDisplayPath $RootDir $fullProjectLog))
    }
}

$summary | Set-Content -Path $summaryPath

Write-Host ""
Write-Host "Summary written to: $(Get-RelativeDisplayPath $RootDir $summaryPath)"






