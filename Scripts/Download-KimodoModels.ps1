# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Downloads the motion model and adapters with an existing user-managed HF CLI login.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$WorkspaceDirectory,
    [Parameter(Mandatory=$true)][string]$MotionDirectory,
    [Parameter(Mandatory=$true)][string]$EncoderDirectory,
    [Parameter(Mandatory=$true)][string]$AdapterDirectory,
    [string]$PythonExecutable = '',
    [string]$HfExecutable = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:HF_HUB_OFFLINE = '0'
$env:TRANSFORMERS_OFFLINE = '0'
$loginRequired = -join @([char]0x8bf7,[char]0x5148,[char]0x767b,[char]0x5f55) + 'hugging face cli'
if (-not $HfExecutable) {
    $hfCommand = Get-Command hf.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hfCommand) { $HfExecutable = $hfCommand.Source }
    elseif ($PythonExecutable) { $HfExecutable = Join-Path (Split-Path -Parent $PythonExecutable) 'hf.exe' }
}
if (-not $HfExecutable -or -not (Test-Path -LiteralPath $HfExecutable -PathType Leaf)) {
    [Console]::Error.WriteLine($loginRequired)
    exit 2
}
$ErrorActionPreference = 'Continue'
$identityOutput = & $HfExecutable auth whoami 2>&1
$identityExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$identityText = ($identityOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
if ($identityExit -ne 0 -or [string]::IsNullOrWhiteSpace($identityText) -or
    $identityText -match '(?i)not logged in|not authenticated') {
    if ($identityText -match '(?i)connectionerror|connecterror|timeout|timed out|name resolution|certificate_verify_failed|proxyerror') {
        [Console]::Error.WriteLine('Hugging Face login check could not reach the server.')
        exit 3
    }
    [Console]::Error.WriteLine($loginRequired)
    exit 2
}
try {
    $pluginPath = [IO.Directory]::GetParent($PSScriptRoot).FullName.TrimEnd('\','/')
    $destinations = @($MotionDirectory,$EncoderDirectory,$AdapterDirectory)
    foreach ($path in @($WorkspaceDirectory) + $destinations) {
        if (-not [IO.Path]::IsPathRooted($path)) { throw 'Select absolute repository and workspace directories.' }
        $fullPath = [IO.Path]::GetFullPath($path).TrimEnd('\','/')
        if ($fullPath -eq $pluginPath -or $fullPath.StartsWith($pluginPath + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Model repositories cannot be downloaded into the plugin.'
        }
        if ($fullPath -eq [IO.Path]::GetPathRoot($path).TrimEnd('\','/')) { throw 'Select a dedicated directory.' }
    }
    for ($i = 0; $i -lt $destinations.Length; $i++) {
        $destinations[$i] = [IO.Path]::GetFullPath($destinations[$i]).TrimEnd('\','/')
        for ($j = 0; $j -lt $i; $j++) {
            if ($destinations[$i] -eq $destinations[$j] -or
                $destinations[$i].StartsWith($destinations[$j] + '\',[StringComparison]::OrdinalIgnoreCase) -or
                $destinations[$j].StartsWith($destinations[$i] + '\',[StringComparison]::OrdinalIgnoreCase)) {
                throw 'Select separate repository directories.'
            }
        }
    }
    $env:HF_HUB_CACHE = Join-Path $WorkspaceDirectory 'Cache\HuggingFace\hub'
    $env:HF_XET_CACHE = Join-Path $WorkspaceDirectory 'Cache\HuggingFace\xet'
    $env:TEMP = Join-Path $WorkspaceDirectory 'Temp'
    $env:TMP = $env:TEMP
    foreach ($directory in @($env:HF_HUB_CACHE,$env:HF_XET_CACHE,$env:TEMP)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $repositories = @('nvidia/Kimodo-SOMA-RP-v1.2',
        'McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp',
        'McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised')
    for ($i = 0; $i -lt $repositories.Length; $i++) {
        & $HfExecutable download $repositories[$i] --local-dir $destinations[$i]
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }
} catch {
    [Console]::Error.WriteLine($_.ToString())
    exit 1
}
exit 0
