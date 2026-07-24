using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KillcamReplay.Recording;

// A chronological slice of recorded skeleton poses, extracted once when a replay
// starts. Paths identify each bone relative to the player root so the puppet can
// map them onto a cloned hierarchy.
public class BoneWindow
{
    public float[] Times;
    public Vector3[] RootPos;
    public Quaternion[] RootRot;
    public Vector3[] BonePos;   // frame-major: [frame * BoneCount + bone]
    public Quaternion[] BoneRot;
    public int BoneCount;
    public string[] Paths;
    // world pose of the held weapon's view root per frame (the weapon view lives
    // outside the player hierarchy, so it gets its own track)
    public Vector3[] WeaponPos;
    public Quaternion[] WeaponRot;
    public bool[] WeaponValid;
}

// Records the full skeleton pose of the LOCAL player only — the replay ghost is
// always the victim, so exact bone playback is affordable: ~a few hundred
// transform reads per sample tick, flat preallocated arrays, no per-frame
// allocations. Bots are never bone-recorded.
public class BoneRecorder
{
    private Transform _root;
    private Transform[] _bones;
    private string[] _paths;
    private float[] _times;
    private Vector3[] _rootPos;
    private Quaternion[] _rootRot;
    private Vector3[] _bonePos;
    private Quaternion[] _boneRot;
    private Vector3[] _weaponPos;
    private Quaternion[] _weaponRot;
    private bool[] _weaponValid;
    private int _head;
    private int _count;
    private int _capacity;
    private int _boneCount;
    private bool _initFailed;

    public void Reset(int capacity)
    {
        _capacity = capacity;
        _root = null;
        _bones = null;
        _paths = null;
        _head = 0;
        _count = 0;
        _initFailed = false;
    }

    public void TrySample(Player mainPlayer, float now)
    {
        if (mainPlayer == null || _capacity == 0) return;

        // A respawn (e.g. via CorpseRun) gives a new Player/GameObject for the local
        // character; the cached bone Transforms would otherwise point at the discarded
        // old body (or, once destroyed, compare Unity-null). Start fresh for the new one.
        if (_bones != null && (_root == null || mainPlayer.gameObject.transform != _root))
        {
            Plugin.Instance.Logger.LogInfo("[KillcamReplay] BoneRecorder: main player changed — reinitializing for respawn");
            Reset(_capacity);
        }

        if (_initFailed) return;
        if (_bones == null && !Init(mainPlayer))
            return;

        int frame = _head;
        int frameBase = frame * _boneCount;
        int prevBase = _count > 0 ? ((_head - 1 + _capacity) % _capacity) * _boneCount : -1;

        _times[frame] = now;
        _rootPos[frame] = _root.position;
        _rootRot[frame] = _root.rotation;

        // The weapon view is aligned to the hands by game code, not by parenting —
        // record its world pose so the puppet can play it back exactly (capturing a
        // hand-relative offset from the ragdolled corpse instead yields an arbitrary
        // pose-dependent orientation).
        var weaponRoot = mainPlayer.PlayerBones?.WeaponRoot?.Original;
        if (weaponRoot != null)
        {
            _weaponPos[frame] = weaponRoot.position;
            _weaponRot[frame] = weaponRoot.rotation;
            _weaponValid[frame] = true;
        }
        else
        {
            _weaponValid[frame] = false;
        }

        for (int i = 0; i < _boneCount; i++)
        {
            var bone = _bones[i];
            if (bone == null)
            {
                // destroyed mid-raid (e.g. dropped gear) — carry the last known pose
                _bonePos[frameBase + i] = prevBase >= 0 ? _bonePos[prevBase + i] : Vector3.zero;
                _boneRot[frameBase + i] = prevBase >= 0 ? _boneRot[prevBase + i] : Quaternion.identity;
                continue;
            }
            _bonePos[frameBase + i] = bone.localPosition;
            _boneRot[frameBase + i] = bone.localRotation;
        }

        _head = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    private bool Init(Player mainPlayer)
    {
        var pelvis = mainPlayer.PlayerBones?.Pelvis?.Original;
        if (pelvis == null)
        {
            _initFailed = true;
            Plugin.Instance.Logger.LogWarning("[KillcamReplay] BoneRecorder: pelvis bone not found");
            return false;
        }

        _root = mainPlayer.gameObject.transform;

        // walk up from the pelvis to the skeleton root directly below the player root
        Transform skeletonRoot = pelvis;
        while (skeletonRoot.parent != null && skeletonRoot.parent != _root)
            skeletonRoot = skeletonRoot.parent;

        _bones = skeletonRoot.GetComponentsInChildren<Transform>(true);
        _boneCount = _bones.Length;
        _paths = new string[_boneCount];
        for (int i = 0; i < _boneCount; i++)
            _paths[i] = GetPath(_bones[i], _root);

        _times = new float[_capacity];
        _rootPos = new Vector3[_capacity];
        _rootRot = new Quaternion[_capacity];
        _bonePos = new Vector3[_capacity * _boneCount];
        _boneRot = new Quaternion[_capacity * _boneCount];
        _weaponPos = new Vector3[_capacity];
        _weaponRot = new Quaternion[_capacity];
        _weaponValid = new bool[_capacity];

        float mb = _capacity * _boneCount * 28f / (1024f * 1024f);
        Plugin.Instance.Logger.LogInfo($"[KillcamReplay] BoneRecorder: tracking {_boneCount} transforms, buffer {mb:F1} MB");
        return true;
    }

    private static string GetPath(Transform bone, Transform root)
    {
        var sb = new System.Text.StringBuilder(bone.name);
        var current = bone.parent;
        while (current != null && current != root)
        {
            sb.Insert(0, "/").Insert(0, current.name);
            current = current.parent;
        }
        return sb.ToString();
    }

    public BoneWindow ExtractWindow(float from, float to)
    {
        if (_bones == null || _count < 2) return null;

        int start = (_head - _count + _capacity) % _capacity;
        var frames = new List<int>(_count);
        for (int i = 0; i < _count; i++)
        {
            int f = (start + i) % _capacity;
            if (_times[f] >= from && _times[f] <= to)
                frames.Add(f);
        }
        if (frames.Count < 2) return null;

        var window = new BoneWindow
        {
            BoneCount = _boneCount,
            Paths = _paths,
            Times = new float[frames.Count],
            RootPos = new Vector3[frames.Count],
            RootRot = new Quaternion[frames.Count],
            BonePos = new Vector3[frames.Count * _boneCount],
            BoneRot = new Quaternion[frames.Count * _boneCount],
            WeaponPos = new Vector3[frames.Count],
            WeaponRot = new Quaternion[frames.Count],
            WeaponValid = new bool[frames.Count]
        };

        for (int i = 0; i < frames.Count; i++)
        {
            int f = frames[i];
            window.Times[i] = _times[f];
            window.RootPos[i] = _rootPos[f];
            window.RootRot[i] = _rootRot[f];
            window.WeaponPos[i] = _weaponPos[f];
            window.WeaponRot[i] = _weaponRot[f];
            window.WeaponValid[i] = _weaponValid[f];
            System.Array.Copy(_bonePos, f * _boneCount, window.BonePos, i * _boneCount, _boneCount);
            System.Array.Copy(_boneRot, f * _boneCount, window.BoneRot, i * _boneCount, _boneCount);
        }
        return window;
    }
}
