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
if ($DownloadLatest) {
    Write-Host "`nDownloading latest pricing from LiteLLM..."
    $litellmUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
    try {
        $raw = Invoke-RestMethod -Uri $litellmUrl -TimeoutSec 30
        $local = Get-Content $JsonPath -Raw | ConvertFrom-Json
        $models = Ensure-ObjectProperty $local "models"
        $validProviders = @("openai", "anthropic", "mistralai", "deepseek", "cohere", "text-completion-openai")
        $addedCount = 0
        $updatedCount = 0
        foreach ($prop in $raw.PSObject.Properties) {
            $modelName = $prop.Name
            $data = $prop.Value
            $inputCost = Get-JsonPropertyValue $data "input_cost_per_token"
            $outputCost = Get-JsonPropertyValue $data "output_cost_per_token"
            if (-not $inputCost -and -not $outputCost) { continue }
            $provider = [string](Get-JsonPropertyValue $data "litellm_provider")
            $isValid = $false
            foreach ($vp in $validProviders) {
                if ($provider -like "*$vp*") { $isValid = $true; break }
            }
            if (-not $isValid) { continue }
            if (-not $inputCost) { $inputCost = 0 }
            if (-not $outputCost) { $outputCost = 0 }
            $inputPer1M = [math]::Round([double]$inputCost * 1000000, 4)
            $outputPer1M = [math]::Round([double]$outputCost * 1000000, 4)
            $cleanName = $modelName -replace "^[^/]+/", ""
            $model = Get-JsonPropertyValue $models $cleanName
            if ($null -eq $model) {
                $model = [PSCustomObject]@{
                    providerType = $provider
                    ownedBy = $provider
                    pricing = [PSCustomObject]@{
                        currency = "USD"
                        inputPer1MTokens = $inputPer1M
                        outputPer1MTokens = $outputPer1M
                    }
                }
                Set-ObjectProperty $models $cleanName $model
                $addedCount++
            }
            else {
                $pricing = Ensure-ObjectProperty $model "pricing"
                $oldInput = Get-JsonPropertyValue $pricing "inputPer1MTokens"
                $oldOutput = Get-JsonPropertyValue $pricing "outputPer1MTokens"
                Set-ObjectProperty $pricing "currency" "USD"
                Set-ObjectProperty $pricing "inputPer1MTokens" $inputPer1M
                Set-ObjectProperty $pricing "outputPer1MTokens" $outputPer1M
                if ($oldInput -ne $inputPer1M -or $oldOutput -ne $outputPer1M) { $updatedCount++ }
            }
            $contextWindow = Get-JsonPropertyValue $data "max_input_tokens"
            if ($null -eq $contextWindow) { $contextWindow = Get-JsonPropertyValue $data "max_tokens" }
            if ($null -ne $contextWindow -and $null -eq (Get-JsonPropertyValue $model "contextWindowTokens")) {
                Set-ObjectProperty $model "contextWindowTokens" ([int]$contextWindow)
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
function Append-IfNotNull($Builder, [string]$Name, [string]$ValueLiteral) {
    if ($ValueLiteral -ne "null") { [void]$Builder.AppendLine("                $Name = $ValueLiteral,") }
}
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("// <auto-generated/>")
[void]$sb.AppendLine("// Generated by scripts/update-model-pricing.ps1 on $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
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
    $pricing = Get-JsonPropertyValue $m "pricing"
    $cap = Get-JsonPropertyValue $m "capabilities"
    $id = Get-JsonPropertyValue $m "id"
    if ([string]::IsNullOrWhiteSpace([string]$id)) { $id = $name }
    $displayName = Get-JsonPropertyValue $m "displayName"
    if ([string]::IsNullOrWhiteSpace([string]$displayName)) { $displayName = $name }
    [void]$sb.AppendLine("        [$((CsString $name))] = new LLMModelMetadata")
    [void]$sb.AppendLine("        {")
    [void]$sb.AppendLine("            Id = $((CsString $id)),")
    [void]$sb.AppendLine("            DisplayName = $((CsString $displayName)),")
    Append-IfNotNull $sb "ProviderType" (CsString (Get-JsonPropertyValue $m "providerType"))
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
