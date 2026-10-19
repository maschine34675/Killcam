# Script-free replay body — 19 October 2026

Base: `8c8ee4586a8b9f4cdb531c06f2ca8b4c4045d343` (Killcam 1.0.0).
Worktree: `D:\SPT41\Development\Killcam-model-fixes`, branch
`fix/script-free-puppet-20261019`. This is a source/offline candidate, not native
acceptance or a release. No deployment, GPU interaction or commit was performed
by this implementation leaf.

## Cause and bounded change

The old `GhostPuppet` called `Object.Instantiate` on the complete native player
GameObject, muted the global Unity logger around that call, then removed scripts
and physics components. An inactive parent cannot prevent serialization work
during cloning. Removing components afterward cannot undo that work, and the
global mute also hid unrelated Unity messages emitted in the same interval.

`PuppetVisualHierarchy.Create` now builds a new hierarchy with a component
allowlist: Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer and LODGroup.
It never instantiates the source object, scripts, Animator, physics, audio or
particle components. `GhostPuppet` no longer contains the clone, logger toggle,
sanitizer or RequireComponent-removal machinery. There is no replacement logger
filter and no manufactured asset.

This repairs an own-code construction and observability defect. It does **not**
establish that the historical JetMaterial, M32 custom-item, Benelli foldable or
menu-preview failures were caused by Killcam, nor that those families disappear.
Those attribution questions remain in ArenaMode's
`docs/MODEL-POOL-INVESTIGATION-20261019.md` and the native-control workstream.

## Preserved visual and lifetime contract

- Every source Transform is represented, including inactive children and empty
  nodes. Names, hierarchy, local transforms, layers and activeSelf are preserved.
  The root starts at the source world position/rotation with source lossyScale
  under an identity holder. Existing recorded path lookup and root/bone playback
  remain unchanged.
- Skin bones and rootBone map by source identity to owned transforms, including
  duplicate names and null slots. Mesh references, blendshape weights, local
  bounds and skin settings are copied. A non-null external bone is refused with
  the existing Error/silhouette fallback rather than retaining a live body bone.
  A body with no supported mesh renderer also falls back.
- Shared mesh/material assets remain EFT-owned. No material is instantiated and
  no shared asset is destroyed. Renderer-wide and per-material property blocks
  are copied separately; empty blocks become null so an absent material override
  does not mask the renderer-wide block. Enabled/render flags, sorting, probe
  settings and internal probe anchors are copied. ShadowsOnly becomes On for the
  first-person head/torso, as intended for replay; other shadow modes survive.
- Each source LODGroup maps its renderer arrays to owned renderers, preserving
  transition heights, fades, size, reference point and enabled state. Unsupported
  renderers are omitted; a level containing only those remains empty. No renderer
  from another level is substituted, so the helper does not duplicate geometry
  to fill an empty level. No global LOD setting changes.
- Construction stays under an inactive holder. Only a completed body with an
  initial recorded pose becomes active. The helper destroys a partially built
  root on failure; the constructor also disposes the holder on any later failure,
  including failure before holder assignment or during source hiding.
- Source renderer/light visibility is the only intentional source change, using
  the existing hiding scope. Previously enabled entries are recorded before
  changing them, so partial failure restores already hidden entries. Previously
  disabled entries remain disabled. Disposal immediately deactivates the owned
  holder, restores source entries individually, and schedules owned destruction;
  repeated disposal and Apply after disposal are safe.
- The held weapon remains a separate static mesh copy under its existing world
  pivot. Selection, recorded root/animation-track choice, pose interpolation,
  recording, replay timing, camera and respawn hand-off are unchanged. A failed
  partial weapon copy now removes its own pivot and leaves the body unarmed.

## Offline evidence

`tools/PuppetVisualChecks` compiles the actual `PuppetVisualHierarchy`,
`GhostPuppet` and `WeaponMeshCollector` source against deterministic Unity/EFT
stand-ins. It does not substitute a second implementation of the copier. Native
rendering, serialization and Unity's deferred Destroy are not simulated proof;
immediate owned-holder deactivation is checked separately from object ownership.

