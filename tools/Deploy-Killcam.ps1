#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$SptPath,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = (Resolve-Path -LiteralPath $AssemblyPath).ProviderPath
$root = (Resolve-Path -LiteralPath $SptPath).ProviderPath.TrimEnd('\','/')
$plugins = Join-Path $root 'BepInEx/plugins'
if (-not (Test-Path -LiteralPath $plugins -PathType Container)) { throw 'Target has no BepInEx/plugins folder.' }
if ([Reflection.AssemblyName]::GetAssemblyName($source).Name -ne 'maschine-Killcam') { throw 'Not the renamed Killcam assembly.' }
$pluginRoot = [IO.Path]::GetFullPath($plugins).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if ($source.StartsWith($pluginRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build source must be outside plugin discovery.' }
$destination = Join-Path $plugins 'maschine-Killcam.dll'
$owned = @('maschine-Killcam.dll','maschine-Killcam.pdb','maschine-KillcamReplay.dll','maschine-KillcamReplay.pdb')
$existing = @(Get-ChildItem -LiteralPath $plugins -File -Recurse | Where-Object Name -In $owned)
# Validate every final path before moving anything. Backups are outside discovery.
foreach ($file in $existing) {
    $resolved = (Resolve-Path -LiteralPath $file.FullName).ProviderPath
    if (-not $resolved.StartsWith($pluginRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Owned file is outside plugin root: $resolved" }
    # Refuse a running game's locked files before any mutation. Never stop a game here.
    $handle = [IO.File]::Open($resolved, 'Open', 'Read', 'None')
    $handle.Dispose()
}
$backup = Join-Path $root ('BepInEx/killcam-backups/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$backupRoot = [IO.Path]::GetFullPath($backup).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
foreach ($file in $existing) {
    $relative = [IO.Path]::GetRelativePath($plugins, $file.FullName)
    $archived = [IO.Path]::GetFullPath((Join-Path $backup $relative))
    if (-not $archived.StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe backup path.' }
    [IO.Directory]::CreateDirectory((Split-Path $archived)) | Out-Null
    Move-Item -LiteralPath $file.FullName -Destination $archived
}
Copy-Item -LiteralPath $source -Destination $destination
if ($Configuration -eq 'Debug') {
    $pdb = [IO.Path]::ChangeExtension($source, '.pdb')
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $plugins 'maschine-Killcam.pdb') }
}
if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw 'Killcam deployment hash mismatch.' }
if (@(Get-ChildItem -LiteralPath $plugins -File -Recurse | Where-Object Name -In @('maschine-Killcam.dll','maschine-KillcamReplay.dll')).Count -ne 1) {
    throw 'Unexpected duplicate Killcam installation remains.'
}
Write-Output "KILLCAM_DEPLOYED $destination"
if ($existing.Count) { Write-Output "KILLCAM_BACKUP $backup" }
