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
        if (mainPlayer == null || _initFailed || _capacity == 0) return;

        if (_bones == null && !Init(mainPlayer))
            return;

        int frame = _head;
        int frameBase = frame * _boneCount;
        int prevBase = _count > 0 ? ((_head - 1 + _capacity) % _capacity) * _boneCount : -1;

        _times[frame] = now;
        _rootPos[frame] = _root.position;
        _rootRot[frame] = _root.rotation;

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
            BoneRot = new Quaternion[frames.Count * _boneCount]
        };

        for (int i = 0; i < frames.Count; i++)
        {
            int f = frames[i];
            window.Times[i] = _times[f];
            window.RootPos[i] = _rootPos[f];
            window.RootRot[i] = _rootRot[f];
            System.Array.Copy(_bonePos, f * _boneCount, window.BonePos, i * _boneCount, _boneCount);
            System.Array.Copy(_boneRot, f * _boneCount, window.BoneRot, i * _boneCount, _boneCount);
        }
        return window;
    }
}
