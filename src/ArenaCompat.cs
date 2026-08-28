using System.Reflection;
using BepInEx.Bootstrap;

namespace KillcamReplay;

// Soft compatibility with the ArenaMode mod (no compile-time dependency).
// In an arena session the player respawns instantly on death — a death replay
// would play over the freshly respawned, live player standing defenseless.
// Skip the replay entirely while an arena session is active; normal raids are
// unaffected. Mirrors the RedeployCompat pattern from the Killcam mod.
public static class ArenaCompat
{
    private const string ArenaGuid = "com.maschine.ArenaMode";
    private const string SessionTypeName = "ArenaMode.Session.ArenaSession";

    private static bool _resolved;
    private static PropertyInfo _isActive;

    public static bool IsArenaActive()
    {
        try
        {
            EnsureResolved();
            return _isActive != null && (bool)_isActive.GetValue(null);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureResolved()
    {
        if (_resolved) return;
        _resolved = true;

        if (!Chainloader.PluginInfos.TryGetValue(ArenaGuid, out var info) || info.Instance == null)
            return;

        var sessionType = info.Instance.GetType().Assembly.GetType(SessionTypeName);
        if (sessionType == null)
        {
            Plugin.Instance.Logger.LogWarning($"ArenaMode detected but {SessionTypeName} not found — replay runs in arena too");
            return;
        }

        _isActive = sessionType.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Static);
        Plugin.Instance.Logger.LogInfo("ArenaMode detected — death replay suppressed during arena sessions");
    }
}
