#Requires -Version 7.0
[CmdletBinding()]
param([string]$SptPath = (Join-Path $PSScriptRoot '../../../..'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$gameRoot = (Resolve-Path -LiteralPath $SptPath).Path
$run = Join-Path $repo ('artifacts/rename-checks/' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run) | Out-Null
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
function InstalledSnapshot {
    $state = [ordered]@{}
    foreach ($folder in @('BepInEx/plugins','BepInEx/config')) {
        Get-ChildItem -LiteralPath (Join-Path $gameRoot $folder) -Recurse -File |
            Where-Object Name -Match '^(maschine-Killcam(Replay)?\.(dll|pdb)|com\.maschine\.Killcam(Replay)?\.cfg)$' |
            Sort-Object FullName | ForEach-Object { $state[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    }
    $state | ConvertTo-Json -Compress
}
$before = InstalledSnapshot
try {
    Run-Dotnet @('build', "$repo/Killcam.csproj", '-c', 'Release', '--nologo',
        "-p:SPTPath=$gameRoot", "-p:CustomAfterMicrosoftCommonTargets=$repo/tools/NoDeploy.targets")
    $dll = "$repo/bin/Release/maschine-Killcam.dll"
    Add-Type -Path "$gameRoot/BepInEx/core/Mono.Cecil.dll"
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll)
    try {
        if ($assembly.Name.Name -ne 'maschine-Killcam') { throw 'Wrong assembly identity.' }
        $plugin = $assembly.MainModule.GetType('Killcam.Plugin')
        $metadata = @($plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
        if ($metadata.Count -ne 1 -or $metadata[0].ConstructorArguments[0].Value -ne 'com.maschine.Killcam' `
            -or $metadata[0].ConstructorArguments[1].Value -ne 'maschine-Killcam') { throw 'Wrong plugin identity.' }
        $version = $metadata[0].ConstructorArguments[2].Value
        if ($assembly.Name.Version -ne [version]($version + '.0')) { throw 'Plugin and assembly versions differ.' }
        if (-not $assembly.MainModule.GetType('Killcam.Replay.ReplayController')) { throw 'Missing renamed replay API.' }
        if (@($assembly.MainModule.GetTypes() | Where-Object FullName -Like 'KillcamReplay*').Count) { throw 'Old runtime types remain.' }
        if (@($plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInIncompatibility' }).Count -ne 1) {
            throw 'Missing duplicate replay-plugin protection.'
        }
        $calls = (@($plugin.Methods | Where-Object Name -eq 'Awake')[0].Body.Instructions | ForEach-Object { [string]$_.Operand }) -join "`n"
        if ($calls.IndexOf('ConfigMigration::CopyLegacyIfMissing') -lt 0 -or
            $calls.IndexOf('ConfigMigration::CopyLegacyIfMissing') -ge $calls.IndexOf('BepInEx.Configuration.ConfigFile::Bind')) {
            throw 'Config migration no longer precedes Bind.'
        }
    } finally { $assembly.Dispose() }
    Run-Dotnet @('run', '--project', "$PSScriptRoot/RenameChecks.csproj", '-c', 'Release')
    $fake = Join-Path $run 'install'
    [IO.Directory]::CreateDirectory("$fake/BepInEx/plugins/old") | Out-Null
    [IO.Directory]::CreateDirectory("$fake/BepInEx/config") | Out-Null
    [IO.File]::WriteAllText("$fake/BepInEx/plugins/old/maschine-Killcam.dll", 'archived-original-killcam')
    [IO.File]::WriteAllText("$fake/BepInEx/plugins/maschine-KillcamReplay.dll", 'previous-replay-build')
    [IO.File]::WriteAllText("$fake/BepInEx/plugins/maschine-KillcamReplay.pdb", 'old-symbols')
    [IO.File]::WriteAllText("$fake/BepInEx/plugins/unrelated.dll", 'do-not-touch')
    [IO.File]::WriteAllText("$fake/BepInEx/config/com.maschine.KillcamReplay.cfg", 'saved-config')
    & "$repo/tools/Deploy-Killcam.ps1" -AssemblyPath $dll -SptPath $fake
    if (@(Get-ChildItem "$fake/BepInEx/plugins" -File -Recurse | Where-Object Name -Match '^maschine-Killcam').Count -ne 1) { throw 'Deployment retained a duplicate.' }
    if ((Get-FileHash "$fake/BepInEx/plugins/maschine-Killcam.dll").Hash -ne (Get-FileHash $dll).Hash) { throw 'Wrong deployed build.' }
    if ([IO.File]::ReadAllText("$fake/BepInEx/plugins/unrelated.dll") -ne 'do-not-touch') { throw 'Foreign plugin changed.' }
    if ([IO.File]::ReadAllText("$fake/BepInEx/config/com.maschine.KillcamReplay.cfg") -ne 'saved-config') { throw 'Deploy altered config.' }
    if (@(Get-ChildItem "$fake/BepInEx/killcam-backups" -File -Recurse).Count -ne 3) { throw 'Previous files were not all recoverable.' }
    # A failed preflight must not remove any already installed file.
    $locked = [IO.File]::Open("$fake/BepInEx/plugins/maschine-Killcam.dll", 'Open', 'Read', 'Read')
    try {
        $refused = $false
        try { & "$repo/tools/Deploy-Killcam.ps1" -AssemblyPath $dll -SptPath $fake } catch { $refused = $true }
        if (-not $refused) { throw 'Deployment accepted a locked plugin.' }
    } finally { $locked.Dispose() }
    if (-not (Test-Path "$fake/BepInEx/plugins/maschine-Killcam.dll")) { throw 'Failed preflight removed installed plugin.' }
} finally {
    if ((InstalledSnapshot) -cne $before) { throw 'Offline rename checks changed the live install.' }
}
Write-Output 'KILLCAM_RENAME_CHECKS_PASSED'
