using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Killcam.Replay;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Killcam.Patches;

public static class ReplayPatches
{
    public static void Enable()
    {
        new KillcamRaidStartPatch().Enable();
        new KillcamShotRecordPatch().Enable();
        new KillcamDeathReplayPatch().Enable();
        new KillcamDeferStopPatch().Enable();
        new KillcamSuppressDeathFadePatch().Enable();
        new KillcamFixPerfectCullingSamplerPatch().Enable();
    }

    // Arms the recorder at raid start (same hook KillAndDamageInfo uses for analytics).
    public class KillcamRaidStartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), nameof(LocalGame.vmethod_0));
        }

        [PatchPostfix]
        public static void Postfix()
        {
            Plugin.Recorder.ResetForNewRaid();
            KillcamDeferStopPatch.StopWasDeferred = false;
            KillcamDeferStopPatch.StopReinvokedAt = 0f;
            Plugin.ReplayCtrl.ResetHandoffForNewRaid();
            HoldRespawnPatch.TryApply();
        }
    }

    // Timestamps every shot so the replay overlay can flash a marker.
    public class KillcamShotRecordPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot));
        }

        [PatchPostfix]
        public static void Postfix(Player __instance)
        {
            try
            {
                Plugin.Recorder.RecordShot(__instance);
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Logger.LogError($"ShotRecord failed: {ex}");
            }
        }
    }

    public class KillcamDeathReplayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.OnDead));
        }

        [PatchPostfix]
        public static void Postfix(Player __instance, IPlayer ___LastAggressor)
        {
            try
            {
                if (!__instance.IsYourPlayer) return;

                // IsYourPlayer stays true on every body the local profile ever had. Only the
                // current MainPlayer is the local player's death; a body retired by a live
                // swap (ArenaMode kit swap) that dies later is not.
                if (!Singleton<GameWorld>.Instantiated
                    || !ReferenceEquals(Singleton<GameWorld>.Instance.MainPlayer, __instance)) return;

                if (!Plugin.DeathReplayActive) return;

                // Most arena modes respawn instantly, and a replay would play over
                // the live respawned player — those ask for zero. A mode that
                // makes the player wait out the round asks for a short one
                // instead (see ArenaCompat).
                if (ArenaCompat.IsArenaActive() && ArenaCompat.ArenaReplaySeconds() <= 0f) return;

                var killer = ___LastAggressor as Player;
                if (killer == null || killer.gameObject == null) return;

                if (!Plugin.Recorder.TryGetTrack(killer, out var track))
                {
                    Plugin.Instance.Logger.LogWarning("No track recorded for killer — skipping replay");
                    return;
                }

                float now = Time.time;
                float from = now - ReplayController.EffectiveDuration;
                var path = track.ExtractWindow(from, now);
                if (path.Length < 2)
                {
                    Plugin.Instance.Logger.LogWarning($"Killer track too short ({path.Length} snapshots) — skipping replay");
                    return;
                }
                var shots = track.ExtractShots(from, now);

                // Own recorded path → victim ghost in the replay; bone window → real-model puppet.
                Killcam.Recording.Snapshot[] victimPath = null;
                System.Collections.Generic.List<float> victimShots = null;
                if (Plugin.Recorder.TryGetTrack(__instance, out var victimTrack))
                {
                    victimPath = victimTrack.ExtractWindow(from, now);
                    victimShots = victimTrack.ExtractShots(from, now);
                }
                var boneWindow = Plugin.Recorder.Bones.ExtractWindow(from, now);

                // RespawnApi v1: true = a respawn driver holds this death open, false = Core will
                // not replace this body, null = unknown (no Core, older Core, Fika). Logged only
                // for now; the replay runs the same either way (the Stop hand-off below covers a
                // Killed Stop that may still follow, and a replacement ends playback itself).
                bool? respawnPending = Plugin.RespawnTracker?.IsRespawnPending(__instance);

                Plugin.Instance.Logger.LogInfo(
                    $"Death replay: killer={killer.Profile?.Nickname}, {path.Length} snapshots, window [{path[0].Time:F2} .. {path[path.Length - 1].Time:F2}], death at {now:F2}, respawnPending={(respawnPending.HasValue ? respawnPending.Value.ToString() : "unknown")}");

                Plugin.ReplayCtrl.StopPlayback("superseded by death replay");

                // must happen AFTER StopPlayback (which marks ready) and BEFORE the
                // game's death chain calls Stop — the deferred Stop waits on this
                Plugin.ReplayCtrl.ArmStopHandoff();

                ReplayController.PendingReplayKiller = killer;
                Plugin.Instance.StartCoroutine(
                    Plugin.ReplayCtrl.RunDeathReplay(__instance, path, victimPath, boneWindow, shots, victimShots, killer.Profile?.Nickname ?? "?"));
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Logger.LogError($"DeathReplay failed: {ex}");
                ReplayController.PendingReplayKiller = null;
            }
        }
    }

    // Defers the entire LocalGame.Stop() call while the replay owns the raid-end
    // sequence — same proven pattern as the KillAndDamageInfo killcam. Stop()
    // immediately shows a black overlay and destroys game state otherwise.
    public class KillcamDeferStopPatch : ModulePatch
    {
        private static bool _isReentry;

        // Observed by the replay's ending sequence: when a deferred Stop owns the
        // raid end, the final fade holds black until the re-invoked Stop's own
        // black screen has taken over, so the corpse cam never flashes in between.
        // When no Stop was deferred (e.g. a respawn mod intercepted the death),
        // the replay hands the camera back immediately instead.
        public static bool StopWasDeferred;
        public static float StopReinvokedAt;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), nameof(LocalGame.Stop));
        }

        [PatchPrefix]
        public static bool Prefix(LocalGame __instance, string profileId, ExitStatus exitStatus, string exitName, float delay)
        {
            if (_isReentry || exitStatus != ExitStatus.Killed || ReplayController.PendingReplayKiller == null)
                return true;

            Plugin.Instance.Logger.LogInfo("Deferring LocalGame.Stop until replay hand-off");
            StopWasDeferred = true;
            StopReinvokedAt = 0f;
            Plugin.Instance.StartCoroutine(DeferredStop(__instance, profileId, exitStatus, exitName, delay));
            return false;
        }

        // Waits for the replay's actual hand-off signal instead of a fixed delay:
        // a fixed wait measured the full sequence from the moment Stop was called,
        // which over-waits whenever the recorded path is shorter than the
        // configured duration — and by ~10s when Stop arrives mid-replay (e.g. a
        // Redeploy give-up), leaving the player staring at the corpse cam.
        // GetExtraDelay only serves as a safety cap now.
        private static IEnumerator DeferredStop(LocalGame game, string profileId, ExitStatus exitStatus, string exitName, float delay)
        {
            // ReadyForStop is the single wait condition: IsPlaying is false during the
            // LeadIn wait before playback starts, so it must not gate the loop.
            float deadline = Time.time + ReplayController.GetExtraDelay() + 5f;
            var replay = Plugin.ReplayCtrl;
            while (Time.time < deadline && replay != null && !replay.ReadyForStop)
                yield return null;

            Plugin.Instance.Logger.LogInfo("Re-invoking deferred LocalGame.Stop");
            StopReinvokedAt = Time.time;
            _isReentry = true;
            game.Stop(profileId, exitStatus, exitName, delay);
            _isReentry = false;
        }
    }

    // EffectsController.method_9 starts the death fade (DeathFade + FastBlur.Die)
    // when the local player is killed, which blacks out the screen while the replay
    // plays underneath it. It runs after our OnDead postfix has set
    // PendingReplayKiller, so gating on that flag skips the fade exactly when a
    // death replay owns the screen. The deferred raid-end flow brings its own
    // black-screen transition later.
    public class KillcamSuppressDeathFadePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EffectsController), "OnHealthDied");
        }

        [PatchPrefix]
        public static bool Prefix()
        {
            if (ReplayController.PendingReplayKiller == null)
                return true;

            Plugin.Instance.Logger.LogDebug("Suppressing death fade during replay");
            return false;
        }
    }

    // The PlayerCameraController Destroy/Create cycle leaves a stale PerfectCulling
    // sampler behind; a second sampler starting during Stop() cleanup throws
    // PerfectCullingException (4.0: GException8). DestroyImmediate the stale ones. Idempotent alongside the same
    // patch in KillAndDamageInfo and Redeploy.Core.
    // The stale ones come from the samplers seen starting plus the sampler's static Instance, not from
    // FindObjectsOfType: that walked the whole scene on every respawn camera and grew with every corpse
    // a long raid keeps (Redeploy docs/RESPAWN-API.md, "Messraid 17.09.2026").
    public class KillcamFixPerfectCullingSamplerPatch : ModulePatch
    {
        private static System.Type _samplerType;
        private static PropertyInfo _instanceProperty;

        private static readonly StaleSamplerRegistry Started =
            new StaleSamplerRegistry(sampler => sampler is Object unity && unity != null);

        protected override MethodBase GetTargetMethod()
        {
            _samplerType = AccessTools.TypeByName("Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneSampler");
            if (_samplerType == null)
            {
                Plugin.Instance.Logger.LogWarning("PerfectCullingCrossSceneSampler type not found — sampler patch skipped");
                return null;
            }

            _instanceProperty = AccessTools.Property(_samplerType, "Instance");
            return AccessTools.Method(_samplerType, "Start");
        }

        [PatchPrefix]
        public static void Prefix(Object __instance)
        {
            if (_samplerType == null) return;

            try
            {
                var current = _instanceProperty?.GetValue(null, null);
                foreach (Object stale in Started.TakeStale(__instance, current))
                    Object.DestroyImmediate(stale);
            }
            catch (System.Exception ex)
            {
                // Never cost the sampler its Start: without it the map is not culled at all.
                Plugin.Instance.Logger.LogWarning($"Stale PerfectCulling sampler check failed: {ex.Message}");
            }
        }
    }
}
