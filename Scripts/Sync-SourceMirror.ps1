# SPDX-License-Identifier: Apache-2.0
<#
.SYNOPSIS
Copies approved source files to a separate mirror, excluding Unity metadata.
.DESCRIPTION
Preview is the default. Apply is explicit. No recursive directory deletion is used.
The manifest must remain outside both plugin roots.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$MirrorRoot,
    [Parameter(Mandatory=$true)][string]$ManifestPath,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\','/')
$mirrorPath = [IO.Path]::GetFullPath($MirrorRoot).TrimEnd('\','/')
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
function Test-Within([string]$Candidate, [string]$Parent) {
    return $Candidate.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or $Candidate.StartsWith($Parent + '\',[StringComparison]::OrdinalIgnoreCase)
}
if ((Test-Within $sourcePath $mirrorPath) -or (Test-Within $mirrorPath $sourcePath)) { throw 'Source and mirror must be separate.' }
if ((Test-Within $manifestFile $sourcePath) -or (Test-Within $manifestFile $mirrorPath)) { throw 'Keep the manifest outside both plugin roots.' }
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'Editor\KimodoForUnity.Editor.asmdef'))) { throw 'Source plugin assembly was not found.' }
foreach ($rootPath in @($sourcePath,$mirrorPath)) {
    $ancestor = [IO.DirectoryInfo]::new($rootPath)
    while ($null -ne $ancestor) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Junctions and symbolic links are not supported.' }
        $ancestor=$ancestor.Parent
    }
}
function Resolve-MirrorFile([string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Split([char[]]@('\','/')) -contains '..') { throw 'Invalid manifest path.' }
    $target = [IO.Path]::GetFullPath((Join-Path $mirrorPath $Relative))
    if (-not (Test-Within $target $mirrorPath) -or $Relative -match '(^|[\\/])\.git([\\/]|$)') { throw 'Path leaves the managed mirror.' }
    $ancestor=[IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($target))
    while ($null -ne $ancestor -and (Test-Within $ancestor.FullName $mirrorPath)) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Mirror contains a reparse point.' }
        $ancestor=$ancestor.Parent
    }
    if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Target is a reparse point.' }
    return $target
}
$previous=@{}
if (Test-Path -LiteralPath $manifestFile) {
    $old = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($old.schemaVersion -ne 1 -or $old.source -ne $sourcePath -or $old.mirror -ne $mirrorPath) { throw 'Manifest belongs to another source or mirror.' }
    foreach ($entry in $old.files) { $previous[$entry.path]=$entry.sha256 }
}
$files=@()
$operations=@()
foreach ($file in Get-ChildItem -LiteralPath $sourcePath -Recurse -File) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source contains a reparse point.' }
    if ($file.Extension -eq '.meta') { continue }
    $relative=$file.FullName.Substring($sourcePath.Length+1)
    $allowed=$file.Extension -in @('.cs','.asmdef','.ps1','.cmd','.sh') -or $relative -in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','.gitignore')
    if (-not $allowed) { throw "Unexpected source payload: $relative" }
    $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $target=Resolve-MirrorFile $relative
    $exists=Test-Path -LiteralPath $target
    $current=if ($exists) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    if ($exists -and $current -ne $hash -and (-not $previous.ContainsKey($relative) -or $previous[$relative] -ne $current)) { throw "External edit conflict: $relative" }
    $files+= [ordered]@{path=$relative;sha256=$hash}
    if ($current -ne $hash) { $operations+= [pscustomobject]@{action='copy';path=$relative;source=$file.FullName;target=$target} }
}
$newPaths=@($files | ForEach-Object { $_.path })
foreach ($relative in $previous.Keys) {
    if ($relative -in $newPaths) { continue }
    $target=Resolve-MirrorFile $relative
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $previous[$relative]) { throw "Removed-source conflict: $relative" }
        $operations+= [pscustomobject]@{action='remove';path=$relative;source='';target=$target}
    }
}
$operations | Select-Object action,path | Format-Table
if (-not $Apply) { Write-Output 'Preview only. Pass -Apply to synchronize managed files.'; return }
foreach ($operation in $operations) {
    if ($operation.action -eq 'copy') {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($operation.target)) | Out-Null
        Copy-Item -LiteralPath $operation.source -Destination $operation.target
    } else { Remove-Item -LiteralPath $operation.target }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestFile)) | Out-Null
$manifest=[ordered]@{schemaVersion=1;source=$sourcePath;mirror=$mirrorPath;files=$files}
$json=$manifest | ConvertTo-Json -Depth 5
$tempManifest=$manifestFile+'.tmp'
[IO.File]::WriteAllText($tempManifest,$json,[Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $manifestFile) { [IO.File]::Replace($tempManifest,$manifestFile,[NullString]::Value) } else { [IO.File]::Move($tempManifest,$manifestFile) }
Write-Output "Synchronized $($files.Count) files; Unity .meta files excluded."
