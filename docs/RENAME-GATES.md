# Gates: Killcam rename and native acceptance

OWNS: src/**, tools/**, docs/**, assets/thumbnail.svg, KillcamReplay.csproj, Killcam.csproj, Shared.props, README.md, CLAUDE.md, .gitignore

Scope: Rename the existing unpublished mod and repository to Killcam, preserve
settings, update Arena integration, deploy the reviewed builds, and exercise a
real death replay on an available GPU test profile. Keep the repository private.
Cross-repository integration is performed by the same parent agent, not by a
concurrent writer; Arena and Redeploy documentation changes are scoped separately.

- [x] G1: Renamed binaries and settings/deployment migration pass offline checks without touching the live install.
  CHECK: pwsh -NoProfile -File tools/RenameChecks/Verify.ps1
  EXPECT: KILLCAM_RENAME_CHECKS_PASSED
  EVIDENCE: automatic-evidence=v1; definition-sha256=ece3e488214aebcd2ff8646cc4fc2e5a0826a52f595674dd06ecbe1c5dcac87e; exit=0; EXPECT=matched; output-sha256=79010efdf18eb8ba6689a7a54685438a71ab8304cf8d7506f96ff9668ab5a108; output-bytes=822; shell=C:\Program Files\PowerShell\7\pwsh.exe; cwd=D:\SPT41\Development\Killcam; path=fa64bc5e3f9a/33 entries

- [x] G2: Existing replay/respawn handoff behavior remains valid against current Core.
  CHECK: dotnet run --project tools/HandoffChecks -c Release
  EXPECT: HANDOFF_CHECKS_PASSED
  EVIDENCE: automatic-evidence=v1; definition-sha256=069e80bac8ae0383ffa54decc1748de60a99a1073be2a92f5ae2dbeef4cad06a; exit=0; EXPECT=matched; output-sha256=ffdd7a4b663cd7070375a5959dc1d92540ee09118d72683b751f08c312fac2fe; output-bytes=50; shell=C:\Program Files\PowerShell\7\pwsh.exe; cwd=D:\SPT41\Development\Killcam; path=fa64bc5e3f9a/33 entries

- [x] G3: Arena's renamed identity binding and camera-release safety pass production-linked and negative-control tests.
  CHECK: pwsh -NoProfile -File ../ArenaMode/tools/ArenaKillcamChecks/Verify.ps1
  EXPECT: ARENA_KILLCAM_CHECKS_PASSED
  EVIDENCE: automatic-evidence=v1; definition-sha256=05f6ed09304166e8f59e001abe50fb0c6b548d2860aa5fdb1eff26a73487da5a; exit=0; EXPECT=matched; output-sha256=11256760d3f599896ff0039a1e747fe02938a51d8c7a51fa6e1c3b8432f1307c; output-bytes=670; shell=C:\Program Files\PowerShell\7\pwsh.exe; cwd=D:\SPT41\Development\Killcam; path=fa64bc5e3f9a/33 entries

- [x] G4: Independent adversarial review accepts identity migration, duplicate handling and unchanged camera ownership.
  EVIDENCE: Independent read-only reviewer checked 12396a6, 63c2766 and Arena 5d53458; reran 72 handoff, 12 migration and the original 170 Arena assertions, then reviewed the six added legacy-identity assertions and UNC path correction. No remaining blockers. See RENAME-20261018.md.

- [x] G5: Existing GitHub repository and local checkout/worktrees use the new name without losing history or becoming public.
  EVIDENCE: GitHub repository ID 1321886160 unchanged; name Killcam, private true, main unchanged. Local Killcam and Killcam-log-followup worktrees readable after repair; old HEAD 00939e5 remains an ancestor of main. Remote URL now points to the same renamed repository.

- [x] G6: GPU run loads only the new Killcam and matching Arena build, preserves settings, and completes death replay followed by safe respawn/exit.
  EVIDENCE: Process 194a160f, native Factory DM enemy death/replay/respawn and two real post-respawn shots passed; frame 12937 visually checked by test agent and parent. Native migration retained all 19 saved entries. Settings/profile and unrelated DLL comparisons passed. See RENAME-20261018.md and ignored artifacts/native-rename-20261018/NATIVE-RESULTS.md; no clean-install or all-modes claim.

- [x] G7: Test changes are restored, scoped work is committed, and final evidence distinguishes offline and native checks.
  EVIDENCE: Native restoration verifier independently rerun by parent: KILLCAM_NATIVE_RENAME_VERIFIED. Reports committed as Killcam b521321 and Arena 0440fc4; implementation 12396a6/63c2766 and 5d53458, Redeploy documentation 71e1513. Only the pre-existing unrelated Arena tools/__pycache__/ remains untracked. GPU2 MainMenu, no active test hooks, control free; report explicitly limits acceptance to one Factory DM replay/respawn and lists native diagnostics.
