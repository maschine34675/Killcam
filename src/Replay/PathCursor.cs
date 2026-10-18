using Killcam.Recording;
using UnityEngine;

namespace Killcam.Replay;

// Interpolating sampler over a chronological snapshot array. Playback time only
// moves forward, so the bracketing index is cached and advanced monotonically.
public class PathCursor
{
    private readonly Snapshot[] _path;
    private int _cursor;

    public PathCursor(Snapshot[] path)
    {
        _path = path;
    }

    public float StartTime => _path[0].Time;
    public float EndTime => _path[_path.Length - 1].Time;

    public Snapshot Sample(float t)
    {
        if (t <= _path[0].Time) return _path[0];
        if (t >= _path[_path.Length - 1].Time) return _path[_path.Length - 1];

        while (_cursor < _path.Length - 2 && _path[_cursor + 1].Time <= t)
            _cursor++;

        ref var a = ref _path[_cursor];
        ref var b = ref _path[_cursor + 1];
        float span = b.Time - a.Time;
        float alpha = span > 0.0001f ? Mathf.Clamp01((t - a.Time) / span) : 0f;

        return new Snapshot
        {
            Time = t,
            CamPos = Vector3.Lerp(a.CamPos, b.CamPos, alpha),
            Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, alpha),
            Pitch = Mathf.LerpAngle(a.Pitch, b.Pitch, alpha),
            PoseLevel = Mathf.Lerp(a.PoseLevel, b.PoseLevel, alpha),
            Prone = alpha < 0.5f ? a.Prone : b.Prone,
            WeaponPos = Vector3.Lerp(a.WeaponPos, b.WeaponPos, alpha),
            WeaponRot = Quaternion.Slerp(a.WeaponRot, b.WeaponRot, alpha),
            WeaponValid = a.WeaponValid && b.WeaponValid
        };
    }
}
