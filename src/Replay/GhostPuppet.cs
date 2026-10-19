using System.Collections.Generic;
using EFT;
using Killcam.Recording;
using UnityEngine;

namespace Killcam.Replay;

// Replay body made from owned visual transforms, meshes and remapped skin bones.
// No player/item scripts are cloned or initialized. Recorded poses drive the
// skeleton directly; the native world and its materials remain owned by EFT.
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
    private Bounds _weaponLocalBounds;
    private bool _hasWeaponBounds;
    private int _cursor;
    private bool _destroyed;

    public static GhostPuppet TryCreate(GameObject source, Player sourcePlayer, BoneWindow window)
    {
        try
        {
            var puppet = new GhostPuppet(source, sourcePlayer, window);
            return puppet;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"Puppet creation failed, falling back to silhouette: {ex}");
            return null;
        }
    }

    private GhostPuppet(GameObject source, Player sourcePlayer, BoneWindow window)
    {
        _window = window;
        _source = source;
        _sourcePlayer = sourcePlayer;

        try
        {
            if (source == null || window == null)
                throw new System.ArgumentException("Puppet source and recorded bone window are required");
            _holder = new GameObject("KillcamPuppet");
            _holder.SetActive(false);
            _cloneRoot = PuppetVisualHierarchy.Create(source, _holder.transform).transform;

            _mapped = new Transform[window.Paths.Length];
            int found = 0;
            for (int i = 0; i < window.Paths.Length; i++)
            {
                _mapped[i] = _cloneRoot.Find(window.Paths[i]);
                if (_mapped[i] != null) found++;
            }
            Plugin.Instance.Logger.LogDebug($"Puppet: mapped {found}/{window.Paths.Length} bones");
            AttachWeaponMeshes();
            Apply(window.Times[0]);
            _holder.SetActive(true);
            HideSourceRenderers();
        }
        catch
        {
            // Includes partial source hiding and any partially built weapon copy.
            // The failed constructor never reaches the caller's _puppet field.
            Destroy();
            throw;
        }
    }

    // The weapon view (PlayerBones.WeaponRoot.Original) is not even a
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
        Transform pivot = null;
        try
        {
            var weaponRoot = _sourcePlayer?.PlayerBones?.WeaponRoot?.Original;
            if (weaponRoot == null || _window.WeaponPos == null) return;

            pivot = new GameObject("PuppetWeapon").transform;
            pivot.SetParent(_holder.transform, false);

            int copied = 0;
            var sourceRenderers = WeaponMeshCollector.Collect(weaponRoot, out bool usedInactiveFallback);
            for (int i = 0; i < sourceRenderers.Count; i++)
            {
                var renderer = sourceRenderers[i];
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

                WeaponMuzzle.Encapsulate(ref _weaponLocalBounds, ref _hasWeaponBounds, t, meshFilter.sharedMesh);
            }

            if (copied == 0)
            {
                // expected whenever nothing is held, e.g. right after an unarmed respawn
                Plugin.Instance.Logger.LogDebug("Puppet weapon: no weapon meshes in hands, showing the puppet unarmed");
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
            Plugin.Instance.Logger.LogDebug(
                $"Puppet weapon: copied {copied} meshes; track spread root={spreadRoot:F2}m anim={spreadAnim:F2}m -> using {(useAnim ? "anim" : "root")}"
                + (usedInactiveFallback ? " (inactive fallback)" : "")
                + (skinned > 0 ? $" ({skinned} skinned skipped)" : ""));
        }
        catch (System.Exception ex)
        {
            if (pivot != null)
            {
                pivot.gameObject.SetActive(false);
                Object.Destroy(pivot.gameObject);
            }
            _weaponPivot = null;
            _hasWeaponBounds = false;
            Plugin.Instance.Logger.LogError($"Puppet weapon attach failed: {ex}");
        }
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
                    _hiddenSourceLights.Add(light);
                    light.enabled = false;
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
                _hiddenSourceRenderers.Add(renderer);
                renderer.enabled = false;
            }
        }
    }

    public void Apply(float t)
    {
        if (_destroyed) return;
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

    // Muzzle pose for a shot flash, aimed along the victim's recorded view.
    public bool TryGetMuzzle(Quaternion aim, out Vector3 position, out Quaternion direction)
    {
        return WeaponMuzzle.TryGetMuzzle(_weaponPivot, _weaponLocalBounds, _hasWeaponBounds, aim, out position, out direction);
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
        if (_destroyed) return;
        _destroyed = true;
        // Stop rendering immediately; Unity's Destroy completes at frame end.
        try
        {
            if (_holder != null) _holder.SetActive(false);
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogWarning($"Puppet deactivation failed: {ex.Message}");
        }
        for (int i = 0; i < _hiddenSourceRenderers.Count; i++)
        {
            var renderer = _hiddenSourceRenderers[i];
            try
            {
                if (renderer != null) renderer.enabled = true;
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Logger.LogWarning($"Puppet source renderer restoration failed: {ex.Message}");
            }
        }
        _hiddenSourceRenderers.Clear();

        for (int i = 0; i < _hiddenSourceLights.Count; i++)
        {
            var light = _hiddenSourceLights[i];
            try
            {
                if (light != null) light.enabled = true;
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Logger.LogWarning($"Puppet source light restoration failed: {ex.Message}");
            }
        }
        _hiddenSourceLights.Clear();

        if (_holder != null)
            Object.Destroy(_holder);
    }
}
