using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Killcam.Recording;

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

    // Time of the most recent sample. Only players in AllAlivePlayersList are
    // sampled, so this doubles as "last seen alive" and drives track retirement.
    public float LastSampleTime { get; private set; }

    public PlayerTrack(int capacity)
    {
        _buffer = new Snapshot[capacity];
    }

    public void Add(in Snapshot s)
    {
        _buffer[_head] = s;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
        LastSampleTime = s.Time;
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
// The track dictionary grows when a new player spawns and shrinks again once a
// track goes stale (see PruneStaleTracks), so its size follows the number of
// living players, not the number that ever lived.
public class SnapshotRecorder : MonoBehaviour
{
    // A track is retired once its player has gone unsampled — dead, despawned or
    // destroyed — for longer than the replay window plus a small margin. Past that
    // point ExtractWindow could not return a usable path anyway (a killer who died
    // that much earlier has no snapshots left inside [death - Duration, death]),
    // while keeping the entry costs a ring buffer and, worse, pins the dead Player
    // itself: the dictionary key is a strong reference. A raid with continuous bot
    // spawns otherwise accumulates one track per bot that ever lived.
    private const float TrackGraceSeconds = 2f;
    private const float PruneInterval = 5f;
    private const float PruneLogInterval = 30f;

    private readonly Dictionary<Player, PlayerTrack> _tracks = new();
    private readonly List<Player> _staleScratch = new();
    private float _nextSampleTime;
    private float _nextPruneTime;
    private float _nextPruneLogTime;
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
        _nextPruneTime = 0f;
        _nextPruneLogTime = 0f;
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
        // respawn mod (e.g. Redeploy) the raid continues and tracking must too.
        // Only pause for the few seconds a replay actively owns the screen.
        if (!_armed || (Plugin.ReplayCtrl != null && Plugin.ReplayCtrl.IsPlaying)) return;

        float now = Time.time;
        if (now < _nextSampleTime) return;
        _nextSampleTime = now + _sampleInterval;

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            // Raid over: nothing reads these tracks again, and holding them would
            // pin every player of the finished raid until the next raid arms.
            _tracks.Clear();
            _armed = false;
            return;
        }

        try
        {
            Bones.TrySample(gameWorld.MainPlayer, now);
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"BoneRecorder sample failed: {ex}");
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

        if (now >= _nextPruneTime)
        {
            _nextPruneTime = now + PruneInterval;
            PruneStaleTracks(now, gameWorld.MainPlayer);
        }
    }

    // Player.OnDeadCoroutine unregisters the dead player from AllAlivePlayersList
    // one frame after death, so a track that stopped advancing marks a player that
    // died, despawned or was destroyed — no death hook of our own needed.
    private void PruneStaleTracks(float now, Player mainPlayer)
    {
        float cutoff = now - Plugin.ReplayDuration.Value - TrackGraceSeconds;

        _staleScratch.Clear();
        foreach (var pair in _tracks)
        {
            var player = pair.Key;

            // Unity-destroyed body: the track can never be extended or used again.
            if (player == null)
            {
                _staleScratch.Add(player);
                continue;
            }

            // The local player's track survives their own death: the recorder stays
            // armed for respawn mods (see Update) and no sample is written while
            // dead, so staleness would retire exactly the track the next replay
            // needs for the victim ghost. Only a previous body that a respawn has
            // already replaced as MainPlayer is allowed to expire.
            if (player.IsYourPlayer && (mainPlayer == null || ReferenceEquals(player, mainPlayer)))
                continue;

            if (pair.Value.LastSampleTime < cutoff)
                _staleScratch.Add(player);
        }

        if (_staleScratch.Count == 0) return;

        for (int i = 0; i < _staleScratch.Count; i++)
            _tracks.Remove(_staleScratch[i]);

        if (now >= _nextPruneLogTime)
        {
            _nextPruneLogTime = now + PruneLogInterval;
            Plugin.Instance.Logger.LogDebug($"Retired {_staleScratch.Count} stale player track(s), {_tracks.Count} live");
        }

        // never keep the dead players alive past the pass that dropped them
        _staleScratch.Clear();
    }
}
