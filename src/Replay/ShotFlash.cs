using UnityEngine;

namespace Killcam.Replay;

// A brief muzzle light at a recorded shot's muzzle: a point light shown for a few
// frames, lighting up the surroundings the way a real muzzle flash does. The shot
// itself is drawn by ShotTracers. No particles, no audio, no game systems.
public class ShotFlash
{
    private readonly GameObject _root;

    public ShotFlash()
    {
        _root = new GameObject("KillcamShotFlash");

        var light = _root.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.75f, 0.35f);
        light.intensity = 3.5f;
        light.range = 14f;
        light.shadows = LightShadows.None;

        _root.SetActive(false);
    }

    public void ShowAt(Vector3 position)
    {
        if (_root == null) return;
        _root.transform.position = position;
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