Final verification command, from this worktree:

```powershell
pwsh -NoProfile -File tools/PuppetVisualChecks/Verify.ps1
```

Result: `PUPPET_VISUAL_VERIFICATION_PASSED positive=14 mutants=8 noDeployBuild=True`.
The 14 positive cases cover scaled root/path/active state; skin ownership and
blendshapes; appearance and independent/absent material overrides; LOD mapping and
an empty unsupported level; component allowlist/inactive assembly/no global mute;
external-bone, active-holder and meshless-source refusals; partial component
failure; owned pose/source restoration/repeated disposal; constructor failure
before holder assignment; malformed pose rollback; partial hiding rollback; and
partial weapon failure with source light/material preservation.

Eight deliberately defective source variants must each exit 1 with the exact
expected behavioral failure, not a compiler error: live native bone, live native
LOD renderer, lost material override, leaked helper hierarchy, raw source clone,
global logger mute, source not restored, and missing constructor rollback. The
production tests run again afterward so the final executable is not a mutant.

The real plugin Release build then passed with **zero warnings and zero errors**
using the absolute `tools/NoDeploy.targets` override. Preprocessing confirms the
original deployment target at `obj/puppet-nodeploy-expanded.xml:1530` is replaced
by the empty target at line 10314. `git diff --check` passed. Parent independently
reported the unchanged HandoffChecks passing 72 assertions; this leaf did not
rerun them or modify their code.

An earlier verifier attempt exposed SDK inclusion of ignored mutant `.cs` files
under `artifacts`: the tests passed but the real plugin build failed on duplicate
definitions. `Killcam.csproj` now excludes `artifacts\**` as well as `tools\**`.
The complete verifier and real build were rerun successfully after that fix and
again after the final empty-property-block safeguard.

Candidate (version remains 1.0.0):

| Item | Value |
| --- | --- |
| DLL | `bin/Release/maschine-Killcam.dll` |
| SHA-256 | `56BF2986A6F330BC396CC3E3314FFDF39E2D9F63EA0584F438DEB7FA5049E11C` |
| MVID | `9821838e-2321-4c8e-aeba-736432a71efc` |

## Four-pass review and remaining native gate

1. Ownership/correctness: inspected each copied component and reference; source
   bone, LOD, material and property-block mutation controls distinguish the
   intended behavior from broken implementations.
2. Integration/lifetime: reviewed constructor failure, partial hiding, weapon
   failure, repeated disposal, unchanged pose/pivot/timing, and native skins.
   Parent also confirmed the native dressed body resolves to ordinary skinned
   renderers and found no blocking issue in the initial helper/GhostPuppet diff.
3. Verification/build: all 14 positive cases and eight defective controls pass;
   the real game-reference build succeeds with the verified empty deployment
   override. The SDK artifact inclusion failure was corrected, not ignored.
4. Final risk/documentation review: added the absent-property-block safeguard and
   reran the complete verifier/build; updated AGENTS and implementation guidance,
   marked the 18 October mute description historical, and checked whitespace.

Native acceptance must prove a non-null actual puppet, its component allowlist,
owned skin/LOD references, recognizable dressed body/weapon, correct pose and
LOD transitions, and restoration after skip/end/death/respawn. A quiet log or a
silhouette is insufficient. Capture the actually loaded DLL identity and compare
unmuted logs without attributing unrelated failures to this repair.

Known bounds: particles/trails/lines are intentionally not replay body geometry;
external skin bones trigger fallback and external probe anchors are dropped.
The root uses world TRS/lossyScale, as the former world-preserving clone did;
arbitrary shear from a rotated nonuniform ancestor is not representable by one
detached TRS root. The harness checks scaled/rotated root transforms, but cannot
certify native mesh deformation, shader appearance, Unity LOD selection or all
asset combinations. The held weapon's existing static-only geometry limitations
remain. No native success is claimed in this document.
