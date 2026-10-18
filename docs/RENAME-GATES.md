# Gates: Killcam rename and native acceptance

OWNS: src/**, tools/**, docs/**, assets/thumbnail.svg, KillcamReplay.csproj, Killcam.csproj, Shared.props, README.md, CLAUDE.md, .gitignore

Scope: Rename the existing unpublished mod and repository to Killcam, preserve
settings, update Arena integration, deploy the reviewed builds, and exercise a
real death replay on an available GPU test profile. Keep the repository private.
Cross-repository integration is performed by the same parent agent, not by a
concurrent writer; Arena and Redeploy documentation changes are scoped separately.

- [ ] G1: Renamed binaries and settings/deployment migration pass offline checks without touching the live install.
  CHECK: pwsh -NoProfile -File tools/RenameChecks/Verify.ps1
  EXPECT: KILLCAM_RENAME_CHECKS_PASSED
  EVIDENCE: pending

- [ ] G2: Existing replay/respawn handoff behavior remains valid against current Core.
  CHECK: dotnet run --project tools/HandoffChecks -c Release
  EXPECT: HANDOFF_CHECKS_PASSED
  EVIDENCE: pending

- [ ] G3: Arena's renamed identity binding and camera-release safety pass production-linked and negative-control tests.
  CHECK: pwsh -NoProfile -File ../ArenaMode/tools/ArenaKillcamChecks/Verify.ps1
  EXPECT: ARENA_KILLCAM_CHECKS_PASSED
  EVIDENCE: pending

- [ ] G4: Independent adversarial review accepts identity migration, duplicate handling and unchanged camera ownership.
  EVIDENCE: pending

- [ ] G5: Existing GitHub repository and local checkout/worktrees use the new name without losing history or becoming public.
  EVIDENCE: pending

- [ ] G6: GPU run loads only the new Killcam and matching Arena build, preserves settings, and completes death replay followed by safe respawn/exit.
  EVIDENCE: pending

- [ ] G7: Test changes are restored, scoped work is committed, and final evidence distinguishes offline and native checks.
  EVIDENCE: pending
