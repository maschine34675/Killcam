# Killcam

Replays the last seconds before your death from the killer's recorded viewpoint.
The raid continues in the background; after playback, the normal death screen
or a compatible mod's respawn takes over.

Current version: **1.0.1**, formerly **KillcamReplay**.

## Features

- Recorded camera movement and aim, with a configurable replay window.
- Slow motion around the killing moment and an optional live view of the killer afterward.
- Victim model with a silhouette fallback, plus a mesh copy of the killer's weapon.
- Optional shot lines and muzzle lights. These illustrate recorded aim, not confirmed hits.
- A skip key and an optional debug replay without dying.
- Optional integration with ArenaMode and the Redeploy.Core respawn API.

## Requirements and compatibility

- Built for **SPT 4.1**, EFT **0.16.9.5 / 40743**, using its BepInEx 5 and SPT client libraries.
  Other SPT/EFT versions have not been verified.
- **Client-only:** install one DLL. Killcam has no server component and does not
  require ArenaMode, Redeploy, or Redeploy.Core for ordinary death replays.
- **ArenaMode:** enable Killcam in Arena's map-selection mutators. Arena selects
  the recorded duration and holds its respawn until the replay releases the
  camera. Its live tail is half the configured duration (2.5 seconds at the
  default of 5); the saved Killcam setting is not changed. Update both mods
  when moving from KillcamReplay to Killcam.
- **Redeploy:** with its shared Core installed, a requested respawn waits for
  playback by default. Disabling `WaitForReplayBeforeRespawn` allows the body
  replacement to cut the replay short. Arena manages its own wait instead.
- **Fika/co-op:** not verified; the current hooks target local raids. Do not assume
  the single-player tests cover synchronized multiplayer deaths.

## Installation and updates

1. Close EFT.
2. Place `maschine-Killcam.dll` in `BepInEx/plugins` under your SPT installation.
3. Keep only one copy, including plugin subfolders. Remove
   `maschine-KillcamReplay.dll` and replace any existing `maschine-Killcam.dll`,
   including the earlier, separate experimental **Killcam 1.0.0**. That prototype
   shares this mod's GUID but is a different implementation.
4. Start EFT. The BepInEx log should show `maschine-Killcam 1.0.1 loaded`.

On the first renamed launch, Killcam copies
`BepInEx/config/com.maschine.KillcamReplay.cfg` to
`BepInEx/config/com.maschine.Killcam.cfg` **only if the new file is absent**.
The original is retained; an existing new config always wins. If migration
fails, Killcam stays inactive for that launch and logs the reason instead of
saving defaults over the old settings. A leftover Replay DLL prevents the new
plugin from loading; remove it rather than keeping both implementations.

For subsequent updates, replace the DLL and keep the config. To uninstall, close
EFT and remove the Killcam DLL. Its config can be kept for a later reinstall or
removed separately. Do not remove Redeploy.Core if another mod still uses it.

## Use

No setup is needed for an ordinary solo raid: after an enemy kills you, the mod
plays the recorded lead-up, slows the final moment, then briefly follows the
killer live. **Space** skips playback. The default recorded window is **10
seconds**; slow motion, transitions and the live tail make a full 10-second
recording take longer than 10 seconds to play back.

The optional **Debug replay shortcut** plays the nearest recorded bot while you
are alive, without the live tail. It is **unbound by default**. To assign a key,
enable **Advanced settings** in F12 and open Killcam's Debug section. Version
1.0.1 replaces the old `Debug / ReplayKey` with `Debug / DebugReplayHotkey`, so
previous F9 or custom debug bindings are intentionally cleared. Once you bind
the new option, that choice is retained. There is no saved video file or replay browser.

The replay needs a valid killer and at least two recorded samples. Deaths
without a usable attacker, or with no/too little recorded history, may have no
replay. This is not a reconstruction of every possible death cause.

## Configuration

Edit `BepInEx/config/com.maschine.Killcam.cfg`, or use **F12** if the BepInEx
ConfigurationManager is installed. F12 uses readable labels and a fixed order;
the table lists the stored keys. Defaults below apply to a new config; updating
preserves existing values except for the old debug shortcut described above.
Recording rate and buffer size are read at
raid start, so choose those before entering a raid.

