# Killcam implementation notes

Player setup, controls and compatibility are in [README.md](../README.md).
These notes describe the current implementation; historical measurements are not
a performance guarantee for every map, mod set or machine.

## How it works

**Recording (during the raid):**
A `SnapshotRecorder` samples at a fixed rate (default 20 Hz), for every living player/bot,
the camera position (`Player.CameraPosition`), view direction (`Player.Rotation`, yaw/pitch),
body pose (`PoseLevel`, `IsInPronePose`) and the world pose of the held weapon
(`PlayerBones.WeaponRoot.Original`) into a ring buffer (default 15 s). Shots are marked with
a timestamp via a postfix on `Player.OnMakingShot`. During the replay, a script-free mesh
copy of the killer's weapon (`WeaponGhost`) uses the recorded weapon position, while
its barrel is aligned to the recorded aim rather than the weapon-root rotation.
The killer's shot lines start at its muzzle.

**Replay (on death):**
`Player.OnDead` provides the killer (`LastAggressor`). The window of the last N seconds
(default 10 s) is extracted from his ring buffer. `LocalGame.Stop()` is deferred (same
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
ArenaMode raids use half the configured live-tail duration (normally 2.5 instead
of 5 seconds). Recorded playback, slow motion and transition fades are unchanged;
ordinary raids retain the full configured duration. The saved setting is not changed.

At the end of the recorded path (the kill moment) the replay does not end abruptly: a short
fade to black (~0.2 s), under full black the scene switches to the live world (puppet
removed, hidden bots and your own corpse visible again — only the killer stays hidden),
fade back, then the camera follows **the killer's eyes live** for `LiveTailSeconds`
(default 5 s) (`FollowKillerInTail`; fallback: last replay position if he has died in the
meantime). Afterwards the replay itself fades to black and holds black until the re-invoked
`LocalGame.Stop` has raised its own blackscreen (detected via
`KillcamDeferStopPatch.StopReinvokedAt` + 1.5 s; emergency timeout 5 s) — so the corpse cam never
flashes up between replay end and the end-of-raid transition. During the live tail the
killer himself stays hidden (the camera sits in his eyes), but his **held weapon is shown
again** (`LivePlayerHider.ShowHeldWeapon`) — it rides on his live hands and reads like a
natural first-person view; weapon views newly created while reloading also stay visible
(excluded from the refresh sweep). If neither a deferred `Stop` nor a waiting
replacement owns the handoff, the camera is returned after the final fade without
a black hold. A waiting Core respawn keeps black until replacement; its death
interception can mean there is no deferred `Stop`. In the F9 debug replay the tail is
deliberately disabled (the player is alive and would be blind for that long).

**Victim ghost (phase 2/3):**
During the replay you watch yourself through the killer's eyes. Two representations:

- **Real character model** (`GhostPuppet`, default): The `BoneRecorder` records the
  complete skeleton of the local player (only the local player — which is what keeps it
  limits recording work to one skeleton, using flat preallocated arrays).
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

## Recording cost and lifetime

The recorder samples at the configured rate, using preallocated per-player ring
buffers. Each snapshot contains camera position, view direction, stance and
weapon position/rotation; it is no longer the small camera-only structure used
by early estimates. The local player's skeleton has a separate bone buffer.
Increasing sample rate, buffer duration and concurrent actor count raises cost.
Replay extraction and mesh/puppet construction allocate additional memory.

The recorder checks for stale tracks every five seconds. Its cutoff uses the
configured `Replay / Duration` plus two seconds, not the buffer size or Arena's
duration override. Destroyed actors are removed during that pass; the current
local body is kept even when dead. Recording pauses while a replay owns the screen,
clears actor tracks when the world disappears, and resets at the next raid.
The per-raid PerfectCulling sampler fix avoids the earlier
scene-wide search on each respawn. These are implementation properties, not a
new CPU benchmark; the README makes no fixed frame-time or memory promise.

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
- **Shot lines = aim, not ballistics** — at each recorded shot timestamp a line (red for
  the killer, cyan for your own shots; `ShotTracers`, pooled `LineRenderer`s) runs from
  the shooter's muzzle to the point where a ray from his eyes along his recorded aim
  meets the world within 2 km
  (`LayersMaskController.HighPolyWithTerrainMask`, i.e. walls and terrain, not bodies) —
  the point a zeroed sight converges on. It stays where it was fired and fades over 0.4 s
  of replay time, so slow motion stretches it; its width follows the camera distance so it
  stays a few pixels wide near and far. Spread, recoil and the 20 Hz sampling are not in
  the aim: a line shows where the shooter pointed, not a confirmed hit. The killer's lines
  start at his weapon ghost's muzzle (below the camera if none is shown), your own at the
  puppet weapon's. A point light at the muzzle flashes for ~70 ms per shot (`ShotFlash`).
  No particle effect, no sound. Can be disabled via `ShowShotLines`.
- **Hidden bots are re-swept every 0.5 s** (`RefreshHidden`): reloading and weapon switching
  create NEW view objects mid-replay (magazine in the hand, round, different weapon) that
  did not exist at the initial hide and would otherwise be visible, floating freely.
- **Logging during corpse cloning:** equipment item scripts can emit an Odin
  NullReferenceException during raw cloning. Those components are stripped before
  playback. The Unity logger is muted for the synchronous `Instantiate` call and
  restored in `finally`. This also hides other Unity output inside that call;
  absence of a message is not proof that cloning had no error.
- **The "Killed in Action" panel** appears as a UI overlay on top of the replay (as with the
  KillAndDamageInfo killcam).
- **Weapon on the puppet = pure mesh copy**: the Odin/Sirenix deserialization of the weapon
  item throws an (internally caught) NullReferenceException on raw cloning and leaves the
  weapon mesh empty. So the weapon is not cloned at all; its meshes are copied individually
  (`AttachWeaponMeshes`: new GameObjects with sharedMesh + sharedMaterials, without any
  scripts) under an independent pivot in the puppet holder. Playback sets that pivot's
  world position and rotation from the recorded weapon-root or animation track,
  rather than parenting it to the mapped WeaponRoot bone. Skinned meshes (e.g. straps)
  are skipped; moving weapon parts (bolt etc.) are static in the copy.
- **Coexistence with KillAndDamageInfo:** its live killcam also owns the death sequence
  (deferred stop + camera takeover). If that mod is loaded, the death replay disables itself
  automatically (log warning); the F9 debug replay still works. To test the death replay,
  temporarily remove the KillAndDamageInfo DLL from `BepInEx/plugins`.
- **Respawn compatibility (e.g. Redeploy):** recording is deliberately NOT stopped
  permanently on your own death (only while a replay actively occupies the
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
