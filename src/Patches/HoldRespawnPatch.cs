using System;
using System.Collections;
using System.Reflection;
using EFT;
using HarmonyLib;
using Killcam.Replay;
using UnityEngine;

namespace Killcam.Patches;

// Holds a respawn of the death replay's victim until the replay has handed the screen
// over, the respawn counterpart of DeferStopPatch. Redeploy.Core runs every respawn
// through RespawnEngine.RunRespawn(RespawnRequest, Action<Exception>), consumed with
// StartCoroutine; a postfix swaps the returned coroutine for one that first waits and
// then runs the original step by step. Nothing in the pipeline has started while it
// waits, so Core's own preflight still decides at the moment the pipeline really starts.
//
// No compile dependency on Core: the method and the two request fields are resolved by
// name. Both compiled drivers (Redeploy.Client, ArenaMode.Client) reference exactly these
// members, and Redeploy's contract tests fail if an installed driver stops resolving them,
// so a rename cannot slip through there unnoticed. If they are missing here anyway, the
// hold stays off and a respawn cuts the replay as before.
//
// Not in ArenaMode raids: ArenaSession.ReplaySeconds is Arena's contract for how long a
// replay may take, and Arena's round flow owns its respawn timing.
public static class HoldRespawnPatch
{
    private const string EngineTypeName = "Redeploy.Core.Engine.RespawnEngine, maschine-Redeploy.Core";
    private const string HarmonyId = "com.maschine.Killcam.HoldRespawn";

    private static bool _applied;
    private static bool _contractWarned;
    private static FieldInfo _gameField;
    private static FieldInfo _deadPlayerField;

    /// <summary>Patches Core once it can be resolved. Cheap to call again (Awake, every raid start).</summary>
    public static void TryApply()
    {
        if (_applied) return;

        try
        {
            var engine = Type.GetType(EngineTypeName, false);
            if (engine == null) return;   // no Core installed: nothing respawns the player, nothing to hold

            var run = AccessTools.Method(engine, "RunRespawn");
            var parameters = run?.GetParameters();
            if (run == null || run.ReturnType != typeof(IEnumerator) || parameters.Length != 2)
            {
                WarnContract("RespawnEngine.RunRespawn(RespawnRequest, Action<Exception>) returning IEnumerator not found");
                return;
            }

            var requestType = parameters[0].ParameterType;
            _gameField = AccessTools.Field(requestType, "Game");
            _deadPlayerField = AccessTools.Field(requestType, "DeadPlayer");
            if (_gameField == null || !typeof(LocalGame).IsAssignableFrom(_gameField.FieldType)
                || _deadPlayerField == null || !typeof(Player).IsAssignableFrom(_deadPlayerField.FieldType))
            {
                WarnContract($"{requestType.Name}.Game / .DeadPlayer not found");
                return;
            }

            new Harmony(HarmonyId).Patch(run, postfix: new HarmonyMethod(typeof(HoldRespawnPatch), nameof(Postfix)));
            _applied = true;
            Plugin.Instance.Logger.LogInfo("Respawn hold armed: a respawn during the death replay waits for the replay (Redeploy.Core RunRespawn)");
        }
        catch (Exception ex)
        {
            WarnContract(ex.Message);
        }
    }

    private static void WarnContract(string what)
    {
        if (_contractWarned) return;
        _contractWarned = true;
        Plugin.Instance.Logger.LogWarning($"Respawn hold unavailable ({what}) — a respawn cuts the replay as before");
    }

    private static void Postfix(ref IEnumerator __result, object[] __args)
    {
        try
        {
            if (__result == null || !Plugin.WaitForReplayBeforeRespawn.Value) return;
            if (ArenaCompat.IsArenaActive()) return;

            var request = __args != null && __args.Length > 0 ? __args[0] : null;
            if (request == null) return;   // Core refuses it synchronously; nothing to wait for

            var game = _gameField.GetValue(request) as LocalGame;
            var body = (_deadPlayerField.GetValue(request) as Player) ?? game?.LocalPlayer;
            var replay = Plugin.ReplayCtrl;
            if (game == null || body == null || replay == null || !replay.HoldReplacementOf(body)) return;

            __result = HoldThenRun(__result, game, body);
        }
        catch (Exception ex)
        {
            // Never break a respawn over the hold: fall back to the unwrapped pipeline.
            Plugin.Instance.Logger.LogError($"Respawn hold failed, respawning without waiting: {ex}");
        }
    }

    private static IEnumerator HoldThenRun(IEnumerator pipeline, LocalGame game, Player body)
    {
        float started = Time.time;
        // Same bound as the deferred Stop: the longest the replay can still take, plus slack.
        float cap = ReplayController.GetExtraDelay() + 5f;
        string reason;

        Plugin.Instance.Logger.LogInfo("Holding the respawn until the death replay hands off");

        while (true)
        {
            var replay = Plugin.ReplayCtrl;
            if (replay == null || !replay.HoldReplacementOf(body)) { reason = "replay handed off"; break; }
            if (RaidIsTearingDown(game)) { reason = "raid is ending"; replay.AbandonHeldReplacement(); break; }
            if (Time.time - started >= cap) { reason = "hold timeout"; replay.AbandonHeldReplacement(); break; }
            yield return null;
        }

        Plugin.Instance.Logger.LogInfo($"Releasing the held respawn after {Time.time - started:F1}s ({reason})");

        // While the pipeline runs, the replay's black hold does not give up on it.
        Plugin.ReplayCtrl?.SetReplacementRunning(true);
        bool more;
        do
        {
            try { more = pipeline.MoveNext(); }
            catch
            {
                Plugin.ReplayCtrl?.SetReplacementRunning(false);
                throw;
            }

            if (!more)
                break;
            yield return pipeline.Current;
        }
        while (true);
        Plugin.ReplayCtrl?.SetReplacementRunning(false);
    }

    private static bool RaidIsTearingDown(LocalGame game)
        => game == null
           || game.Status == GameStatus.Stopping
           || game.Status == GameStatus.SoftStopping
           || game.Status == GameStatus.Stopped;
}
