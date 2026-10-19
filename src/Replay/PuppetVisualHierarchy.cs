using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Killcam.Replay;

// Copies appearance, never a native player/item component. Inactive Instantiate
// still runs serialization callbacks; constructing this allowlist avoids them.
internal static class PuppetVisualHierarchy
{
    internal static GameObject Create(GameObject source, Transform holder)
    {
        if (source == null || holder == null)
            throw new ArgumentException("Puppet source and holder are required");
        if (holder.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Puppet must be assembled under an inactive holder");

        GameObject root = null;
        try
        {
            root = new GameObject(source.name);
            root.SetActive(false);
            var transforms = new Dictionary<Transform, Transform>();
            CopyTransforms(source.transform, root.transform, holder, transforms, true);
            var renderers = new Dictionary<Renderer, Renderer>();
            foreach (var original in source.GetComponentsInChildren<Renderer>(true))
            {
                Renderer copy;
                var target = transforms[original.transform].gameObject;
                if (original is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                {
                    var skinned = target.AddComponent<SkinnedMeshRenderer>();
                    copy = skinned;
                    skinned.sharedMesh = skin.sharedMesh;
                    var bones = skin.bones;
                    var mappedBones = new Transform[bones.Length];
                    for (int i = 0; i < bones.Length; i++)
                        mappedBones[i] = MapBone(bones[i], transforms);
                    skinned.bones = mappedBones;
                    skinned.rootBone = MapBone(skin.rootBone, transforms);
                    skinned.localBounds = skin.localBounds;
                    skinned.quality = skin.quality;
                    skinned.updateWhenOffscreen = skin.updateWhenOffscreen;
                    skinned.skinnedMotionVectors = skin.skinnedMotionVectors;
                    for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++)
                        skinned.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
                }
                else if (original is MeshRenderer && original.GetComponent<MeshFilter>() is { } filter
                         && filter.sharedMesh != null)
                {
                    target.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    copy = target.AddComponent<MeshRenderer>();
                }
                else
                {
                    // Particle, trail and line renderers need their own simulation;
                    // no such live effect is part of the recorded body geometry.
                    continue;
                }
                CopyAppearance(original, copy, transforms);
                renderers.Add(original, copy);
            }
            if (renderers.Count == 0)
                throw new InvalidOperationException("Puppet source has no supported mesh renderers");
            CopyLods(source, transforms, renderers);
            return root;
        }
        catch
        {
            if (root != null)
            {
                root.SetActive(false);
                UnityEngine.Object.Destroy(root);
            }
            throw;
        }
    }

    private static void CopyTransforms(Transform source, Transform target, Transform parent,
        Dictionary<Transform, Transform> transforms, bool root)
    {
        transforms.Add(source, target);
        target.gameObject.layer = source.gameObject.layer;
        target.SetParent(parent, false);
        if (root)
        {
            // The holder is a new world-space identity object. Preserve a source
            // beneath a scaled/rotated native parent without retaining that parent.
            target.SetPositionAndRotation(source.position, source.rotation);
            target.localScale = source.lossyScale;
        }
        else
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
        }
        target.gameObject.SetActive(source.gameObject.activeSelf);
        for (int i = 0; i < source.childCount; i++)
        {
            Transform child = source.GetChild(i);
            var copy = new GameObject(child.name);
            // Attach before any later property access can fail, so root owns cleanup.
            copy.transform.SetParent(target, false);
            CopyTransforms(child, copy.transform, target, transforms, false);
        }
    }

    private static Transform MapBone(Transform bone, Dictionary<Transform, Transform> transforms)
    {
        if (bone == null) return null;
        if (transforms.TryGetValue(bone, out var copy)) return copy;
        // An external native bone would animate our mesh with the living world and
        // could disappear on a respawn. Fall back to the silhouette instead.
        throw new InvalidOperationException("Puppet skin references a bone outside its source: " + bone.name);
    }

    private static void CopyAppearance(Renderer source, Renderer target,
        Dictionary<Transform, Transform> transforms)
    {
        target.sharedMaterials = source.sharedMaterials;
        target.enabled = source.enabled;
        target.forceRenderingOff = source.forceRenderingOff;
        // EFT's first-person body hides head/torso using ShadowsOnly. The replay
        // must show those meshes; other original shadow modes remain unchanged.
        target.shadowCastingMode = source.shadowCastingMode == ShadowCastingMode.ShadowsOnly
            ? ShadowCastingMode.On : source.shadowCastingMode;
        target.receiveShadows = source.receiveShadows;
        target.lightProbeUsage = source.lightProbeUsage;
        target.reflectionProbeUsage = source.reflectionProbeUsage;
        target.motionVectorGenerationMode = source.motionVectorGenerationMode;
        target.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        target.renderingLayerMask = source.renderingLayerMask;
        target.sortingLayerID = source.sortingLayerID;
        target.sortingOrder = source.sortingOrder;
        // Internal anchors follow recorded bones; external scene anchors are not
        // retained as native-body lifetime dependencies.
        target.probeAnchor = source.probeAnchor != null && transforms.TryGetValue(source.probeAnchor, out var anchor)
            ? anchor : null;
        var properties = new MaterialPropertyBlock();
        source.GetPropertyBlock(properties);
        target.SetPropertyBlock(properties.isEmpty ? null : properties);
        for (int i = 0; i < source.sharedMaterials.Length; i++)
        {
            properties.Clear();
            source.GetPropertyBlock(properties, i);
            // An absent material override must not mask the renderer-wide block.
            target.SetPropertyBlock(properties.isEmpty ? null : properties, i);
        }
    }

    private static void CopyLods(GameObject source, Dictionary<Transform, Transform> transforms,
        Dictionary<Renderer, Renderer> renderers)
    {
        foreach (var original in source.GetComponentsInChildren<LODGroup>(true))
        {
            var copy = transforms[original.transform].gameObject.AddComponent<LODGroup>();
            copy.enabled = false;
            var sourceLods = original.GetLODs();
            var lods = new LOD[sourceLods.Length];
            for (int level = 0; level < lods.Length; level++)
            {
                var mapped = new List<Renderer>();
                foreach (var renderer in sourceLods[level].renderers ?? Array.Empty<Renderer>())
                    if (renderer != null && renderers.TryGetValue(renderer, out var target))
                        mapped.Add(target);
                lods[level] = new LOD(sourceLods[level].screenRelativeTransitionHeight, mapped.ToArray())
                {
                    fadeTransitionWidth = sourceLods[level].fadeTransitionWidth
                };
            }
            copy.SetLODs(lods);
            copy.localReferencePoint = original.localReferencePoint;
            copy.size = original.size;
            copy.fadeMode = original.fadeMode;
            copy.animateCrossFading = original.animateCrossFading;
            copy.enabled = original.enabled;
        }
    }
}
