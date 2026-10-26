# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Pulls the FP16 Llama 3 model through a user-installed Ollama client, then prepares local Kimodo weights.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OllamaExecutable,
    [Parameter(Mandatory=$true)][string]$PythonExecutable,
    [Parameter(Mandatory=$true)][string]$WorkspaceDirectory,
    [Parameter(Mandatory=$true)][string]$OllamaModelsDirectory,
    [Parameter(Mandatory=$true)][string]$EncoderDirectory,
    [Parameter(Mandatory=$true)][string]$BaseDirectory,
    [switch]$DownloadOnly
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$server = $null
$oldEnvironment = @{}
try {
    foreach ($executable in @($OllamaExecutable,$PythonExecutable)) {
        if (-not [IO.Path]::IsPathRooted($executable) -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Executable not found: $executable"
        }
    }
    $pluginPath = [IO.Directory]::GetParent($PSScriptRoot).FullName.TrimEnd('\','/')
    foreach ($path in @($WorkspaceDirectory,$OllamaModelsDirectory,$BaseDirectory)) {
        if (-not [IO.Path]::IsPathRooted($path)) { throw 'Select absolute model and workspace directories.' }
        $full = [IO.Path]::GetFullPath($path).TrimEnd('\','/')
        if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\','/') -or $full -eq $pluginPath -or
            $full.StartsWith($pluginPath + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Select a dedicated model directory outside the plugin.'
        }
        for ($ancestor = [IO.DirectoryInfo]::new($full); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
            if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Use model paths without junctions or symbolic links.'
            }
        }
    }
    $modelRoot = [IO.Path]::GetFullPath($OllamaModelsDirectory).TrimEnd('\','/')
    $baseRoot = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\','/')
    if ($modelRoot -eq $baseRoot -or $modelRoot.StartsWith($baseRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or
        $baseRoot.StartsWith($modelRoot + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Select separate Ollama storage and converted-model directories.'
    }
    $logRoot = Join-Path $WorkspaceDirectory ('Logs\Ollama-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($logRoot) | Out-Null
    [IO.Directory]::CreateDirectory($modelRoot) | Out-Null
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $endpoint = "http://127.0.0.1:$port"
    $environment = @{
        OLLAMA_HOST = "127.0.0.1:$port"; OLLAMA_MODELS = $modelRoot; OLLAMA_NO_CLOUD = '1'
        PYTHONUTF8 = '1'; PYTHONIOENCODING = 'utf-8'; PYTHONUNBUFFERED = '1'; PYTHONNOUSERSITE = '1'
        HF_HUB_OFFLINE = '1'; TRANSFORMERS_OFFLINE = '1'
        TEMP = (Join-Path $WorkspaceDirectory 'Temp'); TMP = (Join-Path $WorkspaceDirectory 'Temp')
    }
    if (-not $env:HTTPS_PROXY) {
        $registryUri = [uri]'https://registry.ollama.ai'
        $systemProxy = [Net.WebRequest]::DefaultWebProxy.GetProxy($registryUri)
        if ($systemProxy -and $systemProxy.Authority -ne $registryUri.Authority) {
            $environment['HTTPS_PROXY'] = $systemProxy.AbsoluteUri
        }
    }
    foreach ($name in $environment.Keys) {
        $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$environment[$name],'Process')
    }
    [IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
    if (-not $DownloadOnly) {
        & $PythonExecutable -c 'import gguf, torch, transformers, safetensors'
        if ($LASTEXITCODE -ne 0) { throw 'Conversion dependencies are missing. Run the environment installation first.' }
    }
    $server = Start-Process -FilePath $OllamaExecutable -ArgumentList 'serve' -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logRoot 'server.stdout.log') -RedirectStandardError (Join-Path $logRoot 'server.stderr.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($server.HasExited) { throw "Ollama server exited. See $logRoot" }
        try { $null = Invoke-RestMethod "$endpoint/api/version" -TimeoutSec 1; $ready = $true; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw "Ollama server startup timed out. See $logRoot" }
    $pullCode = @'
import json, sys, urllib.request
endpoint = sys.argv[1]
request = urllib.request.Request(endpoint + '/api/pull', data=json.dumps({'model': 'llama3:8b-instruct-fp16', 'stream': True}).encode(), headers={'Content-Type': 'application/json'})
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
previous = None
success = False
with opener.open(request, timeout=600) as response:
    for line in response:
        progress = json.loads(line)
        if progress.get('error'):
            raise RuntimeError(progress['error'])
        status = progress.get('status', '')
        total = progress.get('total', 0)
        percent = int(100 * progress.get('completed', 0) / total) if total else None
        key = (status, percent // 2 if percent is not None else None)
        if key != previous:
            print(status + (' ' + str(percent) + '%' if percent is not None else ''), flush=True)
            previous = key
        success = status == 'success'
if not success:
    raise RuntimeError('Ollama download ended before success.')
'@
    Write-Output 'Pulling llama3:8b-instruct-fp16'
    & $PythonExecutable -u -c $pullCode $endpoint
    if ($LASTEXITCODE -ne 0) { throw 'Ollama model download failed.' }
    if (-not $DownloadOnly) {
        & (Join-Path $PSScriptRoot 'Convert-KimodoLlama.ps1') -PythonExecutable $PythonExecutable `
            -OllamaModelsDirectory $modelRoot -EncoderDirectory $EncoderDirectory -BaseDirectory $BaseDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Ollama model conversion failed.' }
    }
    Write-Output 'Llama 3 preparation completed.'
} catch {
    [Console]::Error.WriteLine($_.ToString())
    exit 1
} finally {
    if ($server) {
        if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
        $server.Dispose()
    }
    foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$oldEnvironment[$name],'Process') }
}
exit 0
