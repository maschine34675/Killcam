# KillcamReplay

GUID `com.maschine.KillcamReplay`, Assembly `maschine-KillcamReplay` — naming scheme as in
[EasyMounting](https://github.com/maschine34675/EasyMounting).

A true killcam for SPT: on death, the final action is played back **from the killer's point
of view** — not a live view of the killer (as in KillAndDamageInfo), but a replay of the
recorded view direction and position of the last seconds before the kill.

## How it works

**Recording (during the raid):**
A `SnapshotRecorder` samples at a fixed rate (default 20 Hz), for every living player/bot,
the camera position (`Player.CameraPosition`), view direction (`Player.Rotation`, yaw/pitch),
body pose (`PoseLevel`, `IsInPronePose`) and the world pose of the held weapon
(`PlayerBones.WeaponRoot.Original`) into a ring buffer (default 15 s). Shots are marked with
a timestamp via a postfix on `Player.OnMakingShot`. During the replay, a script-free mesh
copy of the killer's weapon (`WeaponGhost`) travels along its recorded track in front of the
camera — you can see what he aims with and where; the muzzle flash sits at its muzzle.

**Replay (on death):**
`Player.OnDead` provides the killer (`LastAggressor`). The window of the last N seconds
(default 5 s) is extracted from his ring buffer. `LocalGame.Stop()` is deferred (same
pattern as the KillAndDamageInfo killcam) and the main camera is interpolated along the
recorded path (position: lerp, yaw/pitch: LerpAngle). The pose is applied in
`Application.onBeforeRender` — after all `LateUpdate`s, so the still-running
`PlayerCameraController` (corpse cam) cannot overwrite the rendered image. Deliberately,
**no** controller is destroyed or created: `PlayerCameraController.Destroy` detaches the
`EffectsController` from the player, whose death handler then throws NullRefs and aborts the
end-of-raid sequence (soft lock). The death fade (`EffectsController.method_9`,
DeathFade + FastBlur) is suppressed via prefix during the replay, otherwise it would sit
black on top of it.

**Slow motion:**
The last `SlowMoSeconds` (default 1.5 s) before the kill run in slow motion
(`SlowMoFactor`, default 0.35×) — purely via the playback clock, no `Time.timeScale`;
the live world keeps running normally. Smooth transition over 0.4 s when entering the window.
The deferred-stop delay accounts for the stretched wall-clock time.

**Soft ending (live tail):**
At the end of the recorded path (the kill moment) the replay does not end abruptly: a short
fade to black (~0.2 s), under full black the scene switches to the live world (puppet
removed, hidden bots and your own corpse visible again — only the killer stays hidden),
fade back, then the camera follows **the killer's eyes live** for `LiveTailSeconds`
(default 5 s) (`FollowKillerInTail`; fallback: last replay position if he has died in the
meantime). Afterwards the replay itself fades to black and holds black until the re-invoked
`LocalGame.Stop` has raised its own blackscreen (detected via
`DeferStopPatch.StopReinvokedAt` + 1.5 s; emergency timeout 5 s) — so the corpse cam never
flashes up between replay end and the end-of-raid transition. During the live tail the
killer himself stays hidden (the camera sits in his eyes), but his **held weapon is shown
again** (`LivePlayerHider.ShowHeldWeapon`) — it rides on his live hands and reads like a
natural first-person view; weapon views newly created while reloading also stay visible
(excluded from the refresh sweep). If `Stop` was not deferred (e.g. because a respawn mod
intercepted the death), the camera is returned directly after the tail — no black hold,
which would otherwise cover the respawn prompt. In the F9 debug replay the tail is
deliberately disabled (the player is alive and would be blind for that long).

**Victim ghost (phase 2/3):**
During the replay you watch yourself through the killer's eyes. Two representations:

- **Real character model** (`GhostPuppet`, default): The `BoneRecorder` records the
  complete skeleton of the local player (only the local player — which is what keeps it
  affordable: ~a few hundred transform reads per tick, flat preallocated arrays, a few MB).
  On replay the corpse is cloned — `Corpse.CreateCorpse` converts the real, dressed player
  GameObject in place, so the clone wears clothing + gear. Before stripping, every renderer
  on the clone is forced to `shadowCastingMode = On` — otherwise head/upper body stay
  invisible, because EFT renders them on your own player in first person with
  `ShadowCastingMode.ShadowsOnly` (the corpse conversion never resets that) and a raw clone
  copies this state along. The obvious route via
  `PlayerBody.UpdatePlayerRenders(ThirdPerson, ...)` — the game's own method for this — does
  NOT work on the clone: it iterates `PlayerBody.BodySkins`, which is only populated at
  runtime (Awake/Equip) and is empty on a clone whose Awake we deliberately never run
  (silent no-op, no error). The direct renderer sweep bypasses that entirely. The clone is
  instantiated under an INACTIVE holder (no Awake runs), all scripts/physics/animator are
  stripped via `DestroyImmediate` (no Awake → no OnDestroy side effects), bones are mapped
  by hierarchy path and overwritten per frame with the recorded poses — **no animator
  needed**, exact playback of gait, lean and aiming. The source (corpse or live body) is
  hidden after cloning so it does not stay visible next to the running ghost.
- **Silhouette** (`ReplayGhost`, automatic fallback): capsule body with pose-dependent
  height, head sphere, view-direction bar — in case puppet creation fails or
  `UseRealModelGhost` is off.

In the F9 debug replay the ghost is your own path (puppet source = living body; your own
head stays invisible there for FPS reasons): stand in front of a bot, F9, watch yourself.

## Performance budget

- **Idle cost per frame:** 2 float comparisons (sample not due → early-out).
- **Per sample tick (20×/s):** per living player 1 transform read + 1 Vector2 read +
  1 struct write into a preallocated array. With ~30 bots: microsecond range.
- **No GC allocations in the hot path:** ring buffers are preallocated (structs), the
  track dictionary only grows when a new player spawns. Extracting the replay window
  allocates only once, on death.
- **Memory:** 32 bytes/snapshot × 20 Hz × 15 s ≈ 9.6 KB per player; with 40 players ≈ 385 KB.
  The bone recorder (local player only) takes a few MB depending on skeleton size
  (logged at raid start).
- **No scene searches on respawn cameras (0.9.10):** the PerfectCulling sampler fix finds the
  stale sampler from the samplers it saw starting and the sampler's own static instance. The
  earlier `FindObjectsOfType` walked the whole scene each time a respawn mod rebuilt the camera
  and grew with every corpse a long raid keeps (about 300 ms per call late in a 25-minute
  Arena raid, measured with Redeploy.Core 1.0.2).

## Configuration (BepInEx F12 / config file)

| Option | Default | Description |
|---|---|---|
| Recording.SampleRate | 20 Hz | Recording rate |
| Recording.BufferSeconds | 15 s | Ring buffer length |
| Replay.Duration | 10 s | Replay length |
| Replay.LiveTailSeconds | 5 s | Keep showing live after replay end (0 = off) |
| Replay.SlowMoSeconds | 1.5 s | Play the last N seconds before the kill in slow motion (0 = off) |
| Replay.SlowMoFactor | 0.35 | Playback speed inside the slow-motion window |
| Replay.FollowKillerInTail | true | Follow the killer live during the tail instead of staying static |
| Replay.HudTopFraction | 0.87 | Vertical position of the killcam HUD (fraction of screen height from the top) |
| Replay.ShowMuzzleFlashes | true | 3D muzzle flashes at recorded shot timestamps |
| Replay.ShowKillerWeapon | true | Mesh copy of the killer's weapon travels its recorded track in front of the camera |
| Replay.DeathReplay | true | Replay on your own death |
| Replay.ShowVictimGhost | true | Show your own silhouette (ghost) in the replay |
| Replay.UseRealModelGhost | true | Real character model instead of silhouette (automatic fallback) |
| Replay.HideLiveBots | true | Hide living bots during the replay |
| Replay.InvertPitch | false | Invert vertical view direction (in case the camera tilts the wrong way) |
| Replay.WaitForReplayBeforeRespawn | true | A respawn during the death replay waits until the replay has faded out (not in ArenaMode raids) |
| Replay.SkipKey | Space | Skip the running replay; a waiting respawn or raid end follows behind a short fade to black |
| Debug.ReplayKey | F9 | Immediately play a replay of the nearest bot (testing without dying) |

## Known limitations (prototype)

- **The world keeps running live** — living bots (including the killer) are hidden during
  the replay though (`LivePlayerHider`: `Renderer.enabled`, AI/logic keep running; restored
  afterwards). Equipment views (weapon, chest rig, backpack, headwear, ...) are pooled
  objects (`AssetPoolObject`) attached to a bone — not guaranteed to be descendants of
  `player.gameObject` — so `PlayerBody.SlotViews` (the game's own authoritative list of
  every currently equipped view) is traversed additionally and its `Renderers` are hidden
  explicitly. The weapon **in the hands** is NOT a SlotView model (the slot suppresses its
  body model while the item is held) — it is a separate view object of the hands controller
  under the `WeaponRoot` bone and is swept separately. A flashlight moreover is not a
  renderer but a `UnityEngine.Light` component (typically sitting on the hand weapon) that
  keeps shining when only the mesh is hidden — so all `Light` components (player hierarchy,
  slots, WeaponRoot) are disabled as well. Your own corpse/live body (the puppet source) is
  also hidden as soon as the clone is up. Additional actors as ghosts (e.g. the killer in a
  third-person view) would be a later extension.
- **Muzzle flash = approximation** — at recorded shot timestamps a point light + stretched
  glow ellipsoid appears in shot direction for ~70 ms (`ShotFlash`; Unity has no cone
  primitive): killer shots just in front of/below the camera (his eyes ARE the camera, his
  weapon is not rendered), own shots at the estimated muzzle of the puppet weapon (furthest
  +Z extent of the copied meshes). No particle effect, no sound. Can be disabled via
  `ShowMuzzleFlashes`.
- **Hidden bots are re-swept every 0.5 s** (`RefreshHidden`): reloading and weapon switching
  create NEW view objects mid-replay (magazine in the hand, round, different weapon) that
  did not exist at the initial hide and would otherwise be visible, floating freely.
- **The Odin NullRef on corpse cloning is no longer logged**: it comes from the item scripts
  of the slot-view equipment (holster/back weapon), is demonstrably without consequence
  (the components are stripped right afterwards) — the Unity logger is muted exactly for the
  synchronous `Instantiate` call.
- **The "Killed in Action" panel** appears as a UI overlay on top of the replay (as with the
  KillAndDamageInfo killcam).
- **Weapon on the puppet = pure mesh copy**: the Odin/Sirenix deserialization of the weapon
  item throws an (internally caught) NullReferenceException on raw cloning and leaves the
  weapon mesh empty. So the weapon is not cloned at all; its meshes are copied individually
  (`AttachWeaponMeshes`: new GameObjects with sharedMesh + sharedMaterials, without any
  scripts) and attached to the puppet's mapped WeaponRoot bone — they ride on the recorded
  hand animation. Skinned meshes (e.g. straps) are skipped; moving weapon parts (bolt etc.)
  are static in the copy.
- **Coexistence with KillAndDamageInfo:** its live killcam also owns the death sequence
  (deferred stop + camera takeover). If that mod is loaded, the death replay disables itself
  automatically (log warning); the F9 debug replay still works. To test the death replay,
  temporarily remove the KillAndDamageInfo DLL from `BepInEx/plugins`.
- **Respawn compatibility (e.g. Redeploy):** recording is deliberately NOT stopped
  permanently on your own death (only during the ~10 s a replay actively occupies the
  screen) — otherwise recording would end for good after the first death and every further
  replay in the same round would fail with "Killer track too short". The `BoneRecorder`
  detects a character switch (new player GameObject after respawn) and reinitializes itself
  automatically. Verified in-game with Redeploy (multiple deaths/replays in one round).
  Since 0.9.8 the mod consumes `RespawnApi` v1 of `maschine-Redeploy.Core` through a copy of
  Redeploy's interop file (`src/Interop/RedeployRespawnInterop.cs`, namespace changed only;
  re-copy it from the Redeploy repo, never edit it). When the replay's victim body is
  replaced, the playback ends in that same frame ("Playback end ...: replacement (death)")
  instead of a frame later on the destroyed camera ("camera destroyed", still the fallback
  without Core). The death log line also reports `respawnPending` (true/false/unknown); the
  replay itself does not change on it yet, and the deferred-Stop hand-off stays, because a
  Killed stop can still follow a claimed death (give-up, match end). A body retired by a live
  swap that dies later no longer starts a replay: only the current `MainPlayer` counts.
  Since 0.9.9 a respawn **waits for the replay** (`WaitForReplayBeforeRespawn`, on by default),
  the respawn counterpart of the deferred `Stop`: choosing Respawn in Redeploy's prompt while the
  replay runs no longer cuts it. The replay plays its live tail, fades to black, and the
  respawn runs under that black; the new view fades in. Mechanism: a postfix on
  `Redeploy.Core`'s `RespawnEngine.RunRespawn` (resolved by name, no compile dependency) wraps the
  pipeline coroutine in a wait for the replay's hand-off, only when the request replaces the
  replay's victim. The wait is capped (the same bound as the deferred Stop), ends early when the
  raid is ending, and never applies in ArenaMode raids (`ArenaSession.ReplaySeconds` is Arena's
  timing contract). If the Core members are missing, the hold stays off and a respawn cuts the
  replay as before. The **skip key** (`SkipKey`, Space; works while other keys are held) ends the
  replay at any time; a waiting respawn or raid end then follows right away behind the fade to
  black. The hold starts when Redeploy actually starts the respawn, i.e. after its own
  `RespawnCooldownSeconds`: skipping during that cooldown simply ends the replay, and the respawn
  follows when the cooldown is over. If the raid ends or the cap runs out while a respawn waits,
  the hold lets go and the replay ends like one nobody waits for. The pure hand-off state and
  decisions are `src/Replay/RespawnHandoff.cs`, checked offline by `tools/HandoffChecks`
  (`dotnet run --project tools/HandoffChecks -c Release`; `HANDOFF_CHECKS_PASSED`, or
  `HANDOFF_CHECKS_PARTIAL` with exit code 3 when the built Core or the game is not found), which
  also reads the Core members the hold binds to from the built Core.

## Build

```
dotnet build -c Debug
```

Automatically copies DLL + PDB to `C:\SPT\BepInEx\plugins\`.
