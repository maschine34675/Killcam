using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Killcam.Interop;
using Killcam.Patches;
using Killcam.Recording;
using Killcam.Replay;
using UnityEngine;

namespace Killcam;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInIncompatibility("com.maschine.KillcamReplay")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.maschine.Killcam";
    public const string PluginName = "maschine-Killcam";
    public const string PluginVersion = "1.0.0";

    public static Plugin Instance;

    public new ManualLogSource Logger => base.Logger;

    public static ConfigEntry<int> SampleRate;
    public static ConfigEntry<float> BufferSeconds;
    public static ConfigEntry<float> ReplayDuration;
    public static ConfigEntry<float> LiveTailSeconds;
    public static ConfigEntry<bool> FollowKillerInTail;
    public static ConfigEntry<bool> DeathReplay;
    public static ConfigEntry<bool> ShowVictimGhost;
    public static ConfigEntry<bool> UseRealModelGhost;
    public static ConfigEntry<bool> HideLiveBots;
    public static ConfigEntry<bool> InvertPitch;
    public static ConfigEntry<float> HudTopFraction;
    public static ConfigEntry<bool> ShowShotLines;
    public static ConfigEntry<bool> ShowKillerWeapon;
    public static ConfigEntry<float> SlowMoSeconds;
    public static ConfigEntry<float> SlowMoFactor;
    public static ConfigEntry<bool> WaitForReplayBeforeRespawn;
    public static ConfigEntry<KeyboardShortcut> SkipReplayKey;
    public static ConfigEntry<KeyboardShortcut> DebugReplayKey;

    public static SnapshotRecorder Recorder;
    public static ReplayController ReplayCtrl;

    // Internal: the interop type is internal by design (copied source, one per mod).
    internal static LocalBodyTracker RespawnTracker;

    // Whether a death plays the replay. Kept as a property: ArenaMode reads it by
    // reflection (ArenaMode.Client/Spectator/KillcamCompat.cs).
    public static bool DeathReplayActive => DeathReplay.Value;

    public void Awake()
    {
        Instance = this;

        // BepInEx has opened the new GUID's ConfigFile, but has not saved it yet.
        // Reload the copied settings before the first Bind can write defaults.
        try
        {
            if (ConfigMigration.CopyLegacyIfMissing(Config.ConfigFilePath))
            {
                Config.Reload();
                Logger.LogInfo("Killcam imported the existing KillcamReplay settings; the original file was retained.");
            }
        }
        catch (System.Exception ex)
        {
            // Do not bind/save defaults over a copied file that could not be read.
            enabled = false;
            Logger.LogError($"Killcam is disabled because settings migration failed. The original settings were retained; check config permissions and restart: {ex.Message}");
            return;
        }

        SampleRate = Config.Bind("Recording", "SampleRate", 20,
            new ConfigDescription("Recording rate in Hz", new AcceptableValueRange<int>(5, 60)));
        BufferSeconds = Config.Bind("Recording", "BufferSeconds", 15f,
            new ConfigDescription("Ring buffer length in seconds", new AcceptableValueRange<float>(5f, 60f)));
        ReplayDuration = Config.Bind("Replay", "Duration", 10f,
            new ConfigDescription("Replay length in seconds", new AcceptableValueRange<float>(2f, 12f)));
        LiveTailSeconds = Config.Bind("Replay", "LiveTailSeconds", 5f,
            new ConfigDescription("Keep showing the live view for this long after the replay ends (with a short fade transition). Arena raids use half this duration.", new AcceptableValueRange<float>(0f, 10f)));
        FollowKillerInTail = Config.Bind("Replay", "FollowKillerInTail", true,
            "Follow the killer live during the live tail instead of staying at the last position");
        DeathReplay = Config.Bind("Replay", "DeathReplay", true,
            "Play the replay when you die");
        ShowVictimGhost = Config.Bind("Replay", "ShowVictimGhost", true,
            "Show your own silhouette (ghost) in the replay");
        UseRealModelGhost = Config.Bind("Replay", "UseRealModelGhost", true,
            "Use the real character model (corpse clone with recorded bone poses) instead of the silhouette; automatically falls back to the silhouette on errors");
        HideLiveBots = Config.Bind("Replay", "HideLiveBots", true,
            "Hide live bots during the replay (otherwise they walk through the shot)");
        InvertPitch = Config.Bind("Replay", "InvertPitch", false,
            "Invert vertical look direction in the replay (in case the camera tilts the wrong way)");
        HudTopFraction = Config.Bind("Replay", "HudTopFraction", 0.87f,
            new ConfigDescription("Vertical position of the killcam HUD as a fraction of screen height from the top (place it below/above the vanilla death panel)", new AcceptableValueRange<float>(0f, 0.9f)));
        ShowShotLines = Config.Bind("Replay", "ShowShotLines", true,
            "Show a line for every recorded shot, from the shooter's muzzle to where his aim met a surface, plus a brief muzzle light (the killer's shots in red, your own in cyan)");
        ShowKillerWeapon = Config.Bind("Replay", "ShowKillerWeapon", true,
            "Show a mesh copy of the killer's weapon riding its recorded path in front of the camera");
        SlowMoSeconds = Config.Bind("Replay", "SlowMoSeconds", 1.5f,
            new ConfigDescription("Play the last N seconds before the kill in slow motion (0 = off)", new AcceptableValueRange<float>(0f, 5f)));
        SlowMoFactor = Config.Bind("Replay", "SlowMoFactor", 0.35f,
            new ConfigDescription("Playback speed during the slow-motion window", new AcceptableValueRange<float>(0.1f, 1f)));
        WaitForReplayBeforeRespawn = Config.Bind("Replay", "WaitForReplayBeforeRespawn", true,
            "When a respawn mod (Redeploy) respawns you while your death replay is still running, wait until the replay has finished: it fades to black, then you respawn. Off: the respawn cuts the replay. ArenaMode raids keep their own timing.");
        SkipReplayKey = Config.Bind("Replay", "SkipKey", new KeyboardShortcut(KeyCode.Space),
            "Skip the running replay. A waiting respawn or raid end then follows right away behind a short fade to black.");
        DebugReplayKey = Config.Bind("Debug", "ReplayKey", new KeyboardShortcut(KeyCode.F9),
            "Play a replay of the nearest bot (for testing without dying)");

        Recorder = gameObject.AddComponent<SnapshotRecorder>();
        ReplayCtrl = gameObject.AddComponent<ReplayController>();

        // RespawnApi v1 of maschine-Redeploy.Core (Redeploy docs/RESPAWN-API.md, section 7),
        // through the copied interop file: bound by reflection when Core is installed,
        // MainPlayer polling otherwise. A replacement of the replay's victim ends the replay
        // in the frame it happens, while the old camera still exists.
        RespawnTracker = gameObject.AddComponent<LocalBodyTracker>();
        RespawnTracker.Log = message => Logger.LogInfo("[Respawn] " + message);
        RespawnTracker.BodyChanged += (previous, current, reason) => ReplayCtrl.NotifyBodyReplaced(previous, current, reason);

        ReplayPatches.Enable();
        HoldRespawnPatch.TryApply();   // retried at every raid start if Core loads later

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded");
    }
}
