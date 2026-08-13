using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using KillcamReplay.Patches;
using KillcamReplay.Recording;
using UnityEngine;

namespace KillcamReplay.Replay;

// Plays a recorded camera path on the main camera and moves a translucent ghost
// along the victim's recorded path. While a death replay owns the raid-end
// sequence, PendingReplayKiller is non-null and LocalGame.Stop() is deferred
// (see ReplayPatches.DeferStopPatch).
//
// No PlayerCameraController is destroyed or created: destroying it detaches the
// EffectsController from the player (its death handler then NullRefs and aborts
// the raid-end flow → soft-lock). Instead the pose is re-applied in
// Application.onBeforeRender, which runs after every LateUpdate and therefore
// wins the rendered frame against the live/corpse camera controller.
public class ReplayController : MonoBehaviour
{
    public const float LeadIn = 0.8f;
    public const float TailBuffer = 0.7f;
    public const float EndFadeDuration = 0.2f;

    public static Player PendingReplayKiller;

    private static readonly System.Reflection.FieldInfo _corpseField =
        HarmonyLib.AccessTools.Field(typeof(Player), "Corpse");

    private PathCursor _cameraPath;
    private PathCursor _victimPath;
    private ReplayGhost _ghost;
    private GhostPuppet _puppet;
    private readonly LivePlayerHider _hider = new();
    private enum EndingPhase
    {
        FadeOutCut,  // fade to black over the frozen final replay pose
        FadeInLive,  // scene swapped to live under black, fading back in
        LiveHold,    // live view following the killer (or frozen pose as fallback)
        FinalFade,   // fade to black before handing the camera back
        HoldBlack    // stay black until the re-invoked Stop's own black screen is up
    }

    private List<float> _shots;
    private List<float> _victimShots;
    private ShotFlash _killerFlash;
    private ShotFlash _ghostFlash;
    private WeaponGhost _killerWeapon;
    private float _nextHiderRefresh;
    private float _clock;
    private float _startTime;
    private bool _playing;
    private bool _withLiveTail;
    private bool _inEnding;
    private bool _cutToLive;
    private EndingPhase _endPhase;
    private float _phaseClock;
    private string _killerName;
    private Camera _camera;
    private GUIStyle _labelStyle;
    private GUIStyle _shotStyle;
    private GUIStyle _timeStyle;
    private GUIStyle _liveStyle;

    public bool IsPlaying => _playing;

    public static float GetExtraDelay()
    {
        if (PendingReplayKiller == null) return 0f;

        // slow motion stretches the last SlowMoSeconds of recorded time by 1/factor
        // in wall-clock time; upper bound (the ease ramp makes it slightly less)
        float slowMoExtra = Plugin.SlowMoSeconds.Value > 0f
            ? Plugin.SlowMoSeconds.Value * (1f / Plugin.SlowMoFactor.Value - 1f)
            : 0f;

        return LeadIn + Plugin.ReplayDuration.Value + slowMoExtra
            + 2f * EndFadeDuration + Plugin.LiveTailSeconds.Value + TailBuffer;
    }

    private void Update()
    {
        if (Plugin.DebugReplayKey.Value.IsDown())
            TryStartDebugReplay();
    }