| Section / option | Default | Meaning |
| --- | --- | --- |
| `Recording / SampleRate` | 20 Hz | Recording rate, 5–60 Hz. |
| `Recording / BufferSeconds` | 15 s | History kept per actor, 5–60 s. Keep it at least as long as the desired replay. |
| `Replay / Duration` | 10 s | Recorded window, 2–12 s; Arena can select its own duration. |
| `Replay / LiveTailSeconds` | 5 s | Live view after recording, 0–10 s; 0 disables it, Arena uses half. |
| `Replay / FollowKillerInTail` | true | Follow the killer during the live tail; otherwise hold the last replay position. |
| `Replay / SlowMoSeconds` | 1.5 s | Final recorded seconds played in slow motion, 0–5 s; 0 disables it. |
| `Replay / SlowMoFactor` | 0.35 | Playback speed during slow motion, 0.1–1. |
| `Replay / DeathReplay` | true | Automatic playback on death. Turning this off does not disable the recorder. |
| `Replay / ShowVictimGhost` | true | Show the victim during playback. |
| `Replay / UseRealModelGhost` | true | Use the character model; fall back to a silhouette when unavailable. |
| `Replay / HideLiveBots` | true | Hide live actors while their recorded scene is shown. AI continues running. |
| `Replay / ShowShotLines` | true | Killer shots in red, victim shots in cyan, with brief muzzle lights. |
| `Replay / ShowKillerWeapon` | true | Show a mesh copy of the killer's weapon. |
| `Replay / InvertPitch` | false | Invert replay view pitch. |
| `Replay / HudTopFraction` | 0.87 | HUD position measured from the top, 0–0.9 of screen height. |
| `Replay / WaitForReplayBeforeRespawn` | true | Hold a compatible Core respawn until replay handoff; Arena uses its own timing. |
| `Replay / SkipKey` | Space | Skip playback, then release the waiting respawn or raid end. |
| `Debug / DebugReplayHotkey` | Unbound | Advanced: replay the nearest bot without dying. |

The old `ShowMuzzleFlashes` entry may remain in migrated configs but is not an
active setting. `ShowShotLines` controls the recorded shot lines and muzzle lights.
The old `Debug / ReplayKey` may also remain as unused text, but is never read or
shown as an active option.

## Limitations and troubleshooting

- The world, AI and sounds continue live. This is a visual reconstruction, not
  a deterministic rewind, audio recording, or proof of bullet penetration/hits.
- Shot lines follow recorded aim against world geometry; spread and the full
  ballistic path are not reconstructed. Weapon mesh copies do not animate
  moving parts such as bolts or reproduce every skinned attachment.
- The ordinary "Killed in Action" overlay can remain visible during replay.
- Missing weapon geometry can omit the killer's weapon; a failed victim-model
  copy can use the silhouette. These fallbacks are logged.
- If no replay appears, check `DeathReplay`, the Arena Killcam mutator when
  applicable, and leftover old DLLs. The log distinguishes
  missing/short killer tracks from loading or migration errors.
- If a respawn cuts the replay short, check `WaitForReplayBeforeRespawn` and
  warnings about the Core handoff contract. Other respawn mods are not covered
  merely because Redeploy works.

The latest [native verification](docs/RENAME-20261018.md) covers a real enemy
death, replay, respawn, shooting afterward and normal exit in Factory Arena
Deathmatch. It does not establish every map, mode, or third-party combination.
Native Unity/EFT warnings remain documented there.

## Building and checks

Build from an SPT installation containing the game assemblies and client
libraries. The default layout is `<SPT>/Development/Killcam`; override `SPTPath`
when using another layout. The client targets .NET Standard 2.1; the offline
check projects require the **.NET 10 SDK**. Auto-deployment also requires
**PowerShell 7** (`pwsh`).

**A normal build deploys into the configured SPT installation.** For an offline
Release build from the repository root:

```powershell
dotnet build Killcam.csproj -c Release "-p:CustomAfterMicrosoftCommonTargets=$((Resolve-Path tools/NoDeploy.targets).Path)"
```

To build and deploy intentionally, close EFT and run:

```powershell
dotnet build Killcam.csproj -c Release
```

`tools/Deploy-Killcam.ps1` refuses locked files and archives existing Killcam and
KillcamReplay DLLs/symbols under `BepInEx/killcam-backups`, outside plugin
discovery. Debug builds also copy the PDB. The script does not stop the game.

Offline checks (no deployment):

```powershell
pwsh -NoProfile -File tools/RenameChecks/Verify.ps1
dotnet run --project tools/ConfigChecks -c Release
dotnet run --project tools/HandoffChecks -c Release
```

The rename checks need the SPT installation (`-SptPath` can override its default).
The config checks run the production bindings with the installed BepInEx and
ConfigurationManager libraries: fresh installs, old-key reset, preserved
preferences, explicit new bindings and readable/advanced menu metadata. They
write only temporary fixture configs; use `-p:SptPath=<path>` to override the
installation root.
The handoff checks also inspect a built Redeploy.Core library beside the repo
or in the installation. Missing prerequisites produce `HANDOFF_CHECKS_PARTIAL`
and exit 3, not a full pass. Offline checks are separate from in-game acceptance.

## Documentation and support

- [Technical implementation and camera/respawn ownership](docs/IMPLEMENTATION.md)
- [Rename, migration and native verification](docs/RENAME-20261018.md)
- [Logging levels and diagnostic limitations](docs/LOGGING-20261018.md)

Report issues through this repository's Issues page. Include the SPT/EFT and
Killcam versions, other camera/respawn mods, reproduction steps and the affected
BepInEx session log (normally `BepInEx/LogOutput.log`, or its rotated session
file). Remove credentials and personal identifiers before sharing logs or configs.
Routine technical details use Debug; replay start/end and failures remain visible.

## License

Released under the MIT License; see [LICENSE](LICENSE).
