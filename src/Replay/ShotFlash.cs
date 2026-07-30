using UnityEngine;

namespace KillcamReplay.Replay;

// A small reusable muzzle-flash stand-in: a point light plus a glowing blob
// stretched along the shot direction (Unity has no cone primitive; an elongated
// ellipsoid reads the same), shown for a few frames at a recorded shot's
// position. No particles, no audio, no game systems.
public class ShotFlash
{
    private static Material _material;

    private readonly GameObject _root;

    public ShotFlash(float blobWidth = 0.06f, float blobLength = 0.22f)
    {
        _root = new GameObject("KillcamReplayShotFlash");

        var light = _root.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.75f, 0.35f);
        light.intensity = 3.5f;
        light.range = 14f;
        light.shadows = LightShadows.None;

        var blob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.Destroy(blob.GetComponent<Collider>());
        blob.transform.SetParent(_root.transform, false);
        blob.transform.localScale = new Vector3(blobWidth, blobWidth, blobLength);
        blob.transform.localPosition = new Vector3(0f, 0f, blobLength * 0.5f);

        var renderer = blob.GetComponent<Renderer>();
        renderer.sharedMaterial = GetMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        _root.SetActive(false);
    }

    private static Material GetMaterial()
    {
        if (_material == null)
        {
            Shader shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Standard");
            _material = new Material(shader)
            {
                color = new Color(1f, 0.72f, 0.25f, 0.9f)
            };
        }
        return _material;
    }

    public void ShowAt(Vector3 position, Quaternion direction)
    {
        if (_root == null) return;
        _root.transform.SetPositionAndRotation(position, direction);
        if (!_root.activeSelf)
            _root.SetActive(true);
    }

    public void Hide()
    {
        if (_root != null && _root.activeSelf)
            _root.SetActive(false);
    }

    public void Destroy()
    {
        if (_root != null)
            Object.Destroy(_root);
    }
}
