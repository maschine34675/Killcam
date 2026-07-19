using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KillcamReplay.Replay;

// Hides the renderers of all live players/bots during playback so present-time
// actors don't walk through a scene that shows the past. Purely visual: only
// Renderer.enabled is toggled, AI and game logic keep running. The local player
// is never hidden (dead → not in the list anyway; F9 → own live body marks the
// path end, like the corpse does in a death replay).
public class LivePlayerHider
{
    private readonly List<Renderer> _hidden = new();

    public void HideAll()
    {
        if (_hidden.Count > 0) return;

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null) return;

        var players = gameWorld.AllAlivePlayersList;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || player.IsYourPlayer || player.gameObject == null) continue;

            var renderers = player.gameObject.GetComponentsInChildren<Renderer>(false);
            for (int j = 0; j < renderers.Length; j++)
            {
                var renderer = renderers[j];
                if (renderer != null && renderer.enabled)
                {
                    renderer.enabled = false;
                    _hidden.Add(renderer);
                }
            }
        }

        Plugin.Instance.Logger.LogInfo($"[KillcamReplay] Hid {_hidden.Count} renderers of live players");
    }

    public void RestoreAll()
    {
        for (int i = 0; i < _hidden.Count; i++)
        {
            var renderer = _hidden[i];
            if (renderer != null) // Unity-null when the bot despawned meanwhile
                renderer.enabled = true;
        }
        _hidden.Clear();
    }
}
