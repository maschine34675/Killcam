using EFT;
using UnityEngine;

namespace KillcamReplay.Replay;

// Script-free mesh copy of a player's held weapon (same technique as the puppet's
// AttachWeaponMeshes) whose world pose is driven from that player's recorded
// weapon track — used to show the KILLER's weapon riding its recorded path in
// front of the replay camera, so you can see what he shot with.
public class WeaponGhost
{
    private readonly Transform _pivot;
    private float _muzzleLocalZ = 0.5f;
    private int _copied;

    public static WeaponGhost TryCreate(Player source)
    {
        try
        {
            var weaponRoot = source?.PlayerBones?.WeaponRoot?.Original;
            if (weaponRoot == null) return null;

            var ghost = new WeaponGhost(weaponRoot);
            if (ghost._copied == 0)
            {
                ghost.Destroy();
                return null;
            }
            Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Killer weapon ghost: copied {ghost._copied} meshes");
            return ghost;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"[KillcamReplay] Killer weapon ghost failed: {ex}");
            return null;
        }
    }

    private WeaponGhost(Transform weaponRoot)
    {
        _pivot = new GameObject("KillcamReplayKillerWeapon").transform;
        _pivot.gameObject.SetActive(false);

        var sourceRenderers = weaponRoot.GetComponentsInChildren<MeshRenderer>(false);
        for (int i = 0; i < sourceRenderers.Length; i++)
        {
            var renderer = sourceRenderers[i];
            if (renderer == null || !renderer.enabled) continue;
            var meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) continue;

            var go = new GameObject("KillerWeaponMesh");
            var t = go.transform;
            t.SetParent(_pivot, false);
            t.localPosition = weaponRoot.InverseTransformPoint(renderer.transform.position);
            t.localRotation = Quaternion.Inverse(weaponRoot.rotation) * renderer.transform.rotation;
            t.localScale = renderer.transform.lossyScale;

            go.AddComponent<MeshFilter>().sharedMesh = meshFilter.sharedMesh;
            var copy = go.AddComponent<MeshRenderer>();
            copy.sharedMaterials = renderer.sharedMaterials;
            copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _copied++;

            float partMaxZ = t.localPosition.z
                + (meshFilter.sharedMesh.bounds.center.z + meshFilter.sharedMesh.bounds.extents.z) * t.localScale.z;
            if (partMaxZ > _muzzleLocalZ)
                _muzzleLocalZ = partMaxZ;
        }
    }

    public void SetPose(Vector3 position, Quaternion rotation)
    {
        if (_pivot == null) return;
        _pivot.SetPositionAndRotation(position, rotation);
        if (!_pivot.gameObject.activeSelf)
            _pivot.gameObject.SetActive(true);
    }

    public void Hide()
    {
        if (_pivot != null && _pivot.gameObject.activeSelf)
            _pivot.gameObject.SetActive(false);
    }

    public bool TryGetMuzzle(out Vector3 position, out Quaternion direction)
    {
        position = default;
        direction = default;
        if (_pivot == null || !_pivot.gameObject.activeSelf) return false;
        position = _pivot.TransformPoint(new Vector3(0f, 0f, _muzzleLocalZ));
        direction = _pivot.rotation;
        return true;
    }

    public void Destroy()
    {
        if (_pivot != null)
            Object.Destroy(_pivot.gameObject);
    }
}
