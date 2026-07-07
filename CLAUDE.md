# CLAUDE.md

## Build & Deploy

```bash
cd C:\SPT\Development\KillcamReplay
dotnet build -c Debug
```

Post-build kopiert DLL + PDB automatisch nach `C:\SPT\BepInEx\plugins\`.

## Projekt

BepInEx 5.x / HarmonyX Client-Mod für SPT: Killcam-**Replay** aus Sicht des Killers
(aufgezeichneter Kamera-Pfad, kein Live-Blick). Prototyp — siehe README.md für
Architektur, Performance-Budget und bekannte Einschränkungen.

- `src/Recording/SnapshotRecorder.cs` — Ringpuffer-Aufzeichnung (20 Hz, allokationsfrei im Hot Path)
- `src/Replay/ReplayController.cs` — Kamera-Pfad-Playback in LateUpdate + IMGUI-Overlay + F9-Debug-Replay
- `src/Patches/ReplayPatches.cs` — Raid-Start (vmethod_0), OnMakingShot, OnDead, DeferStop, PerfectCulling-Fix

## Wichtige Muster

Dieselben Harmony-Pitfalls wie in `C:\SPT\Development\KillAndDamageInfo\CLAUDE.md`
(dekompilierte Quelle: `C:\SPT\Development\decompiled\Assembly-CSharp`). Insbesondere:

- **Deferred Stop**: `LocalGame.Stop()` per Prefix (return false) aufschieben, Re-Invoke
  mit `_isReentry`-Flag nach dem Replay. Sonst schwarzer Screen + State-Zerstörung.
- **PlayerCameraController Destroy/Create**: `yield return null` dazwischen, sonst
  GException8 (PerfectCulling-Sampler). Der Sampler-Fix-Patch ist idempotent zu dem in
  KillAndDamageInfo.
- **Koexistenz**: Death-Replay deaktiviert sich automatisch, wenn
  `com.local.killanddamageinfo` geladen ist (dessen Live-Killcam besitzt die Todessequenz).
- Postfixes in Gameplay-Pfaden (OnDead, OnMakingShot) immer in try/catch.