    private void Play(Snapshot[] killerPath, Snapshot[] victimPath, BoneWindow boneWindow,
        GameObject puppetSource, Player puppetSourcePlayer, Player observedPlayer,
        List<float> shots, List<float> victimShots, string killerName, bool withLiveTail)
    {
        _cameraPath = new PathCursor(killerPath);
        _victimPath = victimPath != null && victimPath.Length >= 2 && Plugin.ShowVictimGhost.Value
            ? new PathCursor(victimPath)
            : null;
        _shots = shots;
        _victimShots = victimShots;
        _killerName = killerName;
        _clock = 0f;
        _withLiveTail = withLiveTail;
        _inEnding = false;
        _cutToLive = false;
        _endPhase = EndingPhase.FadeOutCut;
        _phaseClock = 0f;
        _startTime = _cameraPath.StartTime;
        _camera = CameraManager.Instance?.Camera;
        _playing = _camera != null && killerPath.Length >= 2;

        Plugin.Instance.Logger.LogInfo(
            $"[KillcamReplay] Playback start: {killerPath.Length} snapshots, span {_cameraPath.EndTime - _cameraPath.StartTime:F2}s, bones={(boneWindow != null ? boneWindow.Times.Length.ToString() : "none")}, camera={(_camera != null ? "ok" : "NULL")}");

        if (_playing)
        {
            if (Plugin.ShowVictimGhost.Value)
            {
                if (Plugin.UseRealModelGhost.Value && boneWindow != null && puppetSource != null)
                    _puppet = GhostPuppet.TryCreate(puppetSource, puppetSourcePlayer, boneWindow);

                if (_puppet == null && _victimPath != null)
                    _ghost = new ReplayGhost();
            }

            // must run BEFORE HideAll — the mesh copy skips disabled renderers
            if (Plugin.ShowKillerWeapon.Value && observedPlayer != null)
                _killerWeapon = WeaponGhost.TryCreate(observedPlayer);

            if (Plugin.HideLiveBots.Value)
                _hider.HideAll();

            if (Plugin.ShowMuzzleFlashes.Value)
            {
                _killerFlash = new ShotFlash();
                _ghostFlash = new ShotFlash();
            }

            Application.onBeforeRender -= ApplyCurrentPose;
            Application.onBeforeRender += ApplyCurrentPose;
        }
    }

    private void EndPlayback(string reason)
    {
        _playing = false;
        _inEnding = false;
        _cutToLive = false;
        Application.onBeforeRender -= ApplyCurrentPose;
        _ghost?.Destroy();
        _ghost = null;
        _puppet?.Destroy();
        _puppet = null;
        _killerFlash?.Destroy();
        _killerFlash = null;
        _ghostFlash?.Destroy();
        _ghostFlash = null;
        _killerWeapon?.Destroy();
        _killerWeapon = null;
        _hider.RestoreAll();
        Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Playback end after {_clock:F2}s: {reason}");
    }

    public void StopPlayback(string reason)
    {
        if (_playing)
            EndPlayback(reason);
    }

    private void LateUpdate()
    {
        if (!_playing) return;

        if (_camera == null)
        {
            EndPlayback("camera destroyed");
            return;
        }

        // reloading/weapon swaps spawn new view objects on hidden bots mid-replay
        // (floating magazines) — sweep them periodically
        if (Plugin.HideLiveBots.Value && Time.time >= _nextHiderRefresh)
        {
            _nextHiderRefresh = Time.time + 0.5f;
            _hider.RefreshHidden();
        }

        if (_inEnding)
        {
            UpdateEnding();
            return;
        }

        _clock += Time.deltaTime * CurrentPlaybackSpeed();

        if (_startTime + _clock >= _cameraPath.EndTime)
        {
            ApplyCurrentPose();
            if (_withLiveTail)
            {
                _inEnding = true;
                _endPhase = EndingPhase.FadeOutCut;
                _phaseClock = 0f;
            }
            else
            {
                EndPlayback("path exhausted");
            }
            return;
        }

        ApplyCurrentPose();
    }

    // Slow motion for the final moments before the kill: the playback clock simply
    // advances slower — no Time.timeScale involved, the live world keeps running
    // normally. Eases into the slow factor over the first 0.4s of the window.
    private float CurrentPlaybackSpeed()
    {
        float slowSeconds = Plugin.SlowMoSeconds.Value;
        if (slowSeconds <= 0f) return 1f;

        float remaining = _cameraPath.EndTime - (_startTime + _clock);
        if (remaining > slowSeconds) return 1f;

        float ease = Mathf.Clamp01((slowSeconds - remaining) / 0.4f);
        return Mathf.Lerp(1f, Plugin.SlowMoFactor.Value, ease);
    }

