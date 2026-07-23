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
// Hidden state is tracked per player so the cut to the live tail can restore
// everyone EXCEPT the killer (whose eyes the camera then follows).
public class LivePlayerHider
{
    private class HiddenSet
    {
        public Player Player;
        public readonly List<Renderer> Renderers = new();
        public readonly List<Light> Lights = new();
    }

    private readonly List<HiddenSet> _hidden = new();

    public void HideAll()
    {
        if (_hidden.Count > 0) return;

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null) return;

        int totalRenderers = 0;
        int totalLights = 0;

        var players = gameWorld.AllAlivePlayersList;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || player.IsYourPlayer || player.gameObject == null) continue;

            var set = new HiddenSet { Player = player };

            HideRenderers(set, player.gameObject.GetComponentsInChildren<Renderer>(false));
            HideLights(set, player.gameObject.GetComponentsInChildren<Light>(false));

            // The weapon currently held in hands is a separate view object created by
            // the hands controller under the WeaponRoot bone — it is NOT any SlotView's
            // model (a slot suppresses its on-body model while its item is in hands),
            // and empirically not a descendant of player.gameObject either. Weapon-mounted
            // flashlights live here too, which is why the beam kept floating.
            var weaponRoot = player.WeaponRoot?.Original;
            if (weaponRoot != null)
            {
                HideRenderers(set, weaponRoot.GetComponentsInChildren<Renderer>(false));
                HideLights(set, weaponRoot.GetComponentsInChildren<Light>(false));
            }

            // Equipment view models (weapon, chest rig, backpack, headwear, ...) are
            // pooled objects (AssetPoolObject) attached to a bone Transform per slot —
            // not guaranteed to be a descendant the sweep above reaches. PlayerBody.SlotViews
            // is the game's own authoritative list of every currently equipped view, each
            // exposing its Renderers directly — hide those explicitly too.
            var slotViews = player.PlayerBody?.SlotViews;
            if (slotViews != null)
            {
                foreach (var slot in slotViews.GetValuesEnumerator())
                {
                    var model = slot?.Model;
                    if (model == null) continue;
                    HideRenderers(set, slot.Renderers);
                    HideLights(set, model.GetComponentsInChildren<Light>(false));
                }
            }

            if (set.Renderers.Count > 0 || set.Lights.Count > 0)
            {
                _hidden.Add(set);
                totalRenderers += set.Renderers.Count;
                totalLights += set.Lights.Count;
            }
        }

        Plugin.Instance.Logger.LogInfo(
            $"[KillcamReplay] Hid {totalRenderers} renderers, {totalLights} lights of {_hidden.Count} live players");
    }

    private static void HideRenderers(HiddenSet set, Renderer[] renderers)
    {
        for (int j = 0; j < renderers.Length; j++)
        {
            var renderer = renderers[j];
            if (renderer != null && renderer.enabled)
            {
                renderer.enabled = false;
                set.Renderers.Add(renderer);
            }
        }
    }

    private static void HideLights(HiddenSet set, Light[] lights)
    {
        for (int j = 0; j < lights.Length; j++)
        {
            var light = lights[j];
            if (light != null && light.enabled)
            {
                light.enabled = false;
                set.Lights.Add(light);
            }
        }
    }

    public void RestoreAll()
    {
        for (int i = 0; i < _hidden.Count; i++)
            Restore(_hidden[i]);
        _hidden.Clear();
    }

    // Used at the cut to the live tail: everyone reappears except the player whose
    // eyes the camera is about to follow — his own model would clip into the view.
    public void RestoreAllExcept(Player keepHidden)
    {
        for (int i = _hidden.Count - 1; i >= 0; i--)
        {
            if (_hidden[i].Player == keepHidden) continue;
            Restore(_hidden[i]);
            _hidden.RemoveAt(i);
        }
    }

    private static void Restore(HiddenSet set)
    {
        for (int i = 0; i < set.Renderers.Count; i++)
        {
            var renderer = set.Renderers[i];
            if (renderer != null) // Unity-null when the object despawned meanwhile
                renderer.enabled = true;
        }
        for (int i = 0; i < set.Lights.Count; i++)
        {
            var light = set.Lights[i];
            if (light != null)
                light.enabled = true;
        }
    }
}
