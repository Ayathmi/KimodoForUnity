# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Hosts the administrator installation and returns logs and cancellation through the workspace.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExecutable,
    [Parameter(Mandatory=$true)][string]$WorkspaceDirectory,
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][string]$NinjaExecutable,
    [Parameter(Mandatory=$true)][string]$GccExecutable,
    [Parameter(Mandatory=$true)][string]$GxxExecutable,
    [Parameter(Mandatory=$true)][string]$LogDirectory,
    [Parameter(Mandatory=$true)][int]$ParentProcessId,
    [Parameter(Mandatory=$true)][long]$ParentStartTicks
)
$ErrorActionPreference = 'Stop'
$exitCode = 1
function Quote-Literal([string]$Value) { return "'" + $Value.Replace("'","''") + "'" }
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator authorization is required for one-click installation.'
    }
    $env:TEMP = $LogDirectory
    $env:TMP = $LogDirectory
    Add-Type -Path (Join-Path $PSScriptRoot '..\Editor\KimodoInstallHost.cs')
    $gate = Quote-Literal (Join-Path $LogDirectory 'ready')
    $command = '[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $ErrorActionPreference = ''Stop''; '
    $command += '$owner = [Diagnostics.Process]::GetProcessById(' + $PID + '); '
    $command += 'while (-not [IO.File]::Exists(' + $gate + ')) { if ($owner.HasExited) { exit 1223 }; Start-Sleep -Milliseconds 100 }; $owner.Dispose(); '
    $command += 'try { & ' + (Quote-Literal (Join-Path $PSScriptRoot 'Install-Kimodo.ps1'))
    foreach ($name in @('PythonExecutable','WorkspaceDirectory','InstallDirectory','NinjaExecutable','GccExecutable','GxxExecutable')) {
        $command += ' -' + $name + ' ' + (Quote-Literal (Get-Variable -Name $name -ValueOnly))
    }
    $command += '; exit 0 } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $shell = Join-Path $PSHOME 'powershell.exe'
    $exitCode = [KimodoForUnity.KimodoInstallHost]::Run($shell, ('-NoProfile -NonInteractive -EncodedCommand ' + $encoded),
        $LogDirectory, $ParentProcessId, $ParentStartTicks)
} catch {
    [IO.File]::AppendAllText((Join-Path $LogDirectory 'host.log'), ($_.ToString() + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
} finally {
    [IO.File]::WriteAllText((Join-Path $LogDirectory 'exit-code.txt'), [string]$exitCode, [Text.UTF8Encoding]::new($false))
}
exit $exitCode
