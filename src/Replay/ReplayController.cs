using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using Killcam.Patches;
using Killcam.Recording;
using UnityEngine;

namespace Killcam.Replay;

// Plays a recorded camera path on the main camera and moves a translucent ghost
// along the victim's recorded path. While a death replay owns the raid-end
// sequence, PendingReplayKiller is non-null and LocalGame.Stop() is deferred
// (see ReplayPatches.KillcamDeferStopPatch).
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
    public const float RevealDuration = 0.35f;

    public static Player PendingReplayKiller;

    // A respawn of the death replay's victim waits for the replay (HoldRespawnPatch).
    private readonly RespawnHandoff _handoff = new();

    // Fade back in from black after a replacement took over under the replay's black
    // hold. -1 = no reveal; otherwise the frame EndPlayback ran in (the replacement's
    // own frame, usually a long one), and _revealStart the time the fade begins.
    private int _revealArmedFrame = -1;
    private float _revealStart = -1f;
    private int _deathReplayGeneration;

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
    private ShotTracers _killerTracers;
    private ShotTracers _ghostTracers;
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
    // The body this playback shows as the victim: the dead local player of a death
    // replay, the living one of the F9 replay. Its replacement ends the playback.
    private Player _victimBody;
    private GUIStyle _labelStyle;
    private GUIStyle _shotStyle;
    private GUIStyle _timeStyle;
    private GUIStyle _liveStyle;

    public bool IsPlaying => _playing;

    // Signals the deferred LocalGame.Stop that the replay is about to hand the
    // screen over (final fade started) — the Stop coroutine waits for this instead
    // of a fixed delay, so it adapts to the ACTUAL sequence length (shorter paths,
    // slow-mo stretch, or a Redeploy give-up arriving mid-replay). Armed (reset to
    // false) in the OnDead postfix BEFORE the game calls Stop; it still carries the
    // previous playback's true otherwise, and the death replay itself only starts
    // after the LeadIn wait.
    public bool ReadyForStop { get; private set; } = true;

    public void ArmStopHandoff()
    {
        ReadyForStop = false;
    }

    /// <summary>
    /// How far back this replay reaches, in seconds.
    ///
    /// One property rather than a read of the config at each site, because the
    /// two readers must agree: the death path cuts a window of this length, and
    /// GetExtraDelay bounds the deferred Stop's wait. That wait normally ends on
    /// ReadyForStop — GetExtraDelay is the cap for when that signal never
    /// arrives — so a cap computed from a different number than the window was
    /// cut with is a cap that can expire mid-playback.
    /// </summary>
    public static float EffectiveDuration
    {
        get
        {
            var arena = ArenaCompat.IsArenaActive() ? ArenaCompat.ArenaReplaySeconds() : 0f;
            return arena > 0f ? arena : Plugin.ReplayDuration.Value;
        }
    }

    // Only the post-replay live view is shorter in Arena. Use the same value
    // for the ending phase and its safety bound; never change the saved setting.
    public static float EffectiveLiveTailSeconds
        => Plugin.LiveTailSeconds.Value * (ArenaCompat.IsArenaActive() ? 0.5f : 1f);

    public static float GetExtraDelay()
    {
        if (PendingReplayKiller == null) return 0f;

        // slow motion stretches the last SlowMoSeconds of recorded time by 1/factor
        // in wall-clock time; upper bound (the ease ramp makes it slightly less)
        float slowMoExtra = Plugin.SlowMoSeconds.Value > 0f
            ? Plugin.SlowMoSeconds.Value * (1f / Plugin.SlowMoFactor.Value - 1f)
            : 0f;

        return LeadIn + EffectiveDuration + slowMoExtra
            + 2f * EndFadeDuration + EffectiveLiveTailSeconds + TailBuffer;
    }

    private void Update()
    {
        if (Plugin.DebugReplayKey.Value.IsDown())
            TryStartDebugReplay();

        if (_playing && SkipKeyPressed())
            SkipPlayback();
    }

    // Not KeyboardShortcut.IsDown(): that refuses to fire while ANY other key is held, and a
    // player who just died is often still holding a movement or sprint key.
    private static bool SkipKeyPressed()
    {
        var shortcut = Plugin.SkipReplayKey.Value;
        if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
            return false;
        foreach (var modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Asked every frame by HoldRespawnPatch while a replacement of <paramref name="body"/>
    /// waits. True keeps the respawn waiting for this death replay.
    /// </summary>
    public bool HoldReplacementOf(Player body)
        => PendingReplayKiller != null && _handoff.ShouldHold(body);

    public bool ReplacementWaiting => _handoff.ReplacementWaiting;

    /// <summary>The hold gave up by itself (raid ending, cap): nothing waits for the black any more.</summary>
    public void AbandonHeldReplacement() => _handoff.AbandonWaiting();

    /// <summary>The released pipeline runs from here (true) until it has finished (false).</summary>
    public void SetReplacementRunning(bool running)
    {
        if (running) _handoff.BeginRunning();
        else _handoff.EndRunning();
    }

    /// <summary>Raid start: no replay of a previous raid may hold anything or leave a reveal behind.</summary>
    public void ResetHandoffForNewRaid()
    {
        if (_playing) return;   // a replay cannot run across a raid start; never cut one short here
        _handoff.EndDeathReplay();
        _revealArmedFrame = -1;
        _revealStart = -1f;
    }

    /// <summary>
    /// The skip key. When something takes over the screen after the replay (a deferred
    /// Stop or a waiting respawn), jump to the final fade so that hand-off still happens
    /// under black; otherwise just end the replay and give the view back.
    /// </summary>
    private void SkipPlayback()
    {
        switch (RespawnHandoff.DecideSkip(
                    ReplayPatches.KillcamDeferStopPatch.StopWasDeferred,
                    _handoff.ReplacementWaiting,
                    _inEnding && (_endPhase == EndingPhase.FinalFade || _endPhase == EndingPhase.HoldBlack)))
        {
            case RespawnHandoff.SkipAction.EndNow:
                EndPlayback("skipped");
                break;
            case RespawnHandoff.SkipAction.FadeToBlack:
                Plugin.Instance.Logger.LogInfo("Replay skipped — handing off under black");
                EnterFinalFade();
                break;
        }
    }

    private void EnterFinalFade()
    {
        _inEnding = true;
        _endPhase = EndingPhase.FinalFade;
        _phaseClock = 0f;
        ReadyForStop = true;
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
        _victimBody = puppetSourcePlayer;
        _revealArmedFrame = -1;
        _revealStart = -1f;
        _camera = CameraManager.Instance?.Camera;
        _playing = _camera != null && killerPath.Length >= 2;

        Plugin.Instance.Logger.LogInfo(
            $"Playback start: {killerPath.Length} snapshots, span {_cameraPath.EndTime - _cameraPath.StartTime:F2}s, bones={(boneWindow != null ? boneWindow.Times.Length.ToString() : "none")}, camera={(_camera != null ? "ok" : "NULL")}");

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

            if (Plugin.ShowShotLines.Value)
            {
                _killerFlash = new ShotFlash();
                _ghostFlash = new ShotFlash();
                _killerTracers = ShotTracers.TryCreate("KillcamKillerShots", ShotTracers.KillerColor);
                _ghostTracers = ShotTracers.TryCreate("KillcamVictimShots", ShotTracers.VictimColor);
            }

            Application.onBeforeRender -= ApplyCurrentPose;
            Application.onBeforeRender += ApplyCurrentPose;
        }
    }

    private void EndPlayback(string reason)
    {
        // Ending under the replay's own black (a replacement took over, or it did not come):
        // fade the view in instead of cutting to it. A re-invoked Stop has the game's own
        // black screen up by then, which needs no reveal.
        if (_inEnding && _endPhase == EndingPhase.HoldBlack && ReplayPatches.KillcamDeferStopPatch.StopReinvokedAt <= 0f)
        {
            _revealArmedFrame = Time.frameCount;
            _revealStart = -1f;
        }

        _playing = false;
        _inEnding = false;
        _cutToLive = false;
        ReadyForStop = true;
        Application.onBeforeRender -= ApplyCurrentPose;
        _ghost?.Destroy();
        _ghost = null;
        _puppet?.Destroy();
        _puppet = null;
        _killerFlash?.Destroy();
        _killerFlash = null;
        _ghostFlash?.Destroy();
        _ghostFlash = null;
        _killerTracers?.Destroy();
        _killerTracers = null;
        _ghostTracers?.Destroy();
        _ghostTracers = null;
        _killerWeapon?.Destroy();
        _killerWeapon = null;
        _victimBody = null;
        _hider.RestoreAll();
        Plugin.Instance.Logger.LogInfo($"Playback end after {_clock:F2}s: {reason}");
    }

    public void StopPlayback(string reason)
    {
        if (_playing)
            EndPlayback(reason);
    }

    /// <summary>
    /// The local body changed (RespawnInterop BodyChanged). When the replaced body is this
    /// playback's victim, a respawn has taken over the screen: end now, in the frame of the
    /// replacement. With Redeploy.Core that frame still has the old camera (its Destroy is
    /// deferred to the end of the frame), so no pose lands on a dying camera and the hidden
    /// renderers come back at once. Without Core the poll reports a frame later and the
    /// camera-null check in LateUpdate may win; both end the same way.
    /// EndPlayback touches no PlayerCameraController and no body, which is what a
    /// BodyChanged handler may do (it can run inside Core's replacement pipeline).
    /// A change to no body is the raid ending, not a replacement: the replay keeps the
    /// raid-end hand-off it already owns (deferred Stop, HoldBlack).
    /// </summary>
    public void NotifyBodyReplaced(Player previousBody, Player currentBody, string reason)
    {
        if (!_playing
            || ReferenceEquals(previousBody, null)
            || ReferenceEquals(currentBody, null)
            || !ReferenceEquals(previousBody, _victimBody))
            return;

        // Under the black hold, EndPlayback arms the fade-in for the new body.
        StopPlayback($"replacement ({reason})");
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
                if (_phaseClock >= EffectiveLiveTailSeconds)
                {
                    if (RespawnHandoff.FadeToBlackAfterLiveTail(
                            ReplayPatches.KillcamDeferStopPatch.StopWasDeferred, _handoff.ReplacementWaiting))
                    {
                        // a deferred Stop or a waiting respawn takes over under black
                        EnterFinalFade();
                    }
                    else
                    {
                        // nothing waits (the respawn prompt is still open, or no respawn
                        // mod is involved) — hand the camera back directly
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
                    // Fully black now: a waiting respawn may run. The hold sees the release
                    // next frame, the pipeline runs synchronously, and its Replaced ends
                    // this playback (NotifyBodyReplaced) with the screen still black.
                    if (_handoff.ReplacementWaiting)
                    {
                        Plugin.Instance.Logger.LogInfo("Replay handed off — releasing the held respawn under black");
                        _handoff.ReleaseUnderBlack();
                    }
                }
                break;

            case EndingPhase.HoldBlack:
                // 1.5s after the re-invoke: the game's own black screen fades in and
                // was empirically not fully opaque yet at 0.8s (brief corpse-cam flash)
                switch (RespawnHandoff.DecideBlackHoldExit(
                            Time.time, _phaseClock, ReplayPatches.KillcamDeferStopPatch.StopReinvokedAt,
                            _handoff.ReplacementReleased, _handoff.ReplacementRunning))
                {
                    case RespawnHandoff.BlackHoldExit.HandedOffUnderGameBlack:
                        EndPlayback("handed off under black screen");
                        return;
                    case RespawnHandoff.BlackHoldExit.ReplacementDidNotArrive:
                        EndPlayback("released respawn did not arrive");
                        return;
                    case RespawnHandoff.BlackHoldExit.Timeout:
                        EndPlayback("black hold timeout");
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
        var aim = Quaternion.Euler(pitch, camSnap.Yaw, 0f);
        tr.position = camSnap.CamPos;
        tr.rotation = aim;

        if (_killerWeapon != null)
        {
            // the camera IS the killer's eyes, so his aim orients his weapon
            if (camSnap.WeaponValid && !_cutToLive)
                _killerWeapon.SetPose(camSnap.WeaponPos, camSnap.WeaponRot, aim, camSnap.CamPos);
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

        UpdateShotEffects(t);
    }

    // Shot lines plus a brief muzzle light at recorded shot times, both taken from
    // the SHOOTER's recorded aim: the killer's eyes are the camera and his weapon
    // ghost's muzzle the origin; the victim's aim comes from their own recorded
    // path, the source the puppet/ghost is posed from. The weapon pivots' local
    // axes do not follow the barrel, so only the aim gives the direction (see
    // WeaponMuzzle).
    private void UpdateShotEffects(float t)
    {
        if (_killerFlash == null) return;

        if (_inEnding)
        {
            _killerFlash.Hide();
            _ghostFlash.Hide();
            _killerTracers?.Hide();
            _ghostTracers?.Hide();
            return;
        }

        var tr = _camera.transform;
        Vector3 cameraPos = tr.position;
        Quaternion killerAim = tr.rotation;

        // prefer the actual muzzle of the killer's weapon ghost if it's showing
        if (_killerWeapon == null || !_killerWeapon.TryGetMuzzle(killerAim, out var killerMuzzle, out _))
            killerMuzzle = cameraPos + tr.forward * 0.55f - tr.up * 0.22f;

        _killerTracers?.Update(_shots, t, true, killerMuzzle, cameraPos, killerAim, cameraPos);
        if (AnyShotActive(_shots, t))
            _killerFlash.ShowAt(killerMuzzle);
        else
            _killerFlash.Hide();

        bool victimArmed = false;
        Vector3 victimMuzzle = default;
        Vector3 victimEye = default;
        Quaternion victimAim = Quaternion.identity;
        if (_victimPath != null && _victimShots != null && _victimShots.Count > 0)
        {
            var s = _victimPath.Sample(t);
            float ghostPitch = Plugin.InvertPitch.Value ? -s.Pitch : s.Pitch;
            victimAim = Quaternion.Euler(ghostPitch, s.Yaw, 0f);
            victimEye = s.CamPos;

            if (_puppet != null)
            {
                victimArmed = _puppet.TryGetMuzzle(victimAim, out victimMuzzle, out _);
            }
            else if (_ghost != null)
            {
                victimMuzzle = s.CamPos + victimAim * Vector3.forward * 0.6f;
                victimArmed = true;
            }
        }

        _ghostTracers?.Update(_victimShots, t, victimArmed, victimMuzzle, victimEye, victimAim, cameraPos);
        if (victimArmed && AnyShotActive(_victimShots, t))
            _ghostFlash.ShowAt(victimMuzzle);
        else
            _ghostFlash.Hide();
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
        _killerTracers?.Destroy();
        _ghostTracers?.Destroy();
        _killerWeapon?.Destroy();
        _hider.RestoreAll();
    }

    public IEnumerator RunDeathReplay(Player deadPlayer, Snapshot[] killerPath, Snapshot[] victimPath,
        BoneWindow boneWindow, List<float> shots, List<float> victimShots, string killerName)
    {
        // From here until the loop below ends, a respawn of this body waits for the replay.
        // Not a try/finally around the yields: Unity never disposes a stopped coroutine, so
        // a finally would promise more than it keeps. The hold itself is bounded instead
        // (HoldRespawnPatch caps the wait and also requires PendingReplayKiller).
        // The generation keeps a superseded run from clearing what a newer death replay set.
        int generation = ++_deathReplayGeneration;
        _handoff.BeginDeathReplay(deadPlayer);

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

        if (generation != _deathReplayGeneration)
            yield break;   // a newer death replay took over during the lead-in

        Play(killerPath, victimPath, boneWindow, puppetSource, deadPlayer, PendingReplayKiller, shots, victimShots, killerName, withLiveTail: true);
        while (_playing && generation == _deathReplayGeneration)
            yield return null;

        if (generation != _deathReplayGeneration)
            yield break;   // superseded: the newer run owns these fields now

        // safety: if Play never started (camera missing), EndPlayback never ran and
        // the deferred Stop would otherwise wait for its timeout cap
        ReadyForStop = true;
        PendingReplayKiller = null;
        _handoff.EndDeathReplay();
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
            Plugin.Instance.Logger.LogWarning("Debug replay: no track for nearest bot");
            return;
        }

        float now = Time.time;
        float from = now - Plugin.ReplayDuration.Value;
        var path = track.ExtractWindow(from, now);
        if (path.Length < 2)
        {
            Plugin.Instance.Logger.LogWarning("Debug replay: not enough snapshots");
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
        if (!_playing)
        {
            DrawReveal();
            return;
        }

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

        // Skip hint above the title; the black of the ending covers the lines below it anyway.
        var skipKey = Plugin.SkipReplayKey.Value;
        if (skipKey.MainKey != KeyCode.None)
        {
            string hint = _handoff.ReplacementWaiting
                ? $"Respawn after the replay — [{skipKey}] skip"
                : $"[{skipKey}] skip";
            GUI.Label(new Rect(0, baseY - 26, Screen.width, 24), hint, _timeStyle);
        }

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

    // After the replay ended under its own black hold: stay black through that frame and
    // the next (a long replacement frame would otherwise eat the fade), then fade the view
    // in. Bounded by RevealDuration, never sticky (RespawnHandoff.RevealAlpha).
    private void DrawReveal()
    {
        if (_revealArmedFrame < 0 || Event.current.type != EventType.Repaint) return;

        float alpha = RespawnHandoff.RevealAlpha(
            Time.frameCount, _revealArmedFrame, Time.unscaledTime, ref _revealStart, RevealDuration);

        if (alpha <= 0f)
        {
            _revealArmedFrame = -1;
            _revealStart = -1f;
            return;
        }

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, Mathf.Clamp01(alpha));
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = prev;
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
