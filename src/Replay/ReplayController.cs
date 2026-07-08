using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using KillcamReplay.Recording;
using UnityEngine;

namespace KillcamReplay.Replay;

// Plays a recorded camera path on the main camera. While a death replay owns the
// raid-end sequence, PendingReplayKiller is non-null and LocalGame.Stop() is deferred
// (see ReplayPatches.DeferStopPatch).
public class ReplayController : MonoBehaviour
{
    public const float LeadIn = 0.8f;
    public const float TailBuffer = 0.7f;

    public static Player PendingReplayKiller;

    private Snapshot[] _path;
    private List<float> _shots;
    private int _cursor;
    private float _clock;
    private float _pathStartTime;
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

    private void Play(Snapshot[] path, List<float> shots, string killerName)
    {
        _path = path;
        _shots = shots;
        _killerName = killerName;
        _cursor = 0;
        _clock = 0f;
        _pathStartTime = path[0].Time;
        _camera = CameraClass.Instance?.Camera;
        _playing = _camera != null && path.Length >= 2;

        float span = path[path.Length - 1].Time - path[0].Time;
        Plugin.Instance.Logger.LogInfo(
            $"[KillcamReplay] Playback start: {path.Length} snapshots, span {span:F2}s, camera={(_camera != null ? "ok" : "NULL")}");

        if (_playing)
        {
            // Apply the pose again right before rendering so nothing that runs in
            // LateUpdate after us (e.g. a PlayerCameraController snapping the camera
            // back to its player via ForceSetPosition) can override the visible frame.
            Application.onBeforeRender -= ApplyCurrentPose;
            Application.onBeforeRender += ApplyCurrentPose;
        }
    }

    private void EndPlayback(string reason)
    {
        _playing = false;
        Application.onBeforeRender -= ApplyCurrentPose;
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
        float t = _pathStartTime + _clock;

        if (t >= _path[_path.Length - 1].Time)
        {
            ApplyInterpolated(_path.Length - 1, _path.Length - 1, 0f);
            EndPlayback("path exhausted");
            return;
        }

        while (_cursor < _path.Length - 2 && _path[_cursor + 1].Time <= t)
            _cursor++;

        ApplyCurrentPose();
    }

    private void ApplyCurrentPose()
    {
        if (!_playing || _camera == null) return;

        float t = _pathStartTime + _clock;
        float span = _path[_cursor + 1].Time - _path[_cursor].Time;
        float alpha = span > 0.0001f ? Mathf.Clamp01((t - _path[_cursor].Time) / span) : 0f;
        ApplyInterpolated(_cursor, _cursor + 1, alpha);
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= ApplyCurrentPose;
    }

    private void ApplyInterpolated(int ia, int ib, float alpha)
    {
        ref var a = ref _path[ia];
        ref var b = ref _path[ib];

        Vector3 pos = Vector3.Lerp(a.CamPos, b.CamPos, alpha);
        float yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, alpha);
        float pitch = Mathf.LerpAngle(a.Pitch, b.Pitch, alpha);
        if (Plugin.InvertPitch.Value)
            pitch = -pitch;

        var tr = _camera.transform;
        tr.position = pos;
        tr.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    // No PlayerCameraController is destroyed or created: destroying it detaches the
    // EffectsController from the player (its death handler then NullRefs and aborts
    // the raid-end flow → soft-lock), and creating one is unnecessary because the
    // onBeforeRender stomp already wins the rendered frame against the corpse cam.
    public IEnumerator RunDeathReplay(Player deadPlayer, Snapshot[] path, List<float> shots, string killerName)
    {
        yield return new WaitForSeconds(LeadIn);

        Play(path, shots, killerName);
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
        var path = track.ExtractWindow(now - Plugin.ReplayDuration.Value, now);
        if (path.Length < 2)
        {
            Plugin.Instance.Logger.LogWarning("[KillcamReplay] Debug replay: not enough snapshots");
            return;
        }
        var shots = track.ExtractShots(now - Plugin.ReplayDuration.Value, now);

        StartCoroutine(RunDebugReplay(main, path, shots, nearest.Profile?.Nickname ?? "Bot"));
    }

    private IEnumerator RunDebugReplay(Player main, Snapshot[] path, List<float> shots, string name)
    {
        Play(path, shots, name);
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

        float t = _pathStartTime + _clock;
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
