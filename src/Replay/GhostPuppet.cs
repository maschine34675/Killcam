using System.Collections.Generic;
using KillcamReplay.Recording;
using UnityEngine;

namespace KillcamReplay.Replay;

// Real-model replay ghost: a sanitized clone of the victim's body (the corpse IS
// the original dressed body — Corpse.CreateCorpse converts the player GameObject
// in place) whose bones are overwritten every frame with recorded poses. No
// Animator involved — raw bone playback reproduces gait, lean and aim exactly.
//
// The clone is instantiated under an INACTIVE holder so none of its components
// run Awake, then all scripts/physics are DestroyImmediate'd (no Awake ran → no
// OnDestroy side effects either) before the holder is activated.
public class GhostPuppet
{
    private readonly GameObject _holder;
    private readonly Transform _cloneRoot;
    private readonly Transform[] _mapped;
    private readonly BoneWindow _window;
    private readonly GameObject _source;
    private readonly List<Renderer> _hiddenSourceRenderers = new();
    private int _cursor;

    public static GhostPuppet TryCreate(GameObject source, BoneWindow window)
    {
        try
        {
            var puppet = new GhostPuppet(source, window);
            return puppet;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"[KillcamReplay] Puppet creation failed, falling back to silhouette: {ex}");
            return null;
        }
    }

    private GhostPuppet(GameObject source, BoneWindow window)
    {
        _window = window;
        _source = source;

        _holder = new GameObject("KillcamReplayPuppet");
        _holder.SetActive(false);

        var clone = Object.Instantiate(source, _holder.transform, true);

        // The local player's own head/torso render in ShadowsOnly mode while in first
        // person (EFT's "hide your own head from your own camera" trick). The corpse
        // conversion never resets it, so a raw clone inherits the hidden state.
        // PlayerBody.UpdatePlayerRenders() is the game's normal fix for this, but it
        // iterates PlayerBody.BodySkins, which is populated at Awake/equip time — empty
        // on a clone whose Awake we deliberately never let run. So force every renderer
        // in the clone to cast+render normally directly, before Sanitize strips
        // PlayerBody (and everything else) away.
        foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

        Sanitize(clone);
        _cloneRoot = clone.transform;

        _mapped = new Transform[window.Paths.Length];
        int found = 0;
        for (int i = 0; i < window.Paths.Length; i++)
        {
            _mapped[i] = _cloneRoot.Find(window.Paths[i]);
            if (_mapped[i] != null) found++;
        }
        Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Puppet: mapped {found}/{window.Paths.Length} bones");

        Apply(window.Times[0]);
        _holder.SetActive(true);

        // Hide the source's own renderers now that the clone has copied their visual
        // state — hiding before Instantiate would make the clone invisible too. This
        // keeps the real corpse (death replay) or the live body (F9 debug replay) from
        // showing alongside the puppet walking its recorded path.
        HideSourceRenderers();
    }

    private void HideSourceRenderers()
    {
        if (_source == null) return;
        var renderers = _source.GetComponentsInChildren<Renderer>(false);
        for (int i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer != null && renderer.enabled)
            {
                renderer.enabled = false;
                _hiddenSourceRenderers.Add(renderer);
            }
        }
    }

    private static void Sanitize(GameObject clone)
    {
        int stripped = 0;

        // joints before rigidbodies (CharacterJoint requires Rigidbody)
        foreach (var j in clone.GetComponentsInChildren<Joint>(true)) { Object.DestroyImmediate(j); stripped++; }
        foreach (var r in clone.GetComponentsInChildren<Rigidbody>(true)) { Object.DestroyImmediate(r); stripped++; }
        foreach (var c in clone.GetComponentsInChildren<Collider>(true)) { Object.DestroyImmediate(c); stripped++; }
        foreach (var c in clone.GetComponentsInChildren<Cloth>(true)) { Object.DestroyImmediate(c); stripped++; }
        foreach (var a in clone.GetComponentsInChildren<Animator>(true)) { Object.DestroyImmediate(a); stripped++; }
        foreach (var a in clone.GetComponentsInChildren<Animation>(true)) { Object.DestroyImmediate(a); stripped++; }
        foreach (var a in clone.GetComponentsInChildren<AudioSource>(true)) { Object.DestroyImmediate(a); stripped++; }
        foreach (var l in clone.GetComponentsInChildren<Light>(true)) l.enabled = false;
        foreach (var p in clone.GetComponentsInChildren<ParticleSystem>(true)) p.Stop();

        // scripts may depend on each other via [RequireComponent] — sweep in passes
        for (int pass = 0; pass < 8; pass++)
        {
            var scripts = clone.GetComponentsInChildren<MonoBehaviour>(true);
            bool any = false;
            foreach (var script in scripts)
            {
                if (script == null) continue;
                try
                {
                    Object.DestroyImmediate(script);
                    stripped++;
                    any = true;
                }
                catch
                {
                    // dependency still present — next pass
                }
            }
            if (!any) break;
        }

        Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Puppet: stripped {stripped} components");
    }

    public void Apply(float t)
    {
        int n = _window.Times.Length;

        while (_cursor < n - 2 && _window.Times[_cursor + 1] <= t)
            _cursor++;

        int a = _cursor;
        int b = Mathf.Min(_cursor + 1, n - 1);
        float span = _window.Times[b] - _window.Times[a];
        float alpha = span > 0.0001f ? Mathf.Clamp01((t - _window.Times[a]) / span) : 0f;

        _cloneRoot.SetPositionAndRotation(
            Vector3.Lerp(_window.RootPos[a], _window.RootPos[b], alpha),
            Quaternion.Slerp(_window.RootRot[a], _window.RootRot[b], alpha));

        int baseA = a * _window.BoneCount;
        int baseB = b * _window.BoneCount;
        for (int i = 0; i < _window.BoneCount; i++)
        {
            var bone = _mapped[i];
            if (bone == null) continue;
            bone.localPosition = Vector3.Lerp(_window.BonePos[baseA + i], _window.BonePos[baseB + i], alpha);
            bone.localRotation = Quaternion.Slerp(_window.BoneRot[baseA + i], _window.BoneRot[baseB + i], alpha);
        }
    }

    public void Destroy()
    {
        for (int i = 0; i < _hiddenSourceRenderers.Count; i++)
        {
            var renderer = _hiddenSourceRenderers[i];
            if (renderer != null)
                renderer.enabled = true;
        }
        _hiddenSourceRenderers.Clear();

        if (_holder != null)
            Object.Destroy(_holder);
    }
}
