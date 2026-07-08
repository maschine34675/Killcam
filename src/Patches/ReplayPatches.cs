using System.Collections;
using System.Reflection;
using EFT;
using HarmonyLib;
using KillcamReplay.Replay;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KillcamReplay.Patches;

public static class ReplayPatches
{
    public static void Enable()
    {
        new RaidStartPatch().Enable();
        new ShotRecordPatch().Enable();
        new DeathReplayPatch().Enable();
        new DeferStopPatch().Enable();
        new SuppressDeathFadePatch().Enable();
        new FixPerfectCullingSamplerPatch().Enable();
    }

    // Arms the recorder at raid start (same hook KillAndDamageInfo uses for analytics).
    public class RaidStartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), nameof(LocalGame.vmethod_0));
        }

        [PatchPostfix]
        public static void Postfix()
        {
            Plugin.Recorder.ResetForNewRaid();
        }
    }

    // Timestamps every shot so the replay overlay can flash a marker.
    public class ShotRecordPatch : ModulePatch
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
                Plugin.Instance.Logger.LogError($"[KillcamReplay] ShotRecord failed: {ex}");
            }
        }
    }

    public class DeathReplayPatch : ModulePatch
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

                Plugin.Recorder.StopRecording();

                if (!Plugin.DeathReplayActive) return;

                var killer = ___LastAggressor as Player;
                if (killer == null || killer.gameObject == null) return;

                if (!Plugin.Recorder.TryGetTrack(killer, out var track))
                {
                    Plugin.Instance.Logger.LogWarning("[KillcamReplay] No track recorded for killer — skipping replay");
                    return;
                }

                float now = Time.time;
                var path = track.ExtractWindow(now - Plugin.ReplayDuration.Value, now);
                if (path.Length < 2)
                {
                    Plugin.Instance.Logger.LogWarning($"[KillcamReplay] Killer track too short ({path.Length} snapshots) — skipping replay");
                    return;
                }
                var shots = track.ExtractShots(now - Plugin.ReplayDuration.Value, now);
                Plugin.Instance.Logger.LogInfo(
                    $"[KillcamReplay] Death replay: killer={killer.Profile?.Nickname}, {path.Length} snapshots, window [{path[0].Time:F2} .. {path[path.Length - 1].Time:F2}], death at {now:F2}");

                Plugin.ReplayCtrl.StopPlayback("superseded by death replay");

                ReplayController.PendingReplayKiller = killer;
                Plugin.Instance.StartCoroutine(
                    Plugin.ReplayCtrl.RunDeathReplay(__instance, path, shots, killer.Profile?.Nickname ?? "?"));
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Logger.LogError($"[KillcamReplay] DeathReplay failed: {ex}");
                ReplayController.PendingReplayKiller = null;
            }
        }
    }

    // Defers the entire LocalGame.Stop() call while the replay owns the raid-end
    // sequence — same proven pattern as the KillAndDamageInfo killcam. Stop()
    // immediately shows a black overlay and destroys game state otherwise.
    public class DeferStopPatch : ModulePatch
    {
        private static bool _isReentry;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), nameof(LocalGame.Stop));
        }

        [PatchPrefix]
        public static bool Prefix(LocalGame __instance, string profileId, ExitStatus exitStatus, string exitName, float delay)
        {
            if (_isReentry || exitStatus != ExitStatus.Killed || ReplayController.PendingReplayKiller == null)
                return true;

            Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Deferring LocalGame.Stop by {ReplayController.GetExtraDelay():F1}s");
            Plugin.Instance.StartCoroutine(DeferredStop(__instance, profileId, exitStatus, exitName, delay));
            return false;
        }

        private static IEnumerator DeferredStop(LocalGame game, string profileId, ExitStatus exitStatus, string exitName, float delay)
        {
            yield return new WaitForSeconds(ReplayController.GetExtraDelay());
            Plugin.Instance.Logger.LogInfo("[KillcamReplay] Re-invoking deferred LocalGame.Stop");
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
    public class SuppressDeathFadePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EffectsController), "method_9");
        }

        [PatchPrefix]
        public static bool Prefix()
        {
            if (ReplayController.PendingReplayKiller == null)
                return true;

            Plugin.Instance.Logger.LogInfo("[KillcamReplay] Suppressing death fade during replay");
            return false;
        }
    }

    // The PlayerCameraController Destroy/Create cycle leaves a stale PerfectCulling
    // sampler behind; a second sampler starting during Stop() cleanup throws
    // GException8. DestroyImmediate the stale ones. Idempotent alongside the same
    // patch in KillAndDamageInfo.
    public class FixPerfectCullingSamplerPatch : ModulePatch
    {
        private static System.Type _samplerType;

        protected override MethodBase GetTargetMethod()
        {
            _samplerType = AccessTools.TypeByName("Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneSampler");
            if (_samplerType == null)
            {
                Plugin.Instance.Logger.LogWarning("[KillcamReplay] PerfectCullingCrossSceneSampler type not found — sampler patch skipped");
                return null;
            }

            return AccessTools.Method(_samplerType, "Start");
        }

        [PatchPrefix]
        public static void Prefix(Object __instance)
        {
            if (_samplerType == null) return;

            var existing = Object.FindObjectsOfType(_samplerType);
            foreach (var obj in existing)
            {
                if (obj != __instance)
                    Object.DestroyImmediate(obj);
            }
        }
    }
}