    // Ending sequence once the recorded path is exhausted (the kill moment): fade
    // to black (~0.2s), swap the replay scene for the live world under full black
    // (puppet gone, corpse and hidden bots restored — except the killer, whose live
    // eyes the camera follows from here), fade back in, hold the live view for a
    // few seconds, then fade to black again and stay black until the re-invoked
    // Stop's own black screen has taken over — so the corpse cam never flashes
    // between the replay and the raid-end transition.
    private void UpdateEnding()
    {
        _phaseClock += Time.deltaTime;

        switch (_endPhase)
        {
            case EndingPhase.FadeOutCut:
                if (_phaseClock >= EndFadeDuration)
                {
                    CutToLive();
                    _endPhase = EndingPhase.FadeInLive;
                    _phaseClock = 0f;
                }
                break;

            case EndingPhase.FadeInLive:
                if (_phaseClock >= EndFadeDuration)
                {
                    _endPhase = EndingPhase.LiveHold;
                    _phaseClock = 0f;
                }
                break;

            case EndingPhase.LiveHold:
                if (_phaseClock >= Plugin.LiveTailSeconds.Value)
                {
                    if (ReplayPatches.DeferStopPatch.StopWasDeferred)
                    {
                        _endPhase = EndingPhase.FinalFade;
                        _phaseClock = 0f;
                    }
                    else
                    {
                        // no deferred Stop owns the raid end (e.g. a respawn mod
                        // intercepted the death) — hand the camera back directly
                        EndPlayback("live tail complete");
                        return;
                    }
                }
                break;

            case EndingPhase.FinalFade:
                if (_phaseClock >= EndFadeDuration)
                {
                    _endPhase = EndingPhase.HoldBlack;
                    _phaseClock = 0f;
                }
                break;

            case EndingPhase.HoldBlack:
                // 1.5s after the re-invoke: the game's own black screen fades in and
                // was empirically not fully opaque yet at 0.8s (brief corpse-cam flash)
                float reinvokedAt = ReplayPatches.DeferStopPatch.StopReinvokedAt;
                bool gameBlackUp = reinvokedAt > 0f && Time.time - reinvokedAt >= 1.5f;
                if (gameBlackUp || _phaseClock >= 5f)
                {
                    EndPlayback(gameBlackUp ? "handed off under black screen" : "black hold timeout");
                    return;
                }
                break;
        }

        ApplyCurrentPose();
    }

    private void CutToLive()
    {
        _cutToLive = true;
        _ghost?.Destroy();
        _ghost = null;
        _puppet?.Destroy();
        _puppet = null;

        // Keep the killer hidden while the camera follows his live eyes — his own
        // model would clip into the view. His HELD WEAPON is shown again though:
        // riding his live hands it reads like a natural first-person view. He fully
        // reappears at EndPlayback's RestoreAll.
        if (PendingReplayKiller != null && Plugin.FollowKillerInTail.Value)
        {
            _hider.RestoreAllExcept(PendingReplayKiller);
            _hider.ShowHeldWeapon(PendingReplayKiller);
        }
        else
        {
            _hider.RestoreAll();
        }
    }

    private void ApplyCurrentPose()
    {
        if (!_playing || _camera == null) return;

        var tr = _camera.transform;

        // After the cut, the camera follows the killer's LIVE eyes; the frozen
        // final replay pose is the fallback if he died or despawned meanwhile.
        if (_cutToLive && TryGetLiveKillerPose(out var livePos, out var liveRot))
        {
            tr.position = livePos;
            tr.rotation = liveRot;
            _killerWeapon?.Hide();
            return;
        }

        float t = Mathf.Min(_startTime + _clock, _cameraPath.EndTime);

        var camSnap = _cameraPath.Sample(t);
        float pitch = Plugin.InvertPitch.Value ? -camSnap.Pitch : camSnap.Pitch;
        tr.position = camSnap.CamPos;
        tr.rotation = Quaternion.Euler(pitch, camSnap.Yaw, 0f);

        if (_killerWeapon != null)
        {
            if (camSnap.WeaponValid && !_cutToLive)
                _killerWeapon.SetPose(camSnap.WeaponPos, camSnap.WeaponRot);
            else
                _killerWeapon.Hide();
        }

        if (_puppet != null)
        {
            _puppet.Apply(t);
        }
        else if (_ghost != null && _victimPath != null)
        {
            var victimSnap = _victimPath.Sample(t);
            if (Plugin.InvertPitch.Value)
                victimSnap.Pitch = -victimSnap.Pitch;
            _ghost.UpdatePose(victimSnap);
        }

        UpdateShotFlashes(t);
    }

