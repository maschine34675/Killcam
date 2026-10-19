param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$worktreeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $PSScriptRoot 'PuppetVisualChecks.csproj'
$visualPath = Join-Path $worktreeRoot 'src\Replay\PuppetVisualHierarchy.cs'
$puppetPath = Join-Path $worktreeRoot 'src\Replay\GhostPuppet.cs'
$visual = [IO.File]::ReadAllText($visualPath)
$puppet = [IO.File]::ReadAllText($puppetPath)
$scratch = Join-Path $worktreeRoot ('artifacts\puppet-visual-checks\' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null

function Run-Checks([string]$visualSource, [string]$puppetSource, [string]$expectedFailure) {
    $output = @(& dotnet run --project $project -c Release "-p:VisualHierarchySource=$visualSource" "-p:PuppetSource=$puppetSource" 2>&1)
    $code = $LASTEXITCODE
    $text = $output -join "`n"
    if ($expectedFailure) {
        if ($code -ne 1 -or !$text.Contains('PUPPET_VISUAL_CHECKS_FAILED') -or !$text.Contains($expectedFailure)) {
            throw "Mutant did not fail its intended behavioral check (exit $code):`n$text"
        }
    } elseif ($code -ne 0 -or !$text.Contains('PUPPET_VISUAL_CHECKS_PASSED checks=14')) {
        throw "Production checks failed (exit $code):`n$text"
    } else {
        $output | ForEach-Object { Write-Output $_ }
    }
}

Run-Checks $visualPath $puppetPath ''
$mutants = @(
    @{ Name='native-bone-reference'; File='visual'; Old='if (transforms.TryGetValue(bone, out var copy)) return copy;'; New='if (transforms.TryGetValue(bone, out var copy)) return bone;'; Failure='skin bone did not map by source identity' },
    @{ Name='native-lod-reference'; File='visual'; Old='mapped.Add(target);'; New='mapped.Add(renderer);'; Failure='LOD0 retained native renderer' },
    @{ Name='lost-material-override'; File='visual'; Old='target.SetPropertyBlock(properties.isEmpty ? null : properties, i);'; New='// defective control: omit material override'; Failure='material property block lost' },
    @{ Name='leaked-partial-hierarchy'; File='visual'; Old='UnityEngine.Object.Destroy(root);'; New='// defective control: omit root cleanup'; Failure='external bone failure leaked hierarchy' },
    @{ Name='script-bearing-clone'; File='visual'; Old='root = new GameObject(source.name);'; New='root = UnityEngine.Object.Instantiate(source);'; Failure='Forbidden native/script-bearing clone' },
    @{ Name='global-logger-mute'; File='visual'; Old='CopyLods(source, transforms, renderers);'; New='Debug.unityLogger.logEnabled = false; CopyLods(source, transforms, renderers);'; Failure='serialization or global logger mutation occurred' },
    @{ Name='source-not-restored'; File='puppet'; Old='if (renderer != null) renderer.enabled = true;'; New='if (renderer != null) renderer.enabled = false;'; Failure='dispose leaked/failed source restore' },
    @{ Name='constructor-leak'; File='puppet'; Old="            Destroy();`n            throw;"; New="            // defective control: omit constructor rollback`n            throw;"; Failure='pose failure leaked holder' }
)
foreach ($mutant in $mutants) {
    $original = if ($mutant.File -eq 'visual') { $visual } else { $puppet }
    $original = $original.Replace("`r`n", "`n")
    if (!$original.Contains($mutant.Old)) { throw "Mutation anchor missing: $($mutant.Name)" }
    $changed = $original.Replace($mutant.Old, $mutant.New)
    $path = Join-Path $scratch ($mutant.Name + '.cs')
    [IO.File]::WriteAllText($path, $changed)
    if ($mutant.File -eq 'visual') { Run-Checks $path $puppetPath $mutant.Failure }
    else { Run-Checks $visualPath $path $mutant.Failure }
    Write-Output "REJECTED_DEFECTIVE_CONTROL $($mutant.Name)"
}
# Leave the executable built from production source, not the last mutant.
Run-Checks $visualPath $puppetPath ''
if (!$SkipBuild) {
    & dotnet build (Join-Path $worktreeRoot 'Killcam.csproj') -c Release "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $worktreeRoot 'tools\NoDeploy.targets')" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Real no-deploy build failed' }
}
Write-Output "PUPPET_VISUAL_VERIFICATION_PASSED positive=14 mutants=$($mutants.Count) noDeployBuild=$(!$SkipBuild)"
