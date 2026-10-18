using System.Collections.Generic;
using UnityEngine;

namespace Killcam.Replay;

// Picks the mesh renderers worth copying from a weapon view hierarchy.
//
// Bot weapon views are LOD-managed: at distance only the low-poly LOD renderer is
// enabled, so a naive "copy enabled renderers" sweep clones a low-res weapon — or
// a single part ("copied 1 meshes"). For every LODGroup the LOD0 (highest detail)
// renderers are taken regardless of their current enabled state; renderers outside
// any LODGroup keep the enabled filter. If the active-only pass finds nothing
// (e.g. the view was returned to the pool inactive after a respawn equipment
// swap), a second pass including inactive objects is tried.
public static class WeaponMeshCollector
{
    public static List<MeshRenderer> Collect(Transform weaponRoot, out bool usedInactiveFallback)
    {
        usedInactiveFallback = false;
        var result = CollectPass(weaponRoot, false);
        if (result.Count == 0)
        {
            result = CollectPass(weaponRoot, true);
            usedInactiveFallback = result.Count > 0;
        }
        return result;
    }

    private static List<MeshRenderer> CollectPass(Transform weaponRoot, bool includeInactive)
    {
        var lodManaged = new HashSet<Renderer>();
        var lod0 = new HashSet<Renderer>();

        foreach (var group in weaponRoot.GetComponentsInChildren<LODGroup>(includeInactive))
        {
            if (group == null) continue;
            var lods = group.GetLODs();
            for (int level = 0; level < lods.Length; level++)
            {
                var renderers = lods[level].renderers;
                if (renderers == null) continue;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null) continue;
                    lodManaged.Add(renderer);
                    if (level == 0)
                        lod0.Add(renderer);
                }
            }
        }

        var result = new List<MeshRenderer>();
        foreach (var renderer in weaponRoot.GetComponentsInChildren<MeshRenderer>(includeInactive))
        {
            if (renderer == null) continue;
            bool wanted = lodManaged.Contains(renderer)
                ? lod0.Contains(renderer)
                : (renderer.enabled || includeInactive);
            if (wanted)
                result.Add(renderer);
        }
        return result;
    }
}
