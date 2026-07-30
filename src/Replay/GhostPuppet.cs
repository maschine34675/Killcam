using System.Collections.Generic;
using EFT;
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
    private readonly Player _sourcePlayer;
    private readonly List<Renderer> _hiddenSourceRenderers = new();
    private readonly List<Light> _hiddenSourceLights = new();
    private Transform _weaponPivot;
    private Vector3[] _weaponTrackPos;
    private Quaternion[] _weaponTrackRot;
    private bool[] _weaponTrackValid;
    private float _muzzleLocalZ = 0.5f;
    private int _cursor;

    public static GhostPuppet TryCreate(GameObject source, Player sourcePlayer, BoneWindow window)
    {
        try
        {
            var puppet = new GhostPuppet(source, sourcePlayer, window);
            return puppet;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"[KillcamReplay] Puppet creation failed, falling back to silhouette: {ex}");
            return null;
        }
    }

    private GhostPuppet(GameObject source, Player sourcePlayer, BoneWindow window)
    {
        _window = window;
        _source = source;
        _sourcePlayer = sourcePlayer;

        _holder = new GameObject("KillcamReplayPuppet");
        _holder.SetActive(false);

        // Cloning the body drags along the Odin-serialized item scripts of slot-view
        // gear (holstered/slung weapons); their OnAfterDeserialize throws a
        // NullReferenceException internally (they expect backend context the item
        // factory would provide). The failure is provably harmless — those components
        // get stripped right after anyway — but Unity logs the exception. Silence the
        // logger for exactly the synchronous Instantiate call.
        GameObject clone;
        bool prevLogEnabled = Debug.unityLogger.logEnabled;
        Debug.unityLogger.logEnabled = false;
        try
        {
            clone = Object.Instantiate(source, _holder.transform, true);
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLogEnabled;
        }

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

        AttachWeaponMeshes();

        Apply(window.Times[0]);
        _holder.SetActive(true);

        // Hide the source's own renderers now that the clone has copied their visual
        // state — hiding before Instantiate would make the clone invisible too. This
        // keeps the real corpse (death replay) or the live body (F9 debug replay) from
        // showing alongside the puppet walking its recorded path.
        HideSourceRenderers();
    }

    // The held weapon's Odin-serialized item scripts throw during a raw clone (they
    // expect backend context the item factory would provide), leaving the puppet
    // unarmed — and the weapon view (PlayerBones.WeaponRoot.Original) is not even a
    // descendant of the player: it lives in its own scene-level container
    // ("weapon_..._container(Clone)") that the game aligns to the hands every frame.
    // So the weapon is never part of the body clone at all. Instead, copy ONLY its
    // meshes (plain new GameObjects with shared mesh + materials, no scripts, no
    // physics, no Odin) under a single pivot whose WORLD pose is played back from
    // the recording each frame — the BoneRecorder tracks the weapon root's world
    // pose alongside the skeleton. Capturing a bone-relative offset from the corpse
    // instead proved unusable: the ragdoll pose makes that offset arbitrary.
    private void AttachWeaponMeshes()
    {
        try
        {
            var weaponRoot = _sourcePlayer?.PlayerBones?.WeaponRoot?.Original;
            if (weaponRoot == null || _window.WeaponPos == null) return;

            var pivot = new GameObject("PuppetWeapon").transform;
            pivot.SetParent(_holder.transform, false);

            int copied = 0;
            var sourceRenderers = weaponRoot.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < sourceRenderers.Length; i++)
            {
                var renderer = sourceRenderers[i];
                if (renderer == null || !renderer.enabled) continue;
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;

                var go = new GameObject("PuppetWeaponMesh");
                var t = go.transform;
                t.SetParent(pivot, false);
                t.localPosition = weaponRoot.InverseTransformPoint(renderer.transform.position);
                t.localRotation = Quaternion.Inverse(weaponRoot.rotation) * renderer.transform.rotation;
                t.localScale = renderer.transform.lossyScale;

                go.AddComponent<MeshFilter>().sharedMesh = meshFilter.sharedMesh;
                var copy = go.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                copied++;

                // rough muzzle estimate: furthest forward extent of any weapon part
                float partMaxZ = t.localPosition.z
                    + (meshFilter.sharedMesh.bounds.center.z + meshFilter.sharedMesh.bounds.extents.z) * t.localScale.z;
                if (partMaxZ > _muzzleLocalZ)
                    _muzzleLocalZ = partMaxZ;
            }

            if (copied == 0)
            {
                Plugin.Instance.Logger.LogWarning("[KillcamReplay] Puppet weapon: no meshes found to copy");
                Object.Destroy(pivot.gameObject);
                return;
            }

            // It is mode-dependent which transform the game actually drives (the view
            // container root vs. Weapon_Root_Anim, which ShiftWeaponRoot sets during
            // first-person play) — use whichever track actually moved in the window.
            float spreadRoot = TrackSpread(_window.WeaponPos, _window.WeaponValid);
            float spreadAnim = TrackSpread(_window.WeaponAnimPos, _window.WeaponAnimValid);
            bool useAnim = spreadRoot < 0.05f && spreadAnim >= 0.05f;
            _weaponTrackPos = useAnim ? _window.WeaponAnimPos : _window.WeaponPos;
            _weaponTrackRot = useAnim ? _window.WeaponAnimRot : _window.WeaponRot;
            _weaponTrackValid = useAnim ? _window.WeaponAnimValid : _window.WeaponValid;

            _weaponPivot = pivot;
            int skinned = weaponRoot.GetComponentsInChildren<SkinnedMeshRenderer>(false).Length;
            Plugin.Instance.Logger.LogInfo(
                $"[KillcamReplay] Puppet weapon: copied {copied} meshes; track spread root={spreadRoot:F2}m anim={spreadAnim:F2}m -> using {(useAnim ? "anim" : "root")}"
                + (skinned > 0 ? $" ({skinned} skinned skipped)" : ""));
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"[KillcamReplay] Puppet weapon attach failed: {ex}");
        }
    }

    private static string GetPath(Transform target, Transform root)
    {
        var sb = new System.Text.StringBuilder(target.name);
        var current = target.parent;
        while (current != null && current != root)
        {
            sb.Insert(0, "/").Insert(0, current.name);
            current = current.parent;
        }
        return sb.ToString();
    }

    private void HideSourceRenderers()
    {
        if (_source == null) return;
        HideRenderers(_source.GetComponentsInChildren<Renderer>(false));

        // the weapon view can live outside the source hierarchy (see LivePlayerHider)
        var weaponRoot = _sourcePlayer?.PlayerBones?.WeaponRoot?.Original;
        if (weaponRoot != null)
        {
            HideRenderers(weaponRoot.GetComponentsInChildren<Renderer>(false));
            foreach (var light in weaponRoot.GetComponentsInChildren<Light>(false))
            {
                if (light != null && light.enabled)
                {
                    light.enabled = false;
                    _hiddenSourceLights.Add(light);
                }
            }
        }
    }

    private void HideRenderers(Renderer[] renderers)
    {
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

    // Object.DestroyImmediate on a component blocked by a [RequireComponent]
    // dependency does NOT throw a catchable C# exception — Unity logs
    // "Can't remove X because Y depends on it" internally and silently no-ops. A
    // try/catch around it therefore can't detect or react to the failure; the only
    // way to avoid the log spam is to never attempt a blocked removal in the first
    // place, which means respecting dependency direction:
    //   - A requirer (Y, e.g. a script or filter) can ALWAYS be removed —
    //     removing the "needs X" side is never blocked by its own requirement.
    //   - The required side (X) can only be removed once every requirer is gone.
    // So: destroy Joints before Rigidbodies (the one common, predictable native
    // pairing on a ragdoll skeleton — every bone hits it, so it's worth a
    // dedicated pass to avoid a burst of identical log lines), then every
    // MonoBehaviour (script) before any native component (a script can require a
    // native component, e.g. AnimatorRootMotionAccess→Animator or the bundled
    // Meta XR spatial-audio scripts→AudioSource, but never the reverse), then
    // whatever native components remain. Each stage retries in passes since a
    // requirer can itself have its own requirer (script chains, or a script
    // requiring a native filter that another native component also requires).
    private static void Sanitize(GameObject clone)
    {
        // Disable rather than destroy: safer for complex Odin-serialized modules
        // (ParticleSystem) and there's no benefit to actually removing the component.
        foreach (var l in clone.GetComponentsInChildren<Light>(true)) l.enabled = false;
        foreach (var p in clone.GetComponentsInChildren<ParticleSystem>(true)) p.Stop();

        int stripped = 0;
        foreach (var j in clone.GetComponentsInChildren<Joint>(true))
        {
            Object.DestroyImmediate(j);
            if (j == null) stripped++;
        }

        stripped += StripUntilStable(clone, c => c is MonoBehaviour);
        stripped += StripUntilStable(clone, c =>
            c is not Transform && c is not Renderer && c is not MeshFilter
            && c is not Light && c is not ParticleSystem);

        Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Puppet: stripped {stripped} components");
    }

    private static int StripUntilStable(GameObject clone, System.Func<Component, bool> shouldRemove)
    {
        int stripped = 0;
        for (int pass = 0; pass < 10; pass++)
        {
            var components = clone.GetComponentsInChildren<Component>(true);
            bool any = false;
            foreach (var c in components)
            {
                if (c == null || !shouldRemove(c) || IsRequiredBySibling(c)) continue;
                try
                {
                    Object.DestroyImmediate(c);
                }
                catch
                {
                    // some third-party OnDestroy hook threw — leave it, try again next pass
                }
                if (c == null) // actually removed (Unity fake-null after a real destroy)
                {
                    stripped++;
                    any = true;
                }
            }
            if (!any) break;
        }
        return stripped;
    }

    // Unity refuses to DestroyImmediate a component while a sibling declares
    // [RequireComponent] on its type — and that refusal does NOT throw, it only
    // logs "Can't remove X because Y depends on it". The only way to keep the log
    // clean is to never attempt a blocked removal: skip the component this pass,
    // its dependents get removed first and free it up in a later pass. This also
    // resolves script→script chains (e.g. the engine-bundled Meta XR spatial-audio
    // scripts requiring each other) that stage ordering alone cannot.
    private static readonly Dictionary<System.Type, System.Type[]> _requirementsCache = new();

    private static bool IsRequiredBySibling(Component c)
    {
        var cType = c.GetType();
        var siblings = c.GetComponents<Component>();
        foreach (var sibling in siblings)
        {
            if (sibling == null || ReferenceEquals(sibling, c)) continue;
            foreach (var required in GetRequirements(sibling.GetType()))
            {
                if (required.IsAssignableFrom(cType))
                    return true;
            }
        }
        return false;
    }

    private static System.Type[] GetRequirements(System.Type type)
    {
        if (_requirementsCache.TryGetValue(type, out var cached))
            return cached;

        List<System.Type> result = null;
        // walk base types manually — RequireComponent is not marked as inherited,
        // but Unity enforces base-class requirements at runtime (e.g. CharacterJoint
        // inherits Joint's Rigidbody requirement)
        for (var t = type; t != null && t != typeof(Component); t = t.BaseType)
        {
            foreach (RequireComponent req in t.GetCustomAttributes(typeof(RequireComponent), false))
            {
                result ??= new List<System.Type>();
                if (req.m_Type0 != null) result.Add(req.m_Type0);
                if (req.m_Type1 != null) result.Add(req.m_Type1);
                if (req.m_Type2 != null) result.Add(req.m_Type2);
            }
        }

        var requirements = result != null ? result.ToArray() : System.Array.Empty<System.Type>();
        _requirementsCache[type] = requirements;
        return requirements;
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

        if (_weaponPivot != null && _weaponTrackPos != null)
        {
            bool valid = _weaponTrackValid[a] && _weaponTrackValid[b];
            if (valid)
            {
                if (!_weaponPivot.gameObject.activeSelf)
                    _weaponPivot.gameObject.SetActive(true);
                _weaponPivot.SetPositionAndRotation(
                    Vector3.Lerp(_weaponTrackPos[a], _weaponTrackPos[b], alpha),
                    Quaternion.Slerp(_weaponTrackRot[a], _weaponTrackRot[b], alpha));
            }
            else if (_weaponPivot.gameObject.activeSelf)
            {
                _weaponPivot.gameObject.SetActive(false);
            }
        }
    }

    // World pose of the puppet weapon's (estimated) muzzle, for shot flashes.
    public bool TryGetMuzzle(out Vector3 position, out Quaternion direction)
    {
        position = default;
        direction = default;
        if (_weaponPivot == null || !_weaponPivot.gameObject.activeSelf) return false;
        position = _weaponPivot.TransformPoint(new Vector3(0f, 0f, _muzzleLocalZ));
        direction = _weaponPivot.rotation;
        return true;
    }

    private static float TrackSpread(Vector3[] positions, bool[] valid)
    {
        if (positions == null || valid == null) return 0f;

        int first = -1;
        float maxSqr = 0f;
        for (int i = 0; i < positions.Length; i++)
        {
            if (!valid[i]) continue;
            if (first < 0)
            {
                first = i;
                continue;
            }
            float sqr = (positions[i] - positions[first]).sqrMagnitude;
            if (sqr > maxSqr) maxSqr = sqr;
        }
        return Mathf.Sqrt(maxSqr);
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

        for (int i = 0; i < _hiddenSourceLights.Count; i++)
        {
            var light = _hiddenSourceLights[i];
            if (light != null)
                light.enabled = true;
        }
        _hiddenSourceLights.Clear();

        if (_holder != null)
            Object.Destroy(_holder);
    }
}
