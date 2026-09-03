using System.Reflection;
using BepInEx.Bootstrap;

namespace KillcamReplay;

// Soft compatibility with the ArenaMode mod (no compile-time dependency).
//
// Most arena modes respawn the player instantly, and a replay would then play
// over a live body already standing somewhere else — so those still suppress it
// entirely. But a mode where death COSTS something, and the player waits out the
// round, is the one place a replay has room. ArenaMode says which it is by
// answering ArenaSession.ReplaySeconds: 0 to suppress, otherwise how many
// seconds it wants, which is shorter than the setting a normal raid uses.
//
// The type and property names are the contract. Mirrors the RedeployCompat
// pattern from the Killcam mod.
public static class ArenaCompat
{
    private const string ArenaGuid = "com.maschine.ArenaMode";
    private const string SessionTypeName = "ArenaMode.Session.ArenaSession";

    private static bool _resolved;
    private static PropertyInfo _isActive;
    private static PropertyInfo _replaySeconds;

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

    /// <summary>
    /// Seconds of replay the running arena mode wants, or 0 for none.
    ///
    /// Also 0 when ArenaMode is an older build without the property: suppressing
    /// is what it asked for before this existed, so an unresolved lookup lands on
    /// the behaviour that mod is expecting.
    /// </summary>
    public static float ArenaReplaySeconds()
    {
        try
        {
            EnsureResolved();
            if (_replaySeconds == null)
                return 0f;

            return (float)_replaySeconds.GetValue(null);
        }
        catch
        {
            return 0f;
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
        _replaySeconds = sessionType.GetProperty("ReplaySeconds", BindingFlags.Public | BindingFlags.Static);

        Plugin.Instance.Logger.LogInfo(_replaySeconds != null
            ? "ArenaMode detected — death replay follows whatever the running arena mode asks for"
            : "ArenaMode detected (no ReplaySeconds) — death replay suppressed during arena sessions");
    }
}
