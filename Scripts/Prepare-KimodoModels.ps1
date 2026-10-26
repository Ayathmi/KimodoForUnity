# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Creates a runtime adapter view referencing a local base model without editing downloaded repositories.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$MotionDirectory,
    [Parameter(Mandatory=$true)][string]$EncoderDirectory,
    [Parameter(Mandatory=$true)][string]$AdapterDirectory,
    [Parameter(Mandatory=$true)][string]$BaseDirectory
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try {
    $runtimePath = [IO.Path]::GetFullPath($RuntimeDirectory)
    if (Test-Path -LiteralPath $runtimePath) { throw 'Select a new runtime directory.' }
    foreach ($source in @($MotionDirectory,$EncoderDirectory,$AdapterDirectory,$BaseDirectory)) {
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing repository directory: $source" }
        $sourcePath = [IO.Path]::GetFullPath($source).TrimEnd('\','/')
        if ($runtimePath.StartsWith($sourcePath + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Runtime views must be outside the downloaded repositories.'
        }
    }
    $adapterFile = Join-Path $EncoderDirectory 'adapter_config.json'
    $adapterConfig = Get-Content -Raw -LiteralPath $adapterFile | ConvertFrom-Json
    if ($adapterConfig.base_model_name_or_path -ne 'meta-llama/Meta-Llama-3-8B-Instruct') {
        throw 'The MNTP adapter references an unsupported base model.'
    }
    $adapterConfig.base_model_name_or_path = [IO.Path]::GetFullPath($BaseDirectory)
    $textRoot = Join-Path $runtimePath 'TextEncoders'
    $encoderView = Join-Path $textRoot 'McGill-NLP\LLM2Vec-Meta-Llama-3-8B-Instruct-mntp'
    [IO.Directory]::CreateDirectory($encoderView) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $textRoot 'meta-llama')) | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $runtimePath 'Kimodo-SOMA-RP-v1.2') -Value ([IO.Path]::GetFullPath($MotionDirectory)) | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $textRoot 'McGill-NLP\LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised') -Value ([IO.Path]::GetFullPath($AdapterDirectory)) | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $textRoot 'meta-llama\Meta-Llama-3-8B-Instruct') -Value ([IO.Path]::GetFullPath($BaseDirectory)) | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $EncoderDirectory -Force) {
        if ($entry.Name -in @('.cache','adapter_config.json')) { continue }
        $destination = Join-Path $encoderView $entry.Name
        if ($entry.PSIsContainer) {
            New-Item -ItemType Junction -Path $destination -Value $entry.FullName | Out-Null
        } else {
            try { New-Item -ItemType HardLink -Path $destination -Value $entry.FullName | Out-Null }
            catch { Copy-Item -LiteralPath $entry.FullName -Destination $destination }
        }
    }
    [IO.File]::WriteAllText((Join-Path $encoderView 'adapter_config.json'),
        ($adapterConfig | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
} catch {
    [Console]::Error.WriteLine($_.ToString())
    exit 1
}
exit 0
