using System.Collections.Generic;
using UnityEngine;

namespace KillcamReplay.Replay;

// Shot lines at recorded shot times — red for the killer, cyan for the victim's own
// shots, so the two directions of fire never read alike. Each shot draws a straight
// line from the shooter's muzzle to the point his recorded aim met: the first wall,
// floor or terrain along the ray from his eyes, the same point a zeroed sight
// converges on.
// A line stays where its shot was fired and fades out over LineSeconds of REPLAY
// time, so slow motion stretches it along with everything else; rapid fire shows
// several at once.
//
// The aim is the recorded view, not the bullet's actual path: spread, recoil and
// the 20 Hz sampling are not in it, and bodies are not in the ray's mask. A line
// shows where the shooter was pointing when he fired, not a confirmed hit.
public class ShotTracers
{
    private const float LineSeconds = 0.4f;
    private const float FadeSeconds = 0.25f;
    // Longer than any map's diagonal, so a line only stops short when its aim met
    // nothing at all (sky). The main camera clips at 10 km (CameraManager), far
    // beyond this; the single raycast per shot costs the same either way.
    private const float MaxRange = 2000f;
    // A LineRenderer's width is in world units. Scaling it with the distance to the
    // camera keeps the line about 2.5 px wide at 1080p and 60° FOV, at the muzzle
    // and 50 m out alike.
    private const float WidthPerMetre = 0.0027f;
    private const float MinWidthDistance = 0.3f;
    private const int PoolSize = 6;

    public static readonly Color KillerColor = new(1f, 0.08f, 0.05f, 0.9f);
    public static readonly Color VictimColor = new(0.05f, 0.9f, 1f, 0.9f);

    private static Material _material;
    private static bool _vertexColored;

    private readonly GameObject _root;
    private readonly Color _color;
    private readonly Material _ownMaterial;
    private readonly LineRenderer[] _lines = new LineRenderer[PoolSize];
    private readonly AnimationCurve[] _widths = new AnimationCurve[PoolSize];
    private readonly int[] _shot = new int[PoolSize];
    private readonly Vector3[] _from = new Vector3[PoolSize];
    private readonly Vector3[] _to = new Vector3[PoolSize];

    public static ShotTracers TryCreate(string name, Color color)
    {
        var material = GetMaterial();
        return material != null ? new ShotTracers(name, color, material) : null;
    }

    private ShotTracers(string name, Color color, Material material)
    {
        _root = new GameObject(name);
        _color = color;

        // a fallback shader without vertex colours takes the colour from the material
        if (!_vertexColored)
        {
            _ownMaterial = new Material(material) { color = color };
            material = _ownMaterial;
        }

        for (int i = 0; i < PoolSize; i++)
        {
            var go = new GameObject("ShotLine");
            go.transform.SetParent(_root.transform, false);

            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;

            _lines[i] = line;
            _widths[i] = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.5f, 0f), new Keyframe(1f, 0f));
            _shot[i] = -1;
        }
    }

    private static Material GetMaterial()
    {
        if (_material != null) return _material;

        // unlit and vertex-coloured, so a line keeps its colour in any light and can fade
        Shader shader = Shader.Find("Sprites/Default")
            ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Legacy Shaders/Transparent/Diffuse");
        if (shader == null)
        {
            Plugin.Instance.Logger.LogWarning("Shot lines: no usable shader found, lines disabled");
            return null;
        }

        _vertexColored = shader.name is "Sprites/Default" or "Legacy Shaders/Particles/Alpha Blended";
        _material = new Material(shader) { color = Color.white };
        Plugin.Instance.Logger.LogDebug($"Shot line shader: {shader.name}");
        return _material;
    }

    // Called every replay frame. The shooter arguments describe the shooter at t and
    // are only read when a shot starts; a shot that starts while `shooterValid` is
    // false draws no line.
    public void Update(List<float> shots, float t, bool shooterValid,
        Vector3 muzzle, Vector3 eye, Quaternion aim, Vector3 cameraPos)
    {
        if (shots == null)
        {
            Hide();
            return;
        }

        for (int i = 0; i < PoolSize; i++)
        {
            if (_shot[i] < 0) continue;
            float age = t - shots[_shot[i]];
            if (age < 0f || age >= LineSeconds)
                Release(i);
        }

        if (shooterValid)
        {
            for (int s = 0; s < shots.Count; s++)
            {
                float age = t - shots[s];
                if (age < 0f || age >= LineSeconds || IsShown(s)) continue;

                int slot = FreeSlot();
                if (slot < 0) break;
                Fire(slot, s, muzzle, eye, aim);
            }
        }

        for (int i = 0; i < PoolSize; i++)
        {
            if (_shot[i] >= 0)
                Refresh(i, t - shots[_shot[i]], cameraPos);
        }
    }

    private void Fire(int slot, int shotIndex, Vector3 muzzle, Vector3 eye, Quaternion aim)
    {
        Vector3 direction = aim * Vector3.forward;
        Vector3 target = Physics.Raycast(eye, direction, out var hit, MaxRange,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)
            ? hit.point
            : eye + direction * MaxRange;

        _shot[slot] = shotIndex;
        _from[slot] = muzzle;
        _to[slot] = target;

        var line = _lines[slot];
        line.SetPosition(0, muzzle);
        line.SetPosition(1, target);
        line.enabled = true;
    }

    private void Refresh(int slot, float age, Vector3 cameraPos)
    {
        var line = _lines[slot];

        if (_vertexColored)
        {
            float fade = Mathf.Clamp01((LineSeconds - age) / FadeSeconds);
            var color = _color;
            color.a *= fade;
            line.startColor = color;
            line.endColor = color;
        }

        // Width at both ends and at the point nearest the camera: a line fired toward
        // the camera would otherwise swell where it passes it. Linear in between.
        Vector3 from = _from[slot];
        Vector3 segment = _to[slot] - from;
        float lengthSq = segment.sqrMagnitude;
        float mid = lengthSq > 0.000001f
            ? Mathf.Clamp(Vector3.Dot(cameraPos - from, segment) / lengthSq, 0.02f, 0.98f)
            : 0.5f;

        float w0 = Width(from, cameraPos);
        float w1 = Width(from + segment * mid, cameraPos);
        float w2 = Width(_to[slot], cameraPos);
        float slopeIn = (w1 - w0) / mid;
        float slopeOut = (w2 - w1) / (1f - mid);

        var curve = _widths[slot];
        curve.MoveKey(0, new Keyframe(0f, w0, slopeIn, slopeIn));
        curve.MoveKey(1, new Keyframe(mid, w1, slopeIn, slopeOut));
        curve.MoveKey(2, new Keyframe(1f, w2, slopeOut, slopeOut));
        line.widthCurve = curve;
    }

    private static float Width(Vector3 point, Vector3 cameraPos)
    {
        return Mathf.Max(Vector3.Distance(point, cameraPos), MinWidthDistance) * WidthPerMetre;
    }

    private bool IsShown(int shotIndex)
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (_shot[i] == shotIndex) return true;
        }
        return false;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (_shot[i] < 0) return i;
        }
        return -1;
    }

    private void Release(int slot)
    {
        _shot[slot] = -1;
        _lines[slot].enabled = false;
    }

    public void Hide()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (_shot[i] >= 0)
                Release(i);
        }
    }

    public void Destroy()
    {
        if (_root != null)
            Object.Destroy(_root);
        if (_ownMaterial != null)
            Object.Destroy(_ownMaterial);
    }
}
