using KillcamReplay.Recording;
using UnityEngine;

namespace KillcamReplay.Replay;

// Translucent primitive silhouette standing in for the recorded victim during
// playback: capsule body (height follows pose level, lies flat when prone),
// sphere head at the recorded eye position and a short bar showing the view
// direction. Built from colliderless primitives — touches no game systems.
public class ReplayGhost
{
    private readonly GameObject _root;
    private readonly Transform _body;
    private readonly Transform _head;
    private readonly Transform _aim;

    private static Material _material;

    public ReplayGhost()
    {
        _root = new GameObject("KillcamReplayGhost");

        _body = CreatePart(PrimitiveType.Capsule);
        _head = CreatePart(PrimitiveType.Sphere);
        _head.localScale = new Vector3(0.28f, 0.28f, 0.28f);
        _aim = CreatePart(PrimitiveType.Cube);
        _aim.localScale = new Vector3(0.045f, 0.045f, 0.55f);
    }

    private Transform CreatePart(PrimitiveType type)
    {
        var go = GameObject.CreatePrimitive(type);
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(_root.transform, false);

        var renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = GetMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go.transform;
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
                color = new Color(0.45f, 0.85f, 1f, 0.45f)
            };
            Plugin.Instance.Logger.LogDebug($"Ghost shader: {shader.name}");
        }
        return _material;
    }

    // Body proportions are estimated from the recorded eye position: EFT eye
    // height is ~1.6 m standing, ~0.95 m crouched (PoseLevel 0..1), ~0.35 m prone.
    public void UpdatePose(in Snapshot s)
    {
        Vector3 eye = s.CamPos;
        Quaternion yawRot = Quaternion.Euler(0f, s.Yaw, 0f);
        Vector3 forward = yawRot * Vector3.forward;

        if (s.Prone)
        {
            float ground = eye.y - 0.35f;
            _body.localScale = new Vector3(0.42f, 0.8f, 0.42f);
            _body.rotation = yawRot * Quaternion.Euler(90f, 0f, 0f);
            _body.position = new Vector3(eye.x, ground + 0.22f, eye.z) - forward * 0.55f;
        }
        else
        {
            float eyeHeight = Mathf.Lerp(0.95f, 1.6f, Mathf.Clamp01(s.PoseLevel));
            float bodyHeight = eyeHeight + 0.12f;
            float ground = eye.y - eyeHeight;
            _body.localScale = new Vector3(0.42f, bodyHeight / 2f, 0.42f);
            _body.rotation = yawRot;
            _body.position = new Vector3(eye.x, ground + bodyHeight / 2f, eye.z);
        }

        _head.position = eye;

        Quaternion viewRot = Quaternion.Euler(s.Pitch, s.Yaw, 0f);
        _aim.rotation = viewRot;
        _aim.position = eye + viewRot * Vector3.forward * 0.4f;
    }

    public void Destroy()
    {
        if (_root != null)
            Object.Destroy(_root);
    }
}
