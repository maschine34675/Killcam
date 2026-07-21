using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
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

    public static Player PendingReplayKiller;

    private static readonly System.Reflection.FieldInfo _corpseField =
        HarmonyLib.AccessTools.Field(typeof(Player), "Corpse");

    private PathCursor _cameraPath;
    private PathCursor _victimPath;
    private ReplayGhost _ghost;
    private GhostPuppet _puppet;
    private readonly LivePlayerHider _hider = new();
    private List<float> _shots;
    private float _clock;
    private float _startTime;
    private bool _playing;
    private string _killerName;
    private Camera _camera;
    private GUIStyle _labelStyle;
    private GUIStyle _shotStyle;

    public bool IsPlaying => _playing;

    public static float GetExtraDelay()
    {
        return PendingReplayKiller != null
            ? LeadIn + Plugin.ReplayDuration.Value + TailBuffer
            : 0f;
    }

    private void Update()
    {
        if (Plugin.DebugReplayKey.Value.IsDown())
            TryStartDebugReplay();
    }

    private void Play(Snapshot[] killerPath, Snapshot[] victimPath, BoneWindow boneWindow,
        GameObject puppetSource, List<float> shots, string killerName)
    {
        _cameraPath = new PathCursor(killerPath);
        _victimPath = victimPath != null && victimPath.Length >= 2 && Plugin.ShowVictimGhost.Value
            ? new PathCursor(victimPath)
            : null;
        _shots = shots;
        _killerName = killerName;
        _clock = 0f;
        _startTime = _cameraPath.StartTime;
        _camera = CameraClass.Instance?.Camera;
        _playing = _camera != null && killerPath.Length >= 2;

        Plugin.Instance.Logger.LogInfo(
            $"[KillcamReplay] Playback start: {killerPath.Length} snapshots, span {_cameraPath.EndTime - _cameraPath.StartTime:F2}s, bones={(boneWindow != null ? boneWindow.Times.Length.ToString() : "none")}, camera={(_camera != null ? "ok" : "NULL")}");

        if (_playing)
        {
            if (Plugin.ShowVictimGhost.Value)
            {
                if (Plugin.UseRealModelGhost.Value && boneWindow != null && puppetSource != null)
                    _puppet = GhostPuppet.TryCreate(puppetSource, boneWindow);

                if (_puppet == null && _victimPath != null)
                    _ghost = new ReplayGhost();
            }

            if (Plugin.HideLiveBots.Value)
                _hider.HideAll();

            Application.onBeforeRender -= ApplyCurrentPose;
            Application.onBeforeRender += ApplyCurrentPose;
        }
    }

    private void EndPlayback(string reason)
    {
        _playing = false;
        Application.onBeforeRender -= ApplyCurrentPose;
        _ghost?.Destroy();
        _ghost = null;
        _puppet?.Destroy();
        _puppet = null;
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

        _clock += Time.deltaTime;

        if (_startTime + _clock >= _cameraPath.EndTime)
        {
            ApplyCurrentPose();
            EndPlayback("path exhausted");
            return;
        }

        ApplyCurrentPose();
    }

    private void ApplyCurrentPose()
    {
        if (!_playing || _camera == null) return;

        float t = Mathf.Min(_startTime + _clock, _cameraPath.EndTime);

        var camSnap = _cameraPath.Sample(t);
        float pitch = Plugin.InvertPitch.Value ? -camSnap.Pitch : camSnap.Pitch;
        var tr = _camera.transform;
        tr.position = camSnap.CamPos;
        tr.rotation = Quaternion.Euler(pitch, camSnap.Yaw, 0f);

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
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= ApplyCurrentPose;
        _ghost?.Destroy();
        _puppet?.Destroy();
        _hider.RestoreAll();
    }

    public IEnumerator RunDeathReplay(Player deadPlayer, Snapshot[] killerPath, Snapshot[] victimPath,
        BoneWindow boneWindow, List<float> shots, string killerName)
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

        Play(killerPath, victimPath, boneWindow, puppetSource, shots, killerName);
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
        if (Plugin.Recorder.TryGetTrack(main, out var mainTrack))
            victimPath = mainTrack.ExtractWindow(from, now);
        var boneWindow = Plugin.Recorder.Bones.ExtractWindow(from, now);

        StartCoroutine(RunDebugReplay(main.gameObject, path, victimPath, boneWindow, shots, nearest.Profile?.Nickname ?? "Bot"));
    }

    private IEnumerator RunDebugReplay(GameObject puppetSource, Snapshot[] path, Snapshot[] victimPath,
        BoneWindow boneWindow, List<float> shots, string name)
    {
        Play(path, victimPath, boneWindow, puppetSource, shots, name);
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
        }

        GUI.Label(new Rect(0, 20, Screen.width, 34), $"KILLCAM — {_killerName}", _labelStyle);

        float t = _startTime + _clock;
        for (int i = 0; i < _shots.Count; i++)
        {
            if (Mathf.Abs(_shots[i] - t) < 0.15f)
            {
                GUI.Label(new Rect(0, 56, Screen.width, 24), "● SCHUSS", _shotStyle);
                break;
            }
        }
    }
}
