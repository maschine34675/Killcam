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
        // Disable rather than destroy: safer for complex Odin-serialized modules
        // (ParticleSystem) and there's no benefit to actually removing the component.
        foreach (var l in clone.GetComponentsInChildren<Light>(true)) l.enabled = false;
        foreach (var p in clone.GetComponentsInChildren<ParticleSystem>(true)) p.Stop();

        // Everything else gets destroyed — Transform (can't be destroyed) and the mesh
        // rendering components (needed for the puppet to be visible) are excluded.
        // [RequireComponent] dependencies (e.g. AnimatorRootMotionAccess requiring
        // Animator) block a destroy until the dependent is gone first; a single
        // component-type-ordered pass can't know that ordering in general (native
        // components like Animator/Rigidbody aren't MonoBehaviours, so they can't be
        // swept together with scripts either) — so every component type is retried
        // together across passes until a pass removes nothing more.
        int stripped = 0;
        for (int pass = 0; pass < 10; pass++)
        {
            var components = clone.GetComponentsInChildren<Component>(true);
            bool any = false;
            foreach (var c in components)
            {
                if (c == null || c is Transform || c is Renderer || c is MeshFilter
                    || c is Light || c is ParticleSystem)
                    continue;
                try
                {
                    Object.DestroyImmediate(c);
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