    // Brief 3D flashes at recorded shot times: the killer's shots light up just in
    // front of the camera (his eyes ARE the camera, his weapon isn't rendered), the
    // victim's own shots flash at the puppet weapon's estimated muzzle.
    private void UpdateShotFlashes(float t)
    {
        if (_killerFlash == null) return;

        if (_inEnding)
        {
            _killerFlash.Hide();
            _ghostFlash.Hide();
            return;
        }

        if (AnyShotActive(_shots, t))
        {
            // prefer the actual muzzle of the killer's weapon ghost if it's showing
            if (_killerWeapon != null && _killerWeapon.TryGetMuzzle(out var killerMuzzle, out var killerDir))
            {
                _killerFlash.ShowAt(killerMuzzle, killerDir);
            }
            else
            {
                var tr = _camera.transform;
                _killerFlash.ShowAt(tr.position + tr.forward * 0.55f - tr.up * 0.22f, tr.rotation);
            }
        }
        else
        {
            _killerFlash.Hide();
        }

        bool ghostShot = AnyShotActive(_victimShots, t);
        if (ghostShot && _puppet != null && _puppet.TryGetMuzzle(out var muzzle, out var muzzleRot))
        {
            _ghostFlash.ShowAt(muzzle, muzzleRot);
        }
        else if (ghostShot && _ghost != null && _victimPath != null)
        {
            var s = _victimPath.Sample(t);
            float ghostPitch = Plugin.InvertPitch.Value ? -s.Pitch : s.Pitch;
            var viewRot = Quaternion.Euler(ghostPitch, s.Yaw, 0f);
            _ghostFlash.ShowAt(s.CamPos + viewRot * Vector3.forward * 0.6f, viewRot);
        }
        else
        {
            _ghostFlash.Hide();
        }
    }

    private static bool AnyShotActive(List<float> shots, float t)
    {
        if (shots == null) return false;
        for (int i = 0; i < shots.Count; i++)
        {
            float sinceShot = t - shots[i];
            if (sinceShot >= 0f && sinceShot < 0.07f) return true;
        }
        return false;
    }

    private bool TryGetLiveKillerPose(out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;
        if (!Plugin.FollowKillerInTail.Value) return false;

        var killer = PendingReplayKiller;
        if (killer == null || killer.gameObject == null) return false;
        if (killer.HealthController == null || !killer.HealthController.IsAlive) return false;

        var camPos = killer.CameraPosition;
        if (camPos == null) return false;

        Vector2 rotation = killer.Rotation;
        float pitch = Plugin.InvertPitch.Value ? -rotation.y : rotation.y;
        pos = camPos.position;
        rot = Quaternion.Euler(pitch, rotation.x, 0f);
        return true;
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= ApplyCurrentPose;
        _ghost?.Destroy();
        _puppet?.Destroy();
        _killerFlash?.Destroy();
        _ghostFlash?.Destroy();
        _killerWeapon?.Destroy();
        _hider.RestoreAll();
    }

    public IEnumerator RunDeathReplay(Player deadPlayer, Snapshot[] killerPath, Snapshot[] victimPath,
        BoneWindow boneWindow, List<float> shots, List<float> victimShots, string killerName)
    {
        yield return new WaitForSeconds(LeadIn);

        // The corpse IS the original dressed body (converted in place), so it is the
        // ideal puppet source. By now (LeadIn later) it exists. Player.Corpse is a
        // protected field — read via reflection.
        GameObject puppetSource = null;
        if (deadPlayer != null && deadPlayer.gameObject != null)
        {
            var corpse = _corpseField?.GetValue(deadPlayer) as Component;
            puppetSource = corpse != null ? corpse.gameObject : deadPlayer.gameObject;
        }

        Play(killerPath, victimPath, boneWindow, puppetSource, deadPlayer, PendingReplayKiller, shots, victimShots, killerName, withLiveTail: true);
        while (_playing)
            yield return null;

        PendingReplayKiller = null;
    }

