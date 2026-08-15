[CmdletBinding()]
param(
    [string] $RuntimeIdentifier,
    [switch] $AuditKnownTrimWarnings
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-DefaultRuntimeIdentifier {
    $architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported processor architecture: $($_). Pass -RuntimeIdentifier explicitly." }
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return "win-$architecture"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        return "linux-$architecture"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return "osx-$architecture"
    }

    throw 'Unable to infer a runtime identifier. Pass -RuntimeIdentifier explicitly.'
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $LogPath
    )

    $display = "$FilePath " + ($Arguments -join ' ')
    Write-Host "`n> $display" -ForegroundColor Cyan
    $lines = @(& $FilePath @Arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        $line
    })
    $exitCode = $LASTEXITCODE
    [System.IO.File]::WriteAllLines($LogPath, [string[]] $lines)

    if ($exitCode -ne 0) {
        throw "Command failed with exit code $exitCode. See $LogPath"
    }

    return ,$lines
}

function Get-DiagnosticCode {
    param([Parameter(Mandatory)] [string] $Line)

    if ($Line.Contains('Query precompilation is an experimental feature and should be used with caution.', [System.StringComparison]::Ordinal) -or
        $Line.Contains('NativeAOT support is experimental and can change in the future.', [System.StringComparison]::Ordinal)) {
        return 'EFCORETASKS'
    }

    $match = [regex]::Match($Line, '(?i)\b(?<code>(?:IL|CS|NETSDK|ASP)\d{4})\b')
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups['code'].Value.ToUpperInvariant()
}

function Get-PublishWarningLines {
    param([Parameter(Mandatory)] [string[]] $Lines)

    return @($Lines | Where-Object {
        $_ -match '(?i)\b(?:warning|avertissement)\s+(?:IL|CS|NETSDK|ASP)\d{4}\s*:' -or
        $_ -match '(?i)\b(?:trim|aot) analysis warning\s+IL\d{4}\s*:' -or
        $_ -match '(?i)\b(?:warning|avertissement)\s*:'
    })
}

