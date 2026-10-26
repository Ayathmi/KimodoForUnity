# SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExecutable,
    [Parameter(Mandatory=$true)][string]$WorkspaceDirectory,
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][string]$NinjaExecutable,
    [Parameter(Mandatory=$true)][string]$GccExecutable,
    [Parameter(Mandatory=$true)][string]$GxxExecutable
)
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath($WorkspaceDirectory)
$environmentPath = [IO.Path]::GetFullPath($InstallDirectory)
$basePython = [IO.Path]::GetFullPath($PythonExecutable)
if (-not (Test-Path -LiteralPath $basePython -PathType Leaf)) { throw 'Python executable was not found.' }
if ($environmentPath -eq [IO.Path]::GetPathRoot($environmentPath)) { throw 'Select a dedicated installation directory.' }
$pluginPath = [IO.Directory]::GetParent($PSScriptRoot).FullName
foreach ($candidate in @($workspacePath,$environmentPath)) {
    if ($candidate -eq $pluginPath -or $candidate.StartsWith($pluginPath+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation and workspace directories cannot be inside the plugin.'
    }
}
if ((Test-Path -LiteralPath $environmentPath) -and
    -not (Test-Path -LiteralPath (Join-Path $environmentPath 'pyvenv.cfg')) -and
    @(Get-ChildItem -LiteralPath $environmentPath -Force).Count -gt 0) {
    throw 'Installation directory is not an empty directory or a Python virtual environment.'
}
function Invoke-Python([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Python command failed with exit code $LASTEXITCODE." }
}
$env:PATH = (Join-Path $environmentPath 'Scripts') + ';' + $env:PATH
. (Join-Path $PSScriptRoot 'Initialize-KimodoBuild.ps1') -NinjaExecutable $NinjaExecutable -GccExecutable $GccExecutable -GxxExecutable $GxxExecutable
$env:PYTHONNOUSERSITE = '1'
$env:PYTHONUTF8 = '1'
$env:PIP_NO_INPUT = '1'
$env:PIP_CACHE_DIR = Join-Path $workspacePath 'Cache\Pip'
$env:TEMP = Join-Path $workspacePath 'Temp'
$env:TMP = $env:TEMP
$env:HF_HOME = Join-Path $workspacePath 'Cache\HuggingFace'
$env:HF_HUB_OFFLINE = '1'
$env:TRANSFORMERS_OFFLINE = '1'
Remove-Item Env:PYTHONPATH,Env:HF_TOKEN,Env:HUGGING_FACE_HUB_TOKEN,Env:SKIP_MOTION_CORRECTION_IN_SETUP -ErrorAction SilentlyContinue
foreach ($directory in @($env:PIP_CACHE_DIR,$env:TEMP,$env:HF_HOME)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
if (-not (Test-Path -LiteralPath (Join-Path $environmentPath 'pyvenv.cfg'))) {
    Invoke-Python $basePython @('-m','venv',$environmentPath)
}
$python = Join-Path $environmentPath 'Scripts\python.exe'
Invoke-Python $python @('-m','pip','install','--upgrade','pip','setuptools','wheel','pybind11>=3,<4','cmake>=3.24,<4')
Invoke-Python $python @('-m','pip','install','torch==2.10.0+cu128','--index-url','https://download.pytorch.org/whl/cu128')
$pybindPath = & $python -m pybind11 --cmakedir
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the pybind11 CMake configuration.' }
$env:CMAKE_PREFIX_PATH = if ($env:CMAKE_PREFIX_PATH) { $pybindPath + ';' + $env:CMAKE_PREFIX_PATH } else { $pybindPath }
Invoke-Python $python @('-m','pip','install','--no-build-isolation','kimodo @ https://github.com/nv-tlabs/kimodo/archive/e1c219c99b4b94ba48be1fb6a1cbc64ecee4c954.zip')
Invoke-Python $python @('-m','pip','install','gguf==0.19.0')
Copy-KimodoBuildRuntime -PythonExecutable $python -GxxExecutable $GxxExecutable
Invoke-Python $python @('-m','pip','check')
Invoke-Python $python @('-c','import kimodo,torch,motion_correction._motion_correction; assert torch.version.cuda')
Write-Output 'Kimodo installation completed.'
