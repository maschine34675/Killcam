using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KillcamReplay.Replay;

// Hides the renderers AND light sources of all live players/bots during playback
// so present-time actors don't walk through a scene that shows the past. Purely
// visual: only Renderer.enabled/Light.enabled are toggled, AI and game logic keep
// running. The local player is never hidden (dead → not in the list anyway; F9 →
// own live body marks the path end, like the corpse does in a death replay).
public class LivePlayerHider
{
    private readonly List<Renderer> _hiddenRenderers = new();
    private readonly List<Light> _hiddenLights = new();

    public void HideAll()
    {
        if (_hiddenRenderers.Count > 0 || _hiddenLights.Count > 0) return;

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null) return;

        var players = gameWorld.AllAlivePlayersList;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || player.IsYourPlayer || player.gameObject == null) continue;

            HideRenderers(player.gameObject.GetComponentsInChildren<Renderer>(false));
            HideLights(player.gameObject.GetComponentsInChildren<Light>(false));

            // Equipment view models (weapon, chest rig, backpack, headwear, ...) are
            // pooled objects (AssetPoolObject) attached to a bone Transform per slot —
            // not guaranteed to be a descendant the sweep above reaches. PlayerBody.SlotViews
            // is the game's own authoritative list of every currently equipped view, each
            // exposing its Renderers directly — hide those explicitly too. A flashlight is a
            // UnityEngine.Light on the same view, not caught by hiding the mesh alone, so its
            // beam is swept separately per slot.
            var slotViews = player.PlayerBody?.SlotViews;
            if (slotViews != null)
            {
                foreach (var slot in slotViews.GetValuesEnumerator())
                {
                    var model = slot?.Model;
                    if (model == null) continue;
                    HideRenderers(slot.Renderers);
                    HideLights(model.GetComponentsInChildren<Light>(false));
                }
            }
        }

        Plugin.Instance.Logger.LogInfo(
            $"[KillcamReplay] Hid {_hiddenRenderers.Count} renderers, {_hiddenLights.Count} lights of live players");
    }

    private void HideRenderers(Renderer[] renderers)
    {
        for (int j = 0; j < renderers.Length; j++)
        {
            var renderer = renderers[j];
            if (renderer != null && renderer.enabled)
            {
                renderer.enabled = false;
                _hiddenRenderers.Add(renderer);
            }
        }
    }

    private void HideLights(Light[] lights)
    {
        for (int j = 0; j < lights.Length; j++)
        {
            var light = lights[j];
            if (light != null && light.enabled)
            {
                light.enabled = false;
                _hiddenLights.Add(light);
            }
        }
    }

    public void RestoreAll()
    {
        for (int i = 0; i < _hiddenRenderers.Count; i++)
        {
            var renderer = _hiddenRenderers[i];
            if (renderer != null) // Unity-null when the bot despawned meanwhile
                renderer.enabled = true;
        }
        _hiddenRenderers.Clear();

        for (int i = 0; i < _hiddenLights.Count; i++)
        {
            var light = _hiddenLights[i];
            if (light != null)
                light.enabled = true;
        }
        _hiddenLights.Clear();
    }
}
