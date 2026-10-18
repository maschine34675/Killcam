using EFT;
using UnityEngine;

namespace Killcam.Replay;

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
// be rebuilt from the aim each frame. Without such a frame the weapon is not shown
// at all — orienting it by the recorded track alone is what left it pointing at
// the ceiling.
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
    private string _rejectReason = "unknown";
    private bool _loggedAlignment;
    private int _copied;
    private bool _usedInactiveFallback;

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

            // Without a barrel frame the copy could only be oriented by the recorded
            // track, which does not carry the aim. Showing nothing is better: the
            // shot flash then uses its camera-relative position, which is oriented
            // by the aim and always right.
            if (!ghost._aimLocked)
            {
                Plugin.Instance.Logger.LogInfo(
                    $"Killer weapon ghost: {ghost._copied} meshes but no usable barrel frame ({ghost._rejectReason}) — not showing the weapon");
                ghost.Destroy();
                return null;
            }

            Plugin.Instance.Logger.LogDebug(
                $"Killer weapon ghost: copied {ghost._copied} meshes, aim-locked via fireport"
                + (ghost._usedInactiveFallback ? " (inactive fallback)" : ""));
            return ghost;
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Logger.LogError($"Killer weapon ghost failed: {ex}");
            return null;
        }
    }

    private WeaponGhost(Transform weaponRoot)
    {
        _pivot = new GameObject("KillcamKillerWeapon").transform;
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

        CaptureBarrel(weaponRoot);
    }

    // The fireport is looked up under THIS weapon root instead of being taken from
    // Player.Fireport. That one is cached when a weapon is equipped and still
    // points at the previous weapon — or at nothing — while its owner is
    // holstering, drawing or has just dropped it, which is precisely when the copy
    // ended up in a strange pose. A fireport found under the root we just copied
    // belongs to these meshes by construction (PlayerBones.FindFireport does the
    // same lookup).
    //
    // EFT weapon rigs shoot along -Y with +Z up, not along Unity's forward: the
    // shot direction defaults to Vector3.down in fireport space
    // (ProceduralWeaponAnimation._shotDirection), CalculateShotDirectionForIron
    // returns (0, -1, zeroing tilt), and the line-of-sight code maps weapon-space
    // points to (-y, z) as (downrange, up). The fireport's +Z is therefore the
    // weapon's vertical — taking it as the barrel put the copy exactly 90° off.
    private void CaptureBarrel(Transform weaponRoot)
    {
        try
        {
            var fireport = TransformTools.FindTransformRecursive(weaponRoot, "fireport", ignoreCase: true);
            if (fireport == null)
            {
                _rejectReason = "no fireport under the weapon root";
                return;
            }

            _localShotDir = weaponRoot.InverseTransformDirection(-fireport.up).normalized;
            Vector3 localUp = weaponRoot.InverseTransformDirection(fireport.forward).normalized;
            if (_localShotDir.sqrMagnitude < 0.5f || localUp.sqrMagnitude < 0.5f)
            {
                _rejectReason = "degenerate fireport frame";
                return;
            }

            if (!MatchesGeometry(out string mismatch))
            {
                _rejectReason = mismatch;
                return;
            }

            // barrel onto the aim's forward, weapon up onto the aim's up
            _aimToPivot = Quaternion.Inverse(Quaternion.LookRotation(_localShotDir, localUp));
            _aimLocked = true;
        }
        catch (System.Exception ex)
        {
            _rejectReason = ex.Message;
            Plugin.Instance.Logger.LogWarning($"Killer weapon ghost: barrel capture failed ({ex.Message})");
        }
    }

    // A firearm is long along its barrel, so when an elongated copy's long axis
    // disagrees with the fireport, that fireport does not describe these meshes —
    // a pooled view picked up by the inactive fallback, or a hierarchy caught
    // mid-swap. Compact copies (a pistol is nearly as tall as it is long) carry no
    // such signal and are left alone.
    private bool MatchesGeometry(out string mismatch)
    {
        mismatch = null;
        if (!_hasBounds) return true;

        Vector3 extents = _localBounds.extents;
        int longest = (extents.x >= extents.y && extents.x >= extents.z) ? 0 : (extents.y >= extents.z ? 1 : 2);
        float second = 0f;
        for (int i = 0; i < 3; i++)
        {
            if (i != longest && extents[i] > second)
                second = extents[i];
        }
        if (extents[longest] < second * 1.5f) return true;

        var axis = Vector3.zero;
        axis[longest] = 1f;
        float angle = Vector3.Angle(_localShotDir, axis);
        angle = Mathf.Min(angle, 180f - angle);
        if (angle <= 50f) return true;

        mismatch = $"fireport {angle:F0}° off the long mesh axis";
        return false;
    }

    public void SetPose(Vector3 position, Quaternion recordedRotation, Quaternion aim, Vector3 eyePosition)
    {
        if (_pivot == null) return;

        Quaternion rotation = aim * _aimToPivot;

        if (!_loggedAlignment)
        {
            _loggedAlignment = true;
            Plugin.Instance.Logger.LogDebug(
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