function Get-AuditOrigin {
    param([Parameter(Mandatory)] [string] $Line)

    if ($Line.Contains('Query precompilation is an experimental feature and should be used with caution.', [System.StringComparison]::Ordinal)) {
        return 'Microsoft.EntityFrameworkCore.Tasks:QueryPrecompilationExperimental'
    }

    if ($Line.Contains('NativeAOT support is experimental and can change in the future.', [System.StringComparison]::Ordinal)) {
        return 'Microsoft.EntityFrameworkCore.Tasks:NativeAotExperimental'
    }

    $assembly = [regex]::Match($Line, '(?i)(?<assembly>[A-Za-z0-9_.-]+\.dll)\s*:\s*(?:warning|avertissement)')
    if ($assembly.Success) {
        return $assembly.Groups['assembly'].Value
    }

    $linker = [regex]::Match(
        $Line,
        '(?i)^(?:ILC|ILLink)\s*:\s*(?:(?:trim|aot) analysis\s+)?warning\s+(?:IL\d{4})\s*:\s*(?<member>[^:]+)')
    if ($linker.Success) {
        return $linker.Groups['member'].Value.Trim()
    }

    $annotatedMember = [regex]::Match(
        $Line,
        "(?i)(?:warning|avertissement)\s+IL\d{4}\s*:\s*(?<kind>Method|Field)\s+'(?<member>[^']+)'")
    if ($annotatedMember.Success) {
        return "$($annotatedMember.Groups['kind'].Value) $($annotatedMember.Groups['member'].Value)"
    }

    $source = [regex]::Match($Line, '(?i)(?<source>(?:src|tests)[\\/].+?\.cs)(?:\(|:)')
    if ($source.Success) {
        return $source.Groups['source'].Value.Replace('\', '/')
    }

    return 'unknown'
}

function Get-FingerprintSetHash {
    param([Parameter(Mandatory)] [string[]] $Fingerprints)

    $canonical = ([string[]] @($Fingerprints | Sort-Object -CaseSensitive)) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-PublishDiagnostics {
    param(
        [Parameter(Mandatory)] [string] $ProjectName,
        [Parameter(Mandatory)] [string[]] $Lines,
        [Parameter(Mandatory)] [hashtable] $KnownAuditFingerprints
    )

    $warnings = Get-PublishWarningLines -Lines $Lines
    if (-not $AuditKnownTrimWarnings) {
        $baseline = @($KnownAuditFingerprints[$ProjectName])
        $unexpectedWarnings = @($warnings | Where-Object {
            $code = Get-DiagnosticCode -Line $_
            $origin = Get-AuditOrigin -Line $_
            $fingerprint = "$code|$origin"
            $code -ne 'EFCORETASKS' -or $fingerprint -notin $baseline
        })
        if ($unexpectedWarnings.Count -gt 0) {
            throw "$ProjectName emitted $($unexpectedWarnings.Count) warning line(s):`n$($unexpectedWarnings -join "`n")"
        }

        $auditedNotices = @($warnings | Where-Object { (Get-DiagnosticCode -Line $_) -eq 'EFCORETASKS' })
        if ($auditedNotices.Count -gt 0) {
            Write-Host "Accepted $($auditedNotices.Count) exact pinned EF Core Tasks experimental notice(s) for $ProjectName." -ForegroundColor DarkYellow
        }

        return
    }

    $baseline = $KnownAuditFingerprints[$ProjectName]
    $observed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($warning in $warnings) {
        $code = Get-DiagnosticCode -Line $warning
        $origin = Get-AuditOrigin -Line $warning
        $fingerprint = "$code|$origin"
        [void] $observed.Add($fingerprint)
    }

    if ($observed.Count -gt 0) {
        Write-Host "Audited $ProjectName fingerprints: $(([string[]] $observed | Sort-Object) -join ', ')" -ForegroundColor DarkYellow
    }

    if ($baseline -is [hashtable]) {
        if ('unknown' -in @($observed | ForEach-Object { ($_ -split '\|', 2)[1] })) {
            throw "The audit could not determine an exact warning origin for $ProjectName."
        }

        $actualHash = Get-FingerprintSetHash -Fingerprints ([string[]] $observed)
        $expectedCount = [int] $baseline['FingerprintCount']
        $expectedHash = [string] $baseline['Sha256']
        if ($observed.Count -ne $expectedCount -or $actualHash -cne $expectedHash) {
            throw "The exact trim/AOT warning fingerprint set for $ProjectName changed. Expected count/hash $expectedCount/$expectedHash; observed $($observed.Count)/$actualHash. $($baseline['Dependencies'])"
        }

        return
    }

    $allowed = @($baseline)
    $unexpected = [System.Collections.Generic.List[string]]::new()
    foreach ($warning in $warnings) {
        $code = Get-DiagnosticCode -Line $warning
        $origin = Get-AuditOrigin -Line $warning
        $fingerprint = "$code|$origin"
        if ($fingerprint -notin $allowed) {
            $unexpected.Add("$fingerprint`n  $warning")
        }
    }

    if ($unexpected.Count -gt 0) {
        throw "The known trim/AOT warning fingerprint for $ProjectName expanded:`n$($unexpected -join "`n")"
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-ForHttpEndpoint {
    param(
        [Parameter(Mandatory)] [uri] $Uri,
        [Parameter(Mandatory)] [System.Diagnostics.Process] $Process,
        [int] $Attempts = 120
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if ($Process.HasExited) {
            throw "Published process exited before $Uri became ready (exit code $($Process.ExitCode))."
        }

        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $response
            }
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for $Uri."
}

function Start-PublishedProcess {
    param(
        [Parameter(Mandatory)] [string] $Executable,
        [Parameter(Mandatory)] [string] $WorkingDirectory,
        [Parameter(Mandatory)] [string] $LogPrefix,
        [string[]] $Arguments = @(),
        [hashtable] $Environment = @{}
    )

    $savedEnvironment = @{}
    try {
        foreach ($entry in $Environment.GetEnumerator()) {
            $savedEnvironment[$entry.Key] = [System.Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [System.Environment]::SetEnvironmentVariable($entry.Key, [string] $entry.Value, 'Process')
        }

        $startParameters = @{
            FilePath = $Executable
            WorkingDirectory = $WorkingDirectory
            RedirectStandardOutput = "$LogPrefix.stdout.log"
            RedirectStandardError = "$LogPrefix.stderr.log"
            PassThru = $true
        }
        if ($Arguments.Count -gt 0) {
            $startParameters.ArgumentList = @($Arguments | ForEach-Object {
                if ($_ -match '\s') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
            })
        }

        return Start-Process @startParameters
    }
    finally {
        foreach ($entry in $Environment.GetEnumerator()) {
            [System.Environment]::SetEnvironmentVariable($entry.Key, $savedEnvironment[$entry.Key], 'Process')
        }
    }
}

function Stop-PublishedProcess {
    param([System.Diagnostics.Process] $Process)

    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        $Process.WaitForExit()
    }
}

function Get-PublishedExecutable {
    param(
        [Parameter(Mandatory)] [string] $PublishDirectory,
        [Parameter(Mandatory)] [string] $ExecutableName
    )

    $extension = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) { '.exe' } else { '' }
    $path = Join-Path $PublishDirectory "$ExecutableName$extension"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected published executable is missing: $path"
    }

    return $path
}

function Invoke-HealthSmoke {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $PublishDirectory,
        [Parameter(Mandatory)] [string] $ExecutableName,
        [int] $Port,
        [hashtable] $Environment = @{},
        [string[]] $Arguments = @()
    )

    if ($Port -le 0) {
        $Port = Get-FreeTcpPort
    }
    $environmentCopy = @{} + $Environment
    $environmentCopy['ASPNETCORE_URLS'] = "http://127.0.0.1:$Port"
    $effectiveArguments = @("--urls=http://127.0.0.1:$Port") + $Arguments
    $process = $null
    try {
        $process = Start-PublishedProcess `
            -Executable (Get-PublishedExecutable $PublishDirectory $ExecutableName) `
            -WorkingDirectory $PublishDirectory `
            -LogPrefix (Join-Path $PublishDirectory $Name) `
            -Arguments $effectiveArguments `
            -Environment $environmentCopy
        $response = Wait-ForHttpEndpoint -Uri "http://127.0.0.1:$Port/health" -Process $process
        if ($response.StatusCode -ne 200) {
            throw "$Name health endpoint returned HTTP $($response.StatusCode)."
        }
    }
    finally {
        Stop-PublishedProcess $process
    }
}

function Invoke-FlowSmoke {
    param([Parameter(Mandatory)] [string] $PublishDirectory)

    $executable = Get-PublishedExecutable $PublishDirectory 'GnOuGo.Flow.Cli'
    $workflow = Join-Path $repoRoot 'src/GnOuGo.Flow.Cli/examples/triage.yaml'
    $log = Join-Path $PublishDirectory 'flow-smoke.log'
    $lines = Invoke-LoggedCommand -FilePath $executable -Arguments @(
        'run', $workflow, '--mock', '--input', 'message=urgent published smoke', 'priority=urgent'
    ) -LogPath $log
    if (($lines -join "`n") -notmatch 'Triage Result') {
        throw "Flow/Jint smoke test did not produce the expected triage output. See $log"
    }
}

function Invoke-AnimationSmoke {
    param([Parameter(Mandatory)] [string] $PublishDirectory)

    $port = Get-FreeTcpPort
    $process = $null
    try {
        $process = Start-PublishedProcess `
            -Executable (Get-PublishedExecutable $PublishDirectory 'GnOuGo.Assets.Animation.Server') `
            -WorkingDirectory $PublishDirectory `
            -LogPrefix (Join-Path $PublishDirectory 'animation-smoke') `
            -Arguments @("--urls=http://127.0.0.1:$port") `
            -Environment @{ ASPNETCORE_URLS = "http://127.0.0.1:$port" }
        [void] (Wait-ForHttpEndpoint -Uri "http://127.0.0.1:$port/health" -Process $process)

        $workflow = Get-Content -LiteralPath (Join-Path $repoRoot 'src/GnOuGo.Flow.Cli/examples/triage.yaml') -Raw
        $body = @{ workflow = $workflow; inputs = @{}; seed = 42; scene = 'Random'; speed = 1.0 } | ConvertTo-Json -Depth 8
        $response = Invoke-RestMethod `
            -Uri "http://127.0.0.1:$port/api/simulations/validate" `
            -Method Post `
            -ContentType 'application/json' `
            -Body $body
        if (-not $response.valid) {
            throw 'Animation/YamlDotNet published validation smoke test rejected the representative workflow.'
        }
    }
    finally {
        Stop-PublishedProcess $process
    }
}

function Invoke-FilesSmoke {
    param(
        [Parameter(Mandatory)] [string] $PublishDirectory,
        [Parameter(Mandatory)] [string] $DataDirectory
    )

    $storageDirectory = Join-Path $DataDirectory 'storage'
    $databasePath = Join-Path $DataDirectory 'files.db'
    [void] (New-Item -ItemType Directory -Path $storageDirectory -Force)
    $environment = @{
        Files__StorageRootPath = $storageDirectory
        Files__DatabasePath = $databasePath
        Files__PurgeIntervalSeconds = '1'
    }
    $tenantA = 'warning-audit-a'
    $tenantB = 'warning-audit-b'
    $headersA = @{ 'X-Tenant-Id' = $tenantA }
    $process = $null

    try {
        $port = Get-FreeTcpPort
        $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$port"
        $process = Start-PublishedProcess `
            -Executable (Get-PublishedExecutable $PublishDirectory 'GnOuGo.Files.Server') `
            -WorkingDirectory $PublishDirectory `
            -LogPrefix (Join-Path $PublishDirectory 'files-new-database') `
            -Arguments @("--urls=http://127.0.0.1:$port") `
            -Environment $environment
        [void] (Wait-ForHttpEndpoint -Uri "http://127.0.0.1:$port/health" -Process $process)

        $payload = [System.Text.Encoding]::UTF8.GetBytes('EF Core Native AOT persistence smoke')
        $created = Invoke-RestMethod `
            -Uri "http://127.0.0.1:$port/api/files?fileName=ef-core-smoke.txt&ttl=01:00:00" `
            -Method Post `
            -Headers $headersA `
            -ContentType 'application/octet-stream' `
            -Body $payload
        if ([string]::IsNullOrWhiteSpace($created.id)) {
            throw 'Files Server upload did not return an id.'
        }

        $listA = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/files" -Headers $headersA
        $listB = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/files" -Headers @{ 'X-Tenant-Id' = $tenantB }
        if ($created.id -notin @($listA.files.id) -or @($listB.files).Count -ne 0) {
            throw 'Files Server tenant isolation smoke test failed.'
        }

        $downloadPath = Join-Path $DataDirectory 'downloaded-smoke.txt'
        Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/files/$($created.id)" -Headers $headersA -OutFile $downloadPath
        if ([System.IO.File]::ReadAllText($downloadPath) -ne 'EF Core Native AOT persistence smoke') {
            throw 'Files Server download did not match the uploaded payload.'
        }
    }
    finally {
        Stop-PublishedProcess $process
        $process = $null
    }

    try {
        $port = Get-FreeTcpPort
        $environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$port"
        $process = Start-PublishedProcess `
            -Executable (Get-PublishedExecutable $PublishDirectory 'GnOuGo.Files.Server') `
            -WorkingDirectory $PublishDirectory `
            -LogPrefix (Join-Path $PublishDirectory 'files-existing-database') `
            -Arguments @("--urls=http://127.0.0.1:$port") `
            -Environment $environment
        [void] (Wait-ForHttpEndpoint -Uri "http://127.0.0.1:$port/health" -Process $process)

        $existing = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/files" -Headers $headersA
        if ($created.id -notin @($existing.files.id)) {
            throw 'Files Server did not reopen its existing compatible EF Core database.'
        }

        $expiring = Invoke-RestMethod `
            -Uri "http://127.0.0.1:$port/api/files?fileName=expires.txt&ttl=00:00:01" `
            -Method Post `
            -Headers $headersA `
            -ContentType 'application/octet-stream' `
            -Body ([System.Text.Encoding]::UTF8.GetBytes('expires'))
        Start-Sleep -Seconds 3
        $afterPurge = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/files" -Headers $headersA
        if ($expiring.id -in @($afterPurge.files.id)) {
            throw 'Files Server TTL purge smoke test left expired EF metadata visible.'
        }
    }
    finally {
        Stop-PublishedProcess $process
    }
}

function Invoke-AgentServerSmoke {
    param(
        [Parameter(Mandatory)] [string] $PublishDirectory,
        [Parameter(Mandatory)] [string] $DataDirectory
    )

    $appPort = Get-FreeTcpPort
    $grpcPort = Get-FreeTcpPort
    $httpPort = Get-FreeTcpPort
    $arguments = @(
        "--urls=http://127.0.0.1:$appPort",
        "--OtlpCollector:GrpcPort=$grpcPort",
        "--OtlpCollector:HttpPort=$httpPort",
        '--OpenTelemetry:Enabled=false',
        "--Agent:DatabasePath=$(Join-Path $DataDirectory 'agent.db')",
        "--KeyVault:DatabasePath=$(Join-Path $DataDirectory 'keyvault.db')",
        "--DocsIngestorMcp:DatabasePath=$(Join-Path $DataDirectory 'docs-mcp.db')",
        "--DocsIngestorMcp:VectorDatabasePath=$(Join-Path $DataDirectory 'docs-vectors.db')",
        "--DocsIngestorMcp:OriginalsDirectory=$(Join-Path $DataDirectory 'originals')",
        "--Files:DatabasePath=$(Join-Path $DataDirectory 'files.db')",
        "--Files:StorageRootPath=$(Join-Path $DataDirectory 'files')",
        "--Database:Path=$(Join-Path $DataDirectory 'telemetry.db')"
    )
    $process = $null
    try {
        $process = Start-PublishedProcess `
            -Executable (Get-PublishedExecutable $PublishDirectory 'GnOuGo.Agent.Server') `
            -WorkingDirectory $PublishDirectory `
            -LogPrefix (Join-Path $PublishDirectory 'agent-server-smoke') `
            -Arguments $arguments
        [void] (Wait-ForHttpEndpoint -Uri "http://127.0.0.1:$appPort/health" -Process $process -Attempts 240)

        $root = Invoke-WebRequest -Uri "http://127.0.0.1:$appPort/" -SkipHttpErrorCheck
        if ($root.StatusCode -ne 200) {
            throw "Published Agent Server static UI returned HTTP $($root.StatusCode)."
        }

        $negotiate = Invoke-WebRequest `
            -Uri "http://127.0.0.1:$appPort/_blazor/negotiate?negotiateVersion=1" `
            -Method Post `
            -ContentType 'text/plain;charset=UTF-8' `
            -SkipHttpErrorCheck
        if ($negotiate.StatusCode -ne 200) {
            throw "Published Agent Server Blazor negotiation returned HTTP $($negotiate.StatusCode)."
        }
    }
    finally {
        Stop-PublishedProcess $process
    }
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $RuntimeIdentifier = Get-DefaultRuntimeIdentifier
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ("gnougo-warning-free-" + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $temporaryRoot)

$frontends = @(
    'src/GnOuGo.Agent.Server/ClientApp',
    'src/GnOuGo.Assets.Animation.Server/ClientApp',
    'src/GnOuGo.Diff.Server/ClientApp',
    'src/GnOuGo.DocIngestor.Server/ClientApp',
    'src/GnOuGo.Files.Server/ClientApp',
    'src/GnOuGo.Flow.Server/ClientApp',
    'src/GnOuGo.KeyVault.Server/ClientApp',
    'src/GnOuGo.OtlpCollector.Server/ClientApp'
)

$publishProfiles = @(
    [pscustomobject]@{ Name = 'GnOuGo.Cmd.Mcp'; Project = 'src/GnOuGo.Cmd.Mcp/GnOuGo.Cmd.Mcp.csproj'; NativeAot = $true; Executable = 'GnOuGo.Cmd.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.Document.Mcp'; Project = 'src/GnOuGo.Document.Mcp/GnOuGo.Document.Mcp.csproj'; NativeAot = $true; Executable = 'GnOuGo.Document.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.Git.Mcp'; Project = 'src/GnOuGo.Git.Mcp/GnOuGo.Git.Mcp.csproj'; NativeAot = $true; Executable = 'GnOuGo.Git.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.GithubCopilot.Mcp'; Project = 'src/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp.csproj'; NativeAot = $true; Executable = 'GnOuGo.GithubCopilot.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.DocIngestor.Mcp'; Project = 'src/GnOuGo.DocIngestor.Mcp/GnOuGo.DocIngestor.Mcp.csproj'; NativeAot = $true; Executable = 'GnOuGo.DocIngestor.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.Flow.Cli'; Project = 'src/GnOuGo.Flow.Cli/GnOuGo.Flow.Cli.csproj'; NativeAot = $true; Executable = 'GnOuGo.Flow.Cli' },
    [pscustomobject]@{ Name = 'GnOuGo.Files.Server'; Project = 'src/GnOuGo.Files.Server/GnOuGo.Files.Server.csproj'; NativeAot = $true; Executable = 'GnOuGo.Files.Server' },
    [pscustomobject]@{ Name = 'GnOuGo.Assets.Animation.Server'; Project = 'src/GnOuGo.Assets.Animation.Server/GnOuGo.Assets.Animation.Server.csproj'; NativeAot = $true; Executable = 'GnOuGo.Assets.Animation.Server' },
    [pscustomobject]@{ Name = 'GnOuGo.Agent.Mcp'; Project = 'src/GnOuGo.Agent.Mcp/GnOuGo.Agent.Mcp.csproj'; NativeAot = $false; Executable = 'GnOuGo.Agent.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.KeyVault.Mcp'; Project = 'src/GnOuGo.KeyVault.Mcp/GnOuGo.KeyVault.Mcp.csproj'; NativeAot = $false; Executable = 'GnOuGo.KeyVault.Mcp' },
    [pscustomobject]@{ Name = 'GnOuGo.OtlpCollector.Server'; Project = 'src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj'; NativeAot = $false; Executable = 'GnOuGo.OtlpCollector.Server' },
    [pscustomobject]@{ Name = 'GnOuGo.Agent.Server'; Project = 'src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj'; NativeAot = $false; Executable = 'GnOuGo.Agent.Server' },
    [pscustomobject]@{ Name = 'GnOuGo.Agent.Desktop'; Project = 'src/GnOuGo.Agent.Desktop/GnOuGo.Agent.Desktop.csproj'; NativeAot = $false; Executable = 'GnOuGo.Agent' }
)

# Fingerprints intentionally omit package versions and absolute paths while retaining
# the diagnostic code and exact originating assembly/member. Small dependency sets are
# listed directly. Large framework sets use a deterministic count and SHA-256 over the
# sorted exact fingerprints, so any addition, removal, or changed origin fails audit.
$knownAuditFingerprints = @{
    'GnOuGo.Flow.Cli' = @(
        'IL2026|Jint.Options.Apply(Engine)',
        'IL2026|Jint.Options.<>c__DisplayClass77_0.<Apply>b__0(JsValue,JsValue[])',
        'IL2026|Jint.DefaultObjectConverter.ConvertSystemTextJsonValue(Engine,JsonNode)',
        'IL2026|Jint.Runtime.Interop.DefaultTypeConverter.BuildDelegate(Type,Func`3<JsValue,JsValue[],JsValue>,Expression)',
        'IL2104|Jint.dll',
        'IL3053|Jint.dll'
    )
    'GnOuGo.GithubCopilot.Mcp' = @(
        'IL2104|Microsoft.EntityFrameworkCore.dll',
        'IL3053|Microsoft.EntityFrameworkCore.dll'
    )
    'GnOuGo.Assets.Animation.Server' = @('IL2104|YamlDotNet.dll', 'IL3053|YamlDotNet.dll')
    'GnOuGo.Files.Server' = @(
        'IL2104|Microsoft.EntityFrameworkCore.dll',
        'IL2104|Microsoft.EntityFrameworkCore.Relational.dll',
        'IL2104|Microsoft.EntityFrameworkCore.Sqlite.dll',
        'IL3053|Microsoft.EntityFrameworkCore.Abstractions.dll',
        'IL3053|Microsoft.EntityFrameworkCore.dll',
        'IL3053|Microsoft.EntityFrameworkCore.Relational.dll',
        'IL3053|Microsoft.EntityFrameworkCore.Sqlite.dll',
        'IL3002|Microsoft.EntityFrameworkCore.Infrastructure.SpatialiteLoader.FindExtension()',
        'IL3002|Microsoft.Extensions.DependencyModel.DependencyContext..cctor()',
        'CS8669|src/GnOuGo.Files.Server/obj/Release/net10.0/FilesMetadataRepository.EFInterceptors.FilesDbContext.g.cs',
        'CS9270|src/GnOuGo.Files.Server/obj/Release/net10.0/FilesMetadataRepository.EFInterceptors.FilesDbContext.g.cs',
        'EFCORETASKS|Microsoft.EntityFrameworkCore.Tasks:QueryPrecompilationExperimental',
        'EFCORETASKS|Microsoft.EntityFrameworkCore.Tasks:NativeAotExperimental'
    )
    'GnOuGo.Agent.Mcp' = @{
        FingerprintCount = 86
        Sha256 = '2575e0c408614d8bb2067f7a0212b734531b10e1678e28786d105071dfdcf39b'
        Dependencies = 'Pinned .NET 10 ASP.NET Core partial-trim and EF Core SQLite closure.'
    }
    'GnOuGo.KeyVault.Mcp' = @{
        FingerprintCount = 3
        Sha256 = '50134311ad2418d4133adb8427eec639c5739a1f61bdaeeb5629624d3c7e0484'
        Dependencies = 'Pinned EF Core SQLite closure.'
    }
    'GnOuGo.OtlpCollector.Server' = @{
        FingerprintCount = 88
        Sha256 = '9fb6fbd5118fb83ed72ab0492bf7e07d35e7048bdc97e9023c8c8639cc52d4a4'
        Dependencies = 'Pinned .NET 10 ASP.NET Core partial-trim and EF Core SQLite closure.'
    }
    'GnOuGo.Agent.Server' = @{
        FingerprintCount = 102
        Sha256 = '4b8c1ba01af1eb67fd9513c5b7b3019e2e5be6087aafda4b5c6dde50ce2d6b0d'
        Dependencies = 'Pinned .NET 10 ASP.NET Core/Blazor, EF Core SQLite, Jint, YamlDotNet, ML.Tokenizers, and generated Routes component closure.'
    }
    'GnOuGo.Agent.Desktop' = @{
        FingerprintCount = 101
        Sha256 = '936ea2746f534c3a45a15e1fe7db28538c753835dca594e1d4866ff52d47715a'
        Dependencies = 'Pinned .NET 10 ASP.NET Core/Blazor, EF Core SQLite, Jint, YamlDotNet, ML.Tokenizers, and embedded Agent Server closure.'
    }
}

$publishDirectories = @{}
try {
    Write-Host "Runtime identifier: $RuntimeIdentifier" -ForegroundColor Green
    Write-Host "Temporary output: $temporaryRoot" -ForegroundColor Green

    foreach ($frontend in $frontends) {
        $frontendPath = Join-Path $repoRoot $frontend
        $safeName = $frontend.Replace('/', '-').Replace('\', '-')
        [void] (Invoke-LoggedCommand -FilePath 'corepack' -Arguments @(
            'pnpm', '--dir', $frontendPath, 'install', '--frozen-lockfile', '--prefer-offline'
        ) -LogPath (Join-Path $temporaryRoot "$safeName-install.log"))
        $frontendOutput = Invoke-LoggedCommand -FilePath 'corepack' -Arguments @(
            'pnpm', '--dir', $frontendPath, 'build'
        ) -LogPath (Join-Path $temporaryRoot "$safeName-build.log")
        $frontendWarnings = @($frontendOutput | Where-Object {
            $_ -match '(?i)\bwarning\b' -or $_ -match '(?i)some chunks are larger' -or $_ -match '^\s*\(!\)'
        })
        if ($frontendWarnings.Count -gt 0) {
            throw "$frontend emitted warning output:`n$($frontendWarnings -join "`n")"
        }
    }

    foreach ($profile in $publishProfiles) {
        $outputDirectory = Join-Path $temporaryRoot "publish/$($profile.Name)"
        [void] (New-Item -ItemType Directory -Path $outputDirectory -Force)
        $publishDirectories[$profile.Name] = $outputDirectory
        $arguments = @(
            'publish', (Join-Path $repoRoot $profile.Project),
            '-c', 'Release',
            '-r', $RuntimeIdentifier,
            '--self-contained', 'true',
            '-o', $outputDirectory,
            '-m:1',
            '-p:PublishTrimmed=true',
            "-p:PublishAot=$($profile.NativeAot.ToString().ToLowerInvariant())",
            '-p:PublishSingleFile=true',
            '-p:SkipClientBuild=true',
            '-p:SkipModelMetadataGeneration=true',
            "-p:AuditKnownTrimWarnings=$($AuditKnownTrimWarnings.IsPresent.ToString().ToLowerInvariant())"
        )
        if ($AuditKnownTrimWarnings) {
            $arguments += '-p:SuppressTrimAnalysisWarnings=false'
        }

        $output = Invoke-LoggedCommand -FilePath 'dotnet' -Arguments $arguments -LogPath (Join-Path $temporaryRoot "$($profile.Name).publish.log")
        Assert-PublishDiagnostics -ProjectName $profile.Name -Lines $output -KnownAuditFingerprints $knownAuditFingerprints
        [void] (Get-PublishedExecutable $outputDirectory $profile.Executable)
    }

    Invoke-FlowSmoke -PublishDirectory $publishDirectories['GnOuGo.Flow.Cli']
    Invoke-AnimationSmoke -PublishDirectory $publishDirectories['GnOuGo.Assets.Animation.Server']
    Invoke-FilesSmoke `
        -PublishDirectory $publishDirectories['GnOuGo.Files.Server'] `
        -DataDirectory (Join-Path $temporaryRoot 'smoke/files')

    Invoke-HealthSmoke `
        -Name 'agent-mcp-smoke' `
        -PublishDirectory $publishDirectories['GnOuGo.Agent.Mcp'] `
        -ExecutableName 'GnOuGo.Agent.Mcp' `
        -Environment @{ Agent__DatabasePath = (Join-Path $temporaryRoot 'smoke/agent-mcp.db') }
    Invoke-HealthSmoke `
        -Name 'keyvault-mcp-smoke' `
        -PublishDirectory $publishDirectories['GnOuGo.KeyVault.Mcp'] `
        -ExecutableName 'GnOuGo.KeyVault.Mcp' `
        -Environment @{ KeyVault__DatabasePath = (Join-Path $temporaryRoot 'smoke/keyvault-mcp.db') }

    $otlpGrpcPort = Get-FreeTcpPort
    $otlpHttpPort = Get-FreeTcpPort
    Invoke-HealthSmoke `
        -Name 'otlp-smoke' `
        -PublishDirectory $publishDirectories['GnOuGo.OtlpCollector.Server'] `
        -ExecutableName 'GnOuGo.OtlpCollector.Server' `
        -Port $otlpHttpPort `
        -Arguments @(
            "--Kestrel:Endpoints:Grpc:Url=http://127.0.0.1:$otlpGrpcPort",
            "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:$otlpHttpPort",
            "--Database:Path=$(Join-Path $temporaryRoot 'smoke/otlp.db')"
        )

    Invoke-AgentServerSmoke `
        -PublishDirectory $publishDirectories['GnOuGo.Agent.Server'] `
        -DataDirectory (Join-Path $temporaryRoot 'smoke/agent-server')

    $desktopDirectory = $publishDirectories['GnOuGo.Agent.Desktop']
    foreach ($toolName in @('GnOuGo.Browser.Mcp', 'GnOuGo.Cmd.Mcp', 'GnOuGo.Document.Mcp', 'GnOuGo.GithubCopilot.Mcp', 'GnOuGo.Git.Mcp')) {
        [void] (Get-PublishedExecutable (Join-Path $desktopDirectory "tools/$toolName") $toolName)
    }
    if (-not (Test-Path -LiteralPath (Join-Path $desktopDirectory 'wwwroot/_framework/blazor.web.js') -PathType Leaf)) {
        throw 'Desktop packaging is missing wwwroot/_framework/blazor.web.js.'
    }

    Write-Host "`nAll Vite builds, publish profiles, warning checks, and published-binary smoke tests passed." -ForegroundColor Green
}
finally {
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith('gnougo-warning-free-', [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