    private void TryStartDebugReplay()
    {
        if (_playing || PendingReplayKiller != null) return;

        var gameWorld = Singleton<GameWorld>.Instance;
        var main = gameWorld?.MainPlayer;
        if (main == null || !main.HealthController.IsAlive) return;

        Player nearest = null;
        float bestDist = float.MaxValue;
        var players = gameWorld.AllAlivePlayersList;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p == null || p.IsYourPlayer) continue;
            float d = (p.Position - main.Position).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                nearest = p;
            }
        }
        if (nearest == null) return;

        if (!Plugin.Recorder.TryGetTrack(nearest, out var track))
        {
            Plugin.Instance.Logger.LogWarning("[KillcamReplay] Debug replay: no track for nearest bot");
            return;
        }

        float now = Time.time;
        float from = now - Plugin.ReplayDuration.Value;
        var path = track.ExtractWindow(from, now);
        if (path.Length < 2)
        {
            Plugin.Instance.Logger.LogWarning("[KillcamReplay] Debug replay: not enough snapshots");
            return;
        }
        var shots = track.ExtractShots(from, now);

        // The main player's own recent path doubles as the ghost — walk up to a bot,
        // press F9 and watch yourself through its eyes. The live body is the puppet
        // source here (no corpse exists); its own head stays hidden (FPS mode).
        Snapshot[] victimPath = null;
        List<float> victimShots = null;
        if (Plugin.Recorder.TryGetTrack(main, out var mainTrack))
        {
            victimPath = mainTrack.ExtractWindow(from, now);
            victimShots = mainTrack.ExtractShots(from, now);
        }
        var boneWindow = Plugin.Recorder.Bones.ExtractWindow(from, now);

        StartCoroutine(RunDebugReplay(main, nearest, path, victimPath, boneWindow, shots, victimShots, nearest.Profile?.Nickname ?? "Bot"));
    }

    private IEnumerator RunDebugReplay(Player main, Player observed, Snapshot[] path, Snapshot[] victimPath,
        BoneWindow boneWindow, List<float> shots, List<float> victimShots, string name)
    {
        // No live tail on the debug replay: the player is alive and holding their
        // view frozen for extra seconds would get them killed for a debug feature.
        Play(path, victimPath, boneWindow, main.gameObject, main, observed, shots, victimShots, name, withLiveTail: false);
        while (_playing)
            yield return null;
        // Nothing to restore — the player's camera controller was never touched and
        // reclaims the view as soon as the stomp stops.
    }

    private void OnGUI()
    {
        if (!_playing) return;

        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            _labelStyle.normal.textColor = new Color(1f, 0.9f, 0.6f);

            _shotStyle = new GUIStyle(_labelStyle) { fontSize = 16 };
            _shotStyle.normal.textColor = new Color(1f, 0.25f, 0.2f);

            _timeStyle = new GUIStyle(_labelStyle) { fontSize = 16, fontStyle = FontStyle.Normal };
            _timeStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            _liveStyle = new GUIStyle(_labelStyle) { fontSize = 16 };
            _liveStyle.normal.textColor = new Color(1f, 0.25f, 0.2f);
        }

        float baseY = Screen.height * Plugin.HudTopFraction.Value;
        GUI.Label(new Rect(0, baseY, Screen.width, 34), $"KILLCAM — {_killerName}", _labelStyle);

        // video-player style position: seconds until the kill moment, then LIVE
        if (_cutToLive)
        {
            GUI.Label(new Rect(0, baseY + 34, Screen.width, 24), "● LIVE", _liveStyle);
        }
        else
        {
            float rel = Mathf.Min(_startTime + _clock, _cameraPath.EndTime) - _cameraPath.EndTime;
            if (rel > -0.05f) rel = 0f;
            GUI.Label(new Rect(0, baseY + 34, Screen.width, 24), $"{rel:F1}s", _timeStyle);
        }

        if (!_inEnding)
        {
            float t = _startTime + _clock;
            for (int i = 0; i < _shots.Count; i++)
            {
                if (Mathf.Abs(_shots[i] - t) < 0.15f)
                {
                    GUI.Label(new Rect(0, baseY + 58, Screen.width, 24), "● SHOT", _shotStyle);
                    break;
                }
            }
        }
        else
        {
            float alpha = CurrentFadeAlpha();
            if (alpha > 0f)
            {
                var prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, alpha);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = prev;
            }
        }
    }

    private float CurrentFadeAlpha()
    {
        switch (_endPhase)
        {
            case EndingPhase.FadeOutCut: return Mathf.Clamp01(_phaseClock / EndFadeDuration);
            case EndingPhase.FadeInLive: return Mathf.Clamp01(1f - _phaseClock / EndFadeDuration);
            case EndingPhase.FinalFade: return Mathf.Clamp01(_phaseClock / EndFadeDuration);
            case EndingPhase.HoldBlack: return 1f;
            default: return 0f;
        }
    }
}
