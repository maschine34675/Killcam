using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KillcamReplay.Recording;

public struct Snapshot
{
    public float Time;
    public Vector3 CamPos;
    public float Yaw;
    public float Pitch;
    public float PoseLevel; // 0 = crouched, 1 = standing
    public bool Prone;
    // world pose of the held weapon's view root (used to show the killer's weapon
    // riding its recorded path during the replay)
    public Vector3 WeaponPos;
    public Quaternion WeaponRot;
    public bool WeaponValid;
}

// Fixed-size ring buffer per player. Preallocated once on first sighting;
// writes never allocate.
public class PlayerTrack
{
    private readonly Snapshot[] _buffer;
    private int _head;
    private int _count;

    private readonly float[] _shotTimes = new float[64];
    private int _shotHead;
    private int _shotCount;

    public PlayerTrack(int capacity)
    {
        _buffer = new Snapshot[capacity];
    }

    public void Add(in Snapshot s)
    {
        _buffer[_head] = s;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public void AddShot(float time)
    {
        _shotTimes[_shotHead] = time;
        _shotHead = (_shotHead + 1) % _shotTimes.Length;
        if (_shotCount < _shotTimes.Length) _shotCount++;
    }

    // Copies snapshots with Time in [from, to] into a new chronological array.
    // Only called once when a replay starts — allocation is fine here.
    public Snapshot[] ExtractWindow(float from, float to)
    {
        var result = new List<Snapshot>(_count);
        int start = (_head - _count + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < _count; i++)
        {
            ref var s = ref _buffer[(start + i) % _buffer.Length];
            if (s.Time >= from && s.Time <= to)
                result.Add(s);
        }
        return result.ToArray();
    }

    public List<float> ExtractShots(float from, float to)
    {
        var result = new List<float>();
        int start = (_shotHead - _shotCount + _shotTimes.Length) % _shotTimes.Length;
        for (int i = 0; i < _shotCount; i++)
        {
            float t = _shotTimes[(start + i) % _shotTimes.Length];
            if (t >= from && t <= to)
                result.Add(t);
        }
        return result;
    }
}

// Samples camera position + view rotation of every alive player into per-player
// ring buffers at a fixed rate (default 20 Hz). The per-frame cost when a sample
// is not due is two float comparisons; a sample tick reads one transform position
// and one Vector2 per player and writes a struct into a preallocated array.
// The track dictionary only grows when a new player spawns.
public class SnapshotRecorder : MonoBehaviour
{
    private readonly Dictionary<Player, PlayerTrack> _tracks = new();
    private float _nextSampleTime;
    private float _sampleInterval;
    private int _capacity;
    private bool _armed;

    // full skeleton poses, local player only (for the real-model replay puppet)
    public readonly BoneRecorder Bones = new();

    public void ResetForNewRaid()
    {
        _tracks.Clear();
        _sampleInterval = 1f / Plugin.SampleRate.Value;
        _capacity = Mathf.CeilToInt(Plugin.BufferSeconds.Value * Plugin.SampleRate.Value) + 2;
        _nextSampleTime = 0f;
        _armed = true;
        Bones.Reset(_capacity);
    }

    public bool TryGetTrack(Player player, out PlayerTrack track)
    {
        return _tracks.TryGetValue(player, out track);
    }

    public void RecordShot(Player player)
    {
        if (!_armed || player == null) return;
        if (_tracks.TryGetValue(player, out var track))
            track.AddShot(Time.time);
    }

    private void Update()
    {
        // Recording is intentionally never disarmed on local-player death: with a
        // respawn mod (e.g. CorpseRun) the raid continues and tracking must too.
        // Only pause for the few seconds a replay actively owns the screen.
        if (!_armed || (Plugin.ReplayCtrl != null && Plugin.ReplayCtrl.IsPlaying)) return;

        float now = Time.time;
        if (now < _nextSampleTime) return;
        _nextSampleTime = now + _sampleInterval;

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            _armed = false;
            return;
        }

        try
        {
            Bones.TrySample(gameWorld.MainPlayer, now);
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"[KillcamReplay] BoneRecorder sample failed: {ex}");
        }

        var players = gameWorld.AllAlivePlayersList;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null) continue;

            var camPos = player.CameraPosition;
            if (camPos == null) continue;

            if (!_tracks.TryGetValue(player, out var track))
            {
                track = new PlayerTrack(_capacity);
                _tracks[player] = track;
            }

            Vector2 rotation = player.Rotation;
            var movementContext = player.MovementContext;
            var weaponRoot = player.PlayerBones?.WeaponRoot?.Original;
            track.Add(new Snapshot
            {
                Time = now,
                CamPos = camPos.position,
                Yaw = rotation.x,
                Pitch = rotation.y,
                PoseLevel = movementContext != null ? movementContext.PoseLevel : 1f,
                Prone = movementContext != null && movementContext.IsInPronePose,
                WeaponPos = weaponRoot != null ? weaponRoot.position : default,
                WeaponRot = weaponRoot != null ? weaponRoot.rotation : Quaternion.identity,
                WeaponValid = weaponRoot != null
            });
        }
    }
}
