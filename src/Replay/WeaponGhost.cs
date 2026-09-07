using EFT;
using UnityEngine;

namespace KillcamReplay.Replay;

// Script-free mesh copy of a player's held weapon (same technique as the puppet's
// AttachWeaponMeshes) shown in front of the replay camera, so you can see what the
// KILLER shot with.
//
// Its position rides the recorded weapon track, but its ORIENTATION comes from the
// killer's recorded aim. The track carries the world pose of the view container
// (PlayerBones.WeaponRoot.Original); for an observed player the aim is applied
// further down that chain, so the copy — a rigid snapshot of the meshes as they
// stood at capture time — stayed frozen in whatever animation pose the capture
// caught. Catching a reload left the weapon pointing at the ceiling for the whole
// replay. The fireport gives the true barrel frame at capture time and sits under
// the same weapon root, so it stays valid for the frozen copy and lets the pivot
// be rebuilt from the aim each frame.
public class WeaponGhost
{
    // beyond this the recorded position is not a weapon in someone's hands any more
    private const float MaxEyeDistance = 1.2f;

    private readonly Transform _pivot;
    private Bounds _localBounds;
    private bool _hasBounds;
    private Quaternion _aimToPivot = Quaternion.identity;
    private Vector3 _localShotDir;
    private bool _aimLocked;
    private bool _loggedAlignment;
    private int _copied;
    private bool _usedInactiveFallback;

    public static WeaponGhost TryCreate(Player source)
    {
        try
        {
            var weaponRoot = source?.PlayerBones?.WeaponRoot?.Original;
            if (weaponRoot == null) return null;

            var ghost = new WeaponGhost(source, weaponRoot);
            if (ghost._copied == 0)
            {
                ghost.Destroy();
                return null;
            }
            Plugin.Instance.Logger.LogInfo(
                $"Killer weapon ghost: copied {ghost._copied} meshes, "
                + (ghost._aimLocked ? "aim-locked via fireport" : "no fireport — falling back to the recorded rotation")
                + (ghost._usedInactiveFallback ? " (inactive fallback)" : ""));
            return ghost;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"Killer weapon ghost failed: {ex}");
            return null;
        }
    }

    private WeaponGhost(Player source, Transform weaponRoot)
    {
        _pivot = new GameObject("KillcamReplayKillerWeapon").transform;
        _pivot.gameObject.SetActive(false);

        var sourceRenderers = WeaponMeshCollector.Collect(weaponRoot, out _usedInactiveFallback);
        for (int i = 0; i < sourceRenderers.Count; i++)
        {
            var renderer = sourceRenderers[i];
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

            WeaponMuzzle.Encapsulate(ref _localBounds, ref _hasBounds, t, meshFilter.sharedMesh);
        }

        CaptureBarrel(source, weaponRoot);
    }

    // PlayerBones.FindFireport looks the fireport up UNDER WeaponRoot.Original, so
    // it is rigid with the meshes copied above and its frame stays valid for the
    // copy. EFT fires along Fireport.TransformDirection(LocalShotDirection).
    private void CaptureBarrel(Player source, Transform weaponRoot)
    {
        try
        {
            var fireport = source?.Fireport?.Original;
            if (fireport == null) return;

            Vector3 shotDir = fireport.forward;
            try
            {
                Vector3 local = source.LocalShotDirection;
                if (local.sqrMagnitude > 0.0001f)
                    shotDir = fireport.TransformDirection(local);
            }
            catch
            {
                // ProceduralWeaponAnimation is not guaranteed on an observed player
            }

            _localShotDir = weaponRoot.InverseTransformDirection(shotDir).normalized;
            if (_localShotDir.sqrMagnitude < 0.5f) return;

            Vector3 up = weaponRoot.InverseTransformDirection(fireport.up);
            if (Vector3.Cross(_localShotDir, up).sqrMagnitude < 0.0001f)
                up = Mathf.Abs(_localShotDir.y) < 0.9f ? Vector3.up : Vector3.forward;

            _aimToPivot = Quaternion.Inverse(Quaternion.LookRotation(_localShotDir, up));
            _aimLocked = true;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogWarning($"Killer weapon ghost: barrel capture failed ({ex.Message})");
        }
    }

    public void SetPose(Vector3 position, Quaternion recordedRotation, Quaternion aim, Vector3 eyePosition)
    {
        if (_pivot == null) return;

        Quaternion rotation = _aimLocked ? aim * _aimToPivot : recordedRotation;

        if (_aimLocked && !_loggedAlignment)
        {
            _loggedAlignment = true;
            Plugin.Instance.Logger.LogInfo(
                $"Killer weapon ghost: recorded rotation was {Vector3.Angle(recordedRotation * _localShotDir, aim * Vector3.forward):F0}° off the aim");
        }

        // the weapon is in his hands; anything further out is a stale track
        Vector3 offset = position - eyePosition;
        if (offset.sqrMagnitude > MaxEyeDistance * MaxEyeDistance)
            offset = offset.normalized * MaxEyeDistance;

        _pivot.SetPositionAndRotation(eyePosition + offset, rotation);
        if (!_pivot.gameObject.activeSelf)
            _pivot.gameObject.SetActive(true);
    }

    public void Hide()
    {
        if (_pivot != null && _pivot.gameObject.activeSelf)
            _pivot.gameObject.SetActive(false);
    }

    // Muzzle pose for a shot flash, aimed along the killer's recorded view.
    public bool TryGetMuzzle(Quaternion aim, out Vector3 position, out Quaternion direction)
    {
        return WeaponMuzzle.TryGetMuzzle(_pivot, _localBounds, _hasBounds, aim, out position, out direction);
    }

    public void Destroy()
    {
        if (_pivot != null)
            Object.Destroy(_pivot.gameObject);
    }
}
