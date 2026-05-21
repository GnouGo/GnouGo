<#
.SYNOPSIS
    Updates model pricing inside model-metadata.json and regenerates the C# model metadata catalog.
.DESCRIPTION
    model-metadata.json is the single source for builtin model limits, capabilities and pricing.
    This script optionally downloads pricing from LiteLLM, merges it into each model's
    pricing object, then regenerates ModelMetadataCatalog.Generated.cs.
.PARAMETER DownloadLatest
    Fetches latest pricing from LiteLLM GitHub and merges it into model-metadata.json.
.PARAMETER JsonPath
    Path to model-metadata.json.
.PARAMETER OutputPath
    Path for ModelMetadataCatalog.Generated.cs.
#>
param(
    [switch]$DownloadLatest,
    [string]$JsonPath = "$PSScriptRoot\..\src\GnOuGo.AI.Core\Telemetry\model-metadata.json",
    [string]$OutputPath = "$PSScriptRoot\..\src\GnOuGo.AI.Core\ModelMetadataCatalog.Generated.cs"
)
$ErrorActionPreference = "Stop"
$resolved = Resolve-Path $JsonPath -ErrorAction SilentlyContinue
if ($resolved) { $JsonPath = $resolved.Path }
Write-Host "Metadata JSON path : $JsonPath"
Write-Host "Generated output   : $OutputPath"
function Get-JsonPropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}
function Ensure-ObjectProperty($Object, [string]$Name) {
    $value = Get-JsonPropertyValue $Object $Name
    if ($null -eq $value) {
        $value = [PSCustomObject]@{}
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $value
    }
    return $value
}
function Set-ObjectProperty($Object, [string]$Name, $Value) {
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}
function Normalize-ProviderType([string]$Provider) {
    if ([string]::IsNullOrWhiteSpace($Provider)) { return $null }
    switch ($Provider.Trim().ToLowerInvariant()) {
        "anthropic" { return "claude" }
        "claude" { return "claude" }
        "github_copilot" { return "copilot" }
        "github" { return "copilot" }
        "copilot" { return "copilot" }
        "azure" { return "openai" }
        "text-completion-openai" { return "openai" }
        default { return $Provider.Trim().ToLowerInvariant() }
    }
}
function Try-SplitProviderQualifiedKey([string]$Key) {
    if ([string]::IsNullOrWhiteSpace($Key)) { return $null }
    $slashIdx = $Key.IndexOf('/')
    if ($slashIdx -le 0 -or $slashIdx -ge ($Key.Length - 1)) { return $null }
    $provider = Normalize-ProviderType $Key.Substring(0, $slashIdx)
    if ([string]::IsNullOrWhiteSpace($provider)) { return $null }
    return [PSCustomObject]@{
        ProviderType = $provider
        ModelId = $Key.Substring($slashIdx + 1)
    }
}
function Get-ProviderQualifiedModelKey([string]$Key, $Model) {
    $qualified = Try-SplitProviderQualifiedKey $Key
    if ($null -ne $qualified) { return "$($qualified.ProviderType)/$($qualified.ModelId)" }
    $providerType = Normalize-ProviderType ([string](Get-JsonPropertyValue $Model "providerType"))
    if ([string]::IsNullOrWhiteSpace($providerType)) { return $Key }
    $modelId = [string](Get-JsonPropertyValue $Model "id")
    if ([string]::IsNullOrWhiteSpace($modelId)) { $modelId = $Key }
    return "$providerType/$modelId"
}
function Convert-LiteLlmModelToCatalogKey([string]$Provider, [string]$ModelName) {
    $providerType = Normalize-ProviderType $Provider
    if ($providerType -notin @("openai", "claude", "copilot", "ollama")) { return $null }
    $trimmed = $ModelName
    foreach ($candidatePrefix in @($Provider, $providerType, "anthropic", "github_copilot", "copilot", "openai", "ollama")) {
        if ([string]::IsNullOrWhiteSpace($candidatePrefix)) { continue }
        $prefix = "$candidatePrefix/"
        if ($trimmed.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $trimmed = $trimmed.Substring($prefix.Length)
            break
        }
    }
    return "$providerType/$trimmed"
}
function Get-SupportedReasoningEfforts($Data) {
    $efforts = New-Object System.Collections.Generic.List[string]
    if ([bool](Get-JsonPropertyValue $Data "supports_minimal_reasoning_effort")) {
        [void]$efforts.Add("minimal")
        [void]$efforts.Add("low")
    }
    if ([bool](Get-JsonPropertyValue $Data "supports_reasoning")) {
        [void]$efforts.Add("medium")
        [void]$efforts.Add("high")
    }
    if ([bool](Get-JsonPropertyValue $Data "supports_max_reasoning_effort") -or [bool](Get-JsonPropertyValue $Data "supports_xhigh_reasoning_effort")) {
        [void]$efforts.Add("max")
    }
    return @($efforts | Select-Object -Unique)
}
if ($DownloadLatest) {
    Write-Host "`nDownloading latest pricing from LiteLLM..."
    $litellmUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
    try {
        $raw = (Invoke-WebRequest -Uri $litellmUrl -TimeoutSec 30 -UseBasicParsing).Content
        $python = Get-Command python -ErrorAction SilentlyContinue
        if ($null -ne $python) {
            $tmpJson = [System.IO.Path]::GetTempFileName()
            try {
                Set-Content -Path $tmpJson -Value $raw -Encoding UTF8
                $normalizedJson = & $python.Source -c "import json, sys; data=json.load(open(sys.argv[1], encoding='utf-8-sig')); supported={'openai','anthropic','github_copilot','ollama'}; filtered={k:v for k,v in data.items() if isinstance(v, dict) and str(v.get('litellm_provider') or '').lower() in supported}; print(json.dumps(filtered, separators=(',', ':')))" $tmpJson
                $raw = $normalizedJson | ConvertFrom-Json
            }
            finally {
                Remove-Item $tmpJson -ErrorAction SilentlyContinue
            }
        }
        else {
            $raw = $raw | ConvertFrom-Json
        }
        $local = Get-Content $JsonPath -Raw | ConvertFrom-Json
        $models = Ensure-ObjectProperty $local "models"
        $aliases = Ensure-ObjectProperty $local "aliases"
        $migratedModels = [ordered]@{}
        $canonicalKeyMap = @{}
        foreach ($prop in $models.PSObject.Properties) {
            if ($prop.Name.StartsWith("_")) { continue }
            $newKey = Get-ProviderQualifiedModelKey $prop.Name $prop.Value
            $migratedModels[$newKey] = $prop.Value
            $canonicalKeyMap[$prop.Name] = $newKey
        }
        $models = [PSCustomObject]$migratedModels
        Set-ObjectProperty $local "models" $models
        $migratedAliases = [ordered]@{}
        foreach ($aliasProp in $aliases.PSObject.Properties) {
            $canonical = [string]$aliasProp.Value
            if ($canonicalKeyMap.ContainsKey($canonical)) {
                $canonical = $canonicalKeyMap[$canonical]
            }
            $aliasSplit = Try-SplitProviderQualifiedKey $aliasProp.Name
            $aliasKey = if ($null -ne $aliasSplit) { "$($aliasSplit.ProviderType)/$($aliasSplit.ModelId)" } else { $aliasProp.Name }
            $migratedAliases[$aliasKey] = $canonical
        }
        $aliases = [PSCustomObject]$migratedAliases
        Set-ObjectProperty $local "aliases" $aliases
        $addedCount = 0
        $updatedCount = 0
        foreach ($prop in $raw.PSObject.Properties) {
            $modelName = $prop.Name
            $data = $prop.Value
            $provider = [string](Get-JsonPropertyValue $data "litellm_provider")
            $catalogKey = Convert-LiteLlmModelToCatalogKey $provider $modelName
            if ([string]::IsNullOrWhiteSpace($catalogKey)) { continue }
            $qualified = Try-SplitProviderQualifiedKey $catalogKey
            if ($null -eq $qualified) { continue }
            $inputCost = Get-JsonPropertyValue $data "input_cost_per_token"
            $outputCost = Get-JsonPropertyValue $data "output_cost_per_token"
            $inputPer1M = if ($null -ne $inputCost) { [math]::Round([double]$inputCost * 1000000, 4) } else { $null }
            $outputPer1M = if ($null -ne $outputCost) { [math]::Round([double]$outputCost * 1000000, 4) } else { $null }
            $model = Get-JsonPropertyValue $models $catalogKey
            if ($null -eq $model) {
                $model = [PSCustomObject]@{
                    id = $qualified.ModelId
                    providerType = $qualified.ProviderType
                }
                switch ($qualified.ProviderType) {
                    "claude" { Set-ObjectProperty $model "ownedBy" "anthropic" }
                    "ollama" { Set-ObjectProperty $model "ownedBy" "ollama" }
                    "openai" { Set-ObjectProperty $model "ownedBy" "openai" }
                }
                Set-ObjectProperty $models $catalogKey $model
                $addedCount++
            }
            else {
                Set-ObjectProperty $model "id" $qualified.ModelId
                Set-ObjectProperty $model "providerType" $qualified.ProviderType
            }

            $contextWindow = Get-JsonPropertyValue $data "max_input_tokens"
            if ($null -eq $contextWindow) { $contextWindow = Get-JsonPropertyValue $data "max_tokens" }
            if ($null -ne $contextWindow) {
                Set-ObjectProperty $model "contextWindowTokens" ([int]$contextWindow)
                Set-ObjectProperty $model "maxInputTokens" ([int]$contextWindow)
            }
            $maxOutputTokens = Get-JsonPropertyValue $data "max_output_tokens"
            if ($null -eq $maxOutputTokens) { $maxOutputTokens = Get-JsonPropertyValue $data "max_tokens" }
            if ($null -ne $maxOutputTokens) {
                Set-ObjectProperty $model "maxOutputTokens" ([int]$maxOutputTokens)
            }

            if ($null -ne $inputPer1M -or $null -ne $outputPer1M -or $qualified.ProviderType -eq "ollama") {
                $pricing = Ensure-ObjectProperty $model "pricing"
                $oldInput = Get-JsonPropertyValue $pricing "inputPer1MTokens"
                $oldOutput = Get-JsonPropertyValue $pricing "outputPer1MTokens"
                Set-ObjectProperty $pricing "currency" "USD"
                if ($null -ne $inputPer1M) { Set-ObjectProperty $pricing "inputPer1MTokens" $inputPer1M }
                elseif ($qualified.ProviderType -eq "ollama" -and $null -eq $oldInput) { Set-ObjectProperty $pricing "inputPer1MTokens" 0 }
                if ($null -ne $outputPer1M) { Set-ObjectProperty $pricing "outputPer1MTokens" $outputPer1M }
                elseif ($qualified.ProviderType -eq "ollama" -and $null -eq $oldOutput) { Set-ObjectProperty $pricing "outputPer1MTokens" 0 }
                $cachedInputCost = Get-JsonPropertyValue $data "cache_read_input_token_cost"
                if ($null -ne $cachedInputCost) {
                    Set-ObjectProperty $pricing "cachedInputPer1MTokens" ([math]::Round([double]$cachedInputCost * 1000000, 4))
                }
                if ($oldInput -ne $inputPer1M -or $oldOutput -ne $outputPer1M) { $updatedCount++ }
            }

            $capabilities = Ensure-ObjectProperty $model "capabilities"
            $mode = [string](Get-JsonPropertyValue $data "mode")
            if ($mode -eq "embedding") {
                Set-ObjectProperty $capabilities "supportsEmbeddings" $true
                Set-ObjectProperty $capabilities "supportsTemperature" $false
                Set-ObjectProperty $capabilities "supportsStructuredOutput" $false
                Set-ObjectProperty $capabilities "supportsTools" $false
                Set-ObjectProperty $capabilities "supportsJsonMode" $false
            }
            if ($null -ne (Get-JsonPropertyValue $data "supports_function_calling")) {
                Set-ObjectProperty $capabilities "supportsTools" ([bool](Get-JsonPropertyValue $data "supports_function_calling"))
            }
            if ($null -ne (Get-JsonPropertyValue $data "supports_response_schema")) {
                $supportsSchema = [bool](Get-JsonPropertyValue $data "supports_response_schema")
                Set-ObjectProperty $capabilities "supportsStructuredOutput" $supportsSchema
                Set-ObjectProperty $capabilities "supportsJsonMode" $supportsSchema
            }
            if ($null -ne (Get-JsonPropertyValue $data "supports_vision")) {
                Set-ObjectProperty $capabilities "supportsVision" ([bool](Get-JsonPropertyValue $data "supports_vision"))
            }
            if ($null -ne (Get-JsonPropertyValue $data "supports_audio_input") -or $null -ne (Get-JsonPropertyValue $data "supports_audio_output")) {
                Set-ObjectProperty $capabilities "supportsAudio" ([bool](Get-JsonPropertyValue $data "supports_audio_input") -or [bool](Get-JsonPropertyValue $data "supports_audio_output"))
            }
            $supportsReasoning = ([bool](Get-JsonPropertyValue $data "supports_reasoning")) -or ([bool](Get-JsonPropertyValue $data "supports_minimal_reasoning_effort")) -or ([bool](Get-JsonPropertyValue $data "supports_max_reasoning_effort")) -or ([bool](Get-JsonPropertyValue $data "supports_xhigh_reasoning_effort"))
            if ($supportsReasoning) {
                Set-ObjectProperty $capabilities "supportsReasoningEffort" $true
                $efforts = @(Get-SupportedReasoningEfforts $data)
                if ($efforts.Count -gt 0) {
                    Set-ObjectProperty $capabilities "supportedReasoningEfforts" $efforts
                }
            }
            elseif ($qualified.ProviderType -eq "claude") {
                Set-ObjectProperty $capabilities "supportsReasoningEffort" $false
            }
        }
        Set-ObjectProperty $local "_updated" (Get-Date).ToString("yyyy-MM-dd")
        $local | ConvertTo-Json -Depth 32 | Set-Content $JsonPath -Encoding UTF8
        Write-Host "Merged pricing into metadata: $addedCount added, $updatedCount updated"
    }
    catch {
        Write-Warning "Could not download LiteLLM pricing: $_"
        Write-Host "Continuing with existing local metadata..."
    }
}
$json = Get-Content $JsonPath -Raw | ConvertFrom-Json
$models = Get-JsonPropertyValue $json "models"
$aliases = Get-JsonPropertyValue $json "aliases"
function CsString($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return "null" }
    $escaped = ([string]$Value).Replace("\", "\\").Replace('"', '\"')
    return "`"$escaped`""
}
function CsInt($Value) {
    if ($null -eq $Value) { return "null" }
    return ([int]$Value).ToString([System.Globalization.CultureInfo]::InvariantCulture)
}
function CsDecimal($Value) {
    if ($null -eq $Value) { return "null" }
    return ([decimal]$Value).ToString([System.Globalization.CultureInfo]::InvariantCulture) + "m"
}
function CsBool($Value) {
    if ($null -eq $Value) { return "null" }
    if ([bool]$Value) { return "true" }
    return "false"
}
function CsStringList($Value) {
    if ($null -eq $Value) { return "null" }
    $items = @($Value) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { CsString $_ }
    if ($items.Count -eq 0) { return "null" }
    return "[" + ($items -join ", ") + "]"
}
function Split-ProviderQualifiedKey([string]$Key) {
    return Try-SplitProviderQualifiedKey $Key
}
function Append-IfNotNull($Builder, [string]$Name, [string]$ValueLiteral) {
    if ($ValueLiteral -ne "null") { [void]$Builder.AppendLine("                $Name = $ValueLiteral,") }
}
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("// <auto-generated/>")
[void]$sb.AppendLine("// Generated by scripts/update-model-metadata.ps1 on $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
[void]$sb.AppendLine("// Source: src/GnOuGo.AI.Core/Telemetry/model-metadata.json")
[void]$sb.AppendLine("// DO NOT EDIT MANUALLY - run the script to regenerate.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("using System.Collections.Frozen;")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("namespace GnOuGo.AI.Core;")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("public static partial class ModelMetadataCatalog")
[void]$sb.AppendLine("{")
[void]$sb.AppendLine("    private static readonly FrozenDictionary<string, LLMModelMetadata> BuiltinModels = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)")
[void]$sb.AppendLine("    {")
$modelCount = 0
foreach ($prop in $models.PSObject.Properties) {
    $name = $prop.Name
    if ($name.StartsWith("_")) { continue }
    $m = $prop.Value
    $qualifiedKey = Split-ProviderQualifiedKey $name
    $pricing = Get-JsonPropertyValue $m "pricing"
    $cap = Get-JsonPropertyValue $m "capabilities"
    $id = Get-JsonPropertyValue $m "id"
    if ([string]::IsNullOrWhiteSpace([string]$id)) { $id = if ($null -ne $qualifiedKey) { $qualifiedKey.ModelId } else { $name } }
    $displayName = Get-JsonPropertyValue $m "displayName"
    if ([string]::IsNullOrWhiteSpace([string]$displayName)) { $displayName = $id }
    $providerType = Normalize-ProviderType (Get-JsonPropertyValue $m "providerType")
    if ([string]::IsNullOrWhiteSpace([string]$providerType) -and $null -ne $qualifiedKey) { $providerType = $qualifiedKey.ProviderType }
    [void]$sb.AppendLine("        [$((CsString $name))] = new LLMModelMetadata")
    [void]$sb.AppendLine("        {")
    [void]$sb.AppendLine("            Id = $((CsString $id)),")
    [void]$sb.AppendLine("            DisplayName = $((CsString $displayName)),")
    Append-IfNotNull $sb "ProviderType" (CsString $providerType)
    Append-IfNotNull $sb "OwnedBy" (CsString (Get-JsonPropertyValue $m "ownedBy"))
    Append-IfNotNull $sb "ContextWindowTokens" (CsInt (Get-JsonPropertyValue $m "contextWindowTokens"))
    Append-IfNotNull $sb "MaxInputTokens" (CsInt (Get-JsonPropertyValue $m "maxInputTokens"))
    Append-IfNotNull $sb "MaxOutputTokens" (CsInt (Get-JsonPropertyValue $m "maxOutputTokens"))
    if ($null -ne $pricing) {
        $currency = Get-JsonPropertyValue $pricing "currency"
        if ([string]::IsNullOrWhiteSpace([string]$currency)) { $currency = "USD" }
        [void]$sb.AppendLine("            Pricing = new ModelPricingMetadata")
        [void]$sb.AppendLine("            {")
        [void]$sb.AppendLine("                Currency = $((CsString $currency)),")
        Append-IfNotNull $sb "InputPer1MTokens" (CsDecimal (Get-JsonPropertyValue $pricing "inputPer1MTokens"))
        Append-IfNotNull $sb "OutputPer1MTokens" (CsDecimal (Get-JsonPropertyValue $pricing "outputPer1MTokens"))
        Append-IfNotNull $sb "CachedInputPer1MTokens" (CsDecimal (Get-JsonPropertyValue $pricing "cachedInputPer1MTokens"))
        Append-IfNotNull $sb "ReasoningOutputPer1MTokens" (CsDecimal (Get-JsonPropertyValue $pricing "reasoningOutputPer1MTokens"))
        [void]$sb.AppendLine("            },")
    }
    if ($null -ne $cap) {
        [void]$sb.AppendLine("            Capabilities = new ModelCapabilityMetadata")
        [void]$sb.AppendLine("            {")
        Append-IfNotNull $sb "SupportsTemperature" (CsBool (Get-JsonPropertyValue $cap "supportsTemperature"))
        Append-IfNotNull $sb "SupportsReasoningEffort" (CsBool (Get-JsonPropertyValue $cap "supportsReasoningEffort"))
        Append-IfNotNull $sb "SupportsStructuredOutput" (CsBool (Get-JsonPropertyValue $cap "supportsStructuredOutput"))
        Append-IfNotNull $sb "SupportsTools" (CsBool (Get-JsonPropertyValue $cap "supportsTools"))
        Append-IfNotNull $sb "SupportsJsonMode" (CsBool (Get-JsonPropertyValue $cap "supportsJsonMode"))
        Append-IfNotNull $sb "SupportsVision" (CsBool (Get-JsonPropertyValue $cap "supportsVision"))
        Append-IfNotNull $sb "SupportsAudio" (CsBool (Get-JsonPropertyValue $cap "supportsAudio"))
        Append-IfNotNull $sb "SupportsEmbeddings" (CsBool (Get-JsonPropertyValue $cap "supportsEmbeddings"))
        Append-IfNotNull $sb "SupportedReasoningEfforts" (CsStringList (Get-JsonPropertyValue $cap "supportedReasoningEfforts"))
        Append-IfNotNull $sb "UnsupportedRequestParameters" (CsStringList (Get-JsonPropertyValue $cap "unsupportedRequestParameters"))
        [void]$sb.AppendLine("            },")
    }
    [void]$sb.AppendLine("        },")
    $modelCount++
}
[void]$sb.AppendLine("    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("    private static readonly FrozenDictionary<string, string> BuiltinAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)")
[void]$sb.AppendLine("    {")
$aliasCount = 0
if ($aliases) {
    foreach ($prop in $aliases.PSObject.Properties) {
        [void]$sb.AppendLine("        [$((CsString $prop.Name))] = $((CsString $prop.Value)),")
        $aliasCount++
    }
}
[void]$sb.AppendLine("    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);")
[void]$sb.AppendLine("}")
Set-Content $OutputPath -Value $sb.ToString() -Encoding UTF8
Write-Host "`nGenerated $OutputPath ($modelCount models, $aliasCount aliases)"
