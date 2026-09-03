using UnityEngine;

namespace KillcamReplay.Replay;

// Shared muzzle estimate for the copied weapon meshes of the puppet and of the
// killer's weapon ghost.
//
// The pivot those meshes hang under carries the recorded world pose of
// PlayerBones.WeaponRoot.Original — a scene-level view container the game aligns
// to the hands — or of Weapon_Root_Anim, and neither transform's local axes are
// guaranteed to run along the barrel; the two need not even share a convention,
// so which one a replay picked could change the result. Reading a muzzle out of
// the pivot's own +Z therefore pointed the shot flash in an arbitrary direction.
//
// Nothing here reads a pivot axis. The shooter's recorded aim supplies the
// direction — it is the same data the replay camera flies, which reproduces what
// the shooter was looking at — and the distance is the weapon's own silhouette
// extent along exactly that aim: the support point of its parts' bounds.
public static class WeaponMuzzle
{
    private const float FallbackDistance = 0.45f;
    private const float MinDistance = 0.15f;
    private const float MaxDistance = 1.2f;

    // Grows `bounds` (in pivot space) to cover one copied weapon part.
    public static void Encapsulate(ref Bounds bounds, ref bool any, Transform part, Mesh mesh)
    {
        Bounds local = mesh.bounds;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? local.min.x : local.max.x,
                (i & 2) == 0 ? local.min.y : local.max.y,
                (i & 4) == 0 ? local.min.z : local.max.z);
            Vector3 inPivot = part.localPosition + part.localRotation * Vector3.Scale(corner, part.localScale);

            if (any)
            {
                bounds.Encapsulate(inPivot);
            }
            else
            {
                bounds = new Bounds(inPivot, Vector3.zero);
                any = true;
            }
        }
    }

    public static bool TryGetMuzzle(Transform pivot, Bounds bounds, bool hasBounds, Quaternion aim,
        out Vector3 position, out Quaternion direction)
    {
        position = default;
        direction = default;
        if (pivot == null || !pivot.gameObject.activeSelf) return false;

        Vector3 forward = aim * Vector3.forward;
        float distance = FallbackDistance;

        if (hasBounds)
        {
            // support point of the parts' AABB along the aim, in pivot space
            Vector3 local = pivot.InverseTransformDirection(forward);
            distance = Vector3.Dot(bounds.center, local)
                + Vector3.Dot(bounds.extents, new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z)));
        }

        position = pivot.position + forward * Mathf.Clamp(distance, MinDistance, MaxDistance);
        direction = aim;
        return true;
    }
}
