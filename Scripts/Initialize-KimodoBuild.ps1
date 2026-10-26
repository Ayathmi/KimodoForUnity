# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Uses user-selected Ninja and x64 MinGW-w64 GCC tools in the current process.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$NinjaExecutable,
    [Parameter(Mandatory=$true)][string]$GccExecutable,
    [Parameter(Mandatory=$true)][string]$GxxExecutable
)
$ErrorActionPreference = 'Stop'
foreach ($entry in @(
    @{ Path = $NinjaExecutable; Name = 'ninja.exe' },
    @{ Path = $GccExecutable; Name = 'gcc.exe' },
    @{ Path = $GxxExecutable; Name = 'g++.exe' }
)) {
    if (-not [IO.Path]::IsPathRooted($entry.Path) -or
        -not (Test-Path -LiteralPath $entry.Path -PathType Leaf) -or
        [IO.Path]::GetFileName($entry.Path) -ine $entry.Name) {
        throw ("Select the full path to " + $entry.Name + ".")
    }
}
$ninjaPath = [IO.Path]::GetFullPath($NinjaExecutable)
$gccPath = [IO.Path]::GetFullPath($GccExecutable)
$gxxPath = [IO.Path]::GetFullPath($GxxExecutable)
if ([IO.Path]::GetDirectoryName($gccPath) -ine [IO.Path]::GetDirectoryName($gxxPath)) {
    throw 'Select gcc.exe and g++.exe from the same MinGW-w64 installation.'
}
$toolDirectories = @($ninjaPath, $gccPath, $gxxPath) |
    ForEach-Object { [IO.Path]::GetDirectoryName($_) } | Select-Object -Unique
$env:PATH = ($toolDirectories -join ';') + ';' + $env:PATH
$ninjaVersion = & $ninjaPath --version
if ($LASTEXITCODE -ne 0) { throw 'The selected Ninja could not run.' }
foreach ($compiler in @($gccPath, $gxxPath)) {
    $target = & $compiler -dumpmachine
    if ($LASTEXITCODE -ne 0 -or ($target -join '').Trim() -ne 'x86_64-w64-mingw32') {
        throw 'Select an x64 MinGW-w64 GCC/G++ toolchain for Windows Python.'
    }
}
$env:CMAKE_GENERATOR = 'Ninja'
$env:CC = '"' + $gccPath.Replace('\','/') + '"'
$env:CXX = '"' + $gxxPath.Replace('\','/') + '"'
Remove-Item Env:CMAKE_GENERATOR_PLATFORM,Env:CMAKE_GENERATOR_TOOLSET,Env:CMAKE_GENERATOR_INSTANCE,Env:CMAKE_TOOLCHAIN_FILE -ErrorAction SilentlyContinue
Write-Output ("Ninja: " + $ninjaPath + " (" + $ninjaVersion + ")")
Write-Output ("GCC: " + $gccPath)
Write-Output ("G++: " + $gxxPath)

<#
.SYNOPSIS
Places MinGW runtime libraries next to the installed extension for DLL resolution.
#>
function Copy-KimodoBuildRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$PythonExecutable,
        [Parameter(Mandatory=$true)][string]$GxxExecutable
    )
    $sitePackages = & $PythonExecutable -c "import sysconfig; print(sysconfig.get_path('platlib'))"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the installed Python package directory.' }
    $extensionDirectory = Join-Path $sitePackages 'motion_correction'
    if (-not (Test-Path -LiteralPath $extensionDirectory -PathType Container)) {
        throw 'The motion_correction package was not installed.'
    }
    $compilerDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($GxxExecutable))
    foreach ($library in @('libstdc++-6.dll','libgcc_s_seh-1.dll','libwinpthread-1.dll','libmcfgthread-1.dll')) {
        $sourceLibrary = Join-Path $compilerDirectory $library
        if (Test-Path -LiteralPath $sourceLibrary -PathType Leaf) {
            Copy-Item -LiteralPath $sourceLibrary -Destination (Join-Path $extensionDirectory $library) -Force
        }
    }
}
