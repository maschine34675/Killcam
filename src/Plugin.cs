using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KillcamReplay.Patches;
using KillcamReplay.Recording;
using KillcamReplay.Replay;
using UnityEngine;

namespace KillcamReplay;

[BepInPlugin("com.local.killcamreplay", "Killcam Replay", "0.4.1")]
public class Plugin : BaseUnityPlugin
{
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
    public static ConfigEntry<KeyboardShortcut> DebugReplayKey;

    public static SnapshotRecorder Recorder;
    public static ReplayController ReplayCtrl;

    // The KillAndDamageInfo live killcam also owns the death sequence (deferred
    // Stop + camera swap). Two owners fight over the camera, so the death replay
    // yields when that mod is loaded; the debug hotkey still works.
    public static bool DeathReplayActive =>
        DeathReplay.Value
        && !BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.local.killanddamageinfo");

    public void Awake()
    {
        Instance = this;

        SampleRate = Config.Bind("Recording", "SampleRate", 20,
            new ConfigDescription("Aufnahmerate in Hz", new AcceptableValueRange<int>(5, 60)));
        BufferSeconds = Config.Bind("Recording", "BufferSeconds", 15f,
            new ConfigDescription("Länge des Ringpuffers in Sekunden", new AcceptableValueRange<float>(5f, 60f)));
        ReplayDuration = Config.Bind("Replay", "Duration", 5f,
            new ConfigDescription("Replay-Länge in Sekunden", new AcceptableValueRange<float>(2f, 12f)));
        LiveTailSeconds = Config.Bind("Replay", "LiveTailSeconds", 2f,
            new ConfigDescription("Nach Replay-Ende noch so lange live weiterzeigen (Übergang wird kurz abgeblendet)", new AcceptableValueRange<float>(0f, 10f)));
        FollowKillerInTail = Config.Bind("Replay", "FollowKillerInTail", true,
            "Während des Live-Nachlaufs dem Killer live folgen statt an der letzten Position zu verharren");
        DeathReplay = Config.Bind("Replay", "DeathReplay", true,
            "Replay beim eigenen Tod abspielen");
        ShowVictimGhost = Config.Bind("Replay", "ShowVictimGhost", true,
            "Eigene Silhouette (Ghost) im Replay anzeigen");
        UseRealModelGhost = Config.Bind("Replay", "UseRealModelGhost", true,
            "Echtes Charaktermodell (Corpse-Klon mit aufgezeichneten Knochen-Posen) statt Silhouette; bei Fehlern automatischer Fallback auf die Silhouette");
        HideLiveBots = Config.Bind("Replay", "HideLiveBots", true,
            "Lebende Bots während des Replays ausblenden (sie laufen sonst durchs Bild)");
        InvertPitch = Config.Bind("Replay", "InvertPitch", false,
            "Vertikale Blickrichtung im Replay invertieren (falls die Kamera falsch herum kippt)");
        DebugReplayKey = Config.Bind("Debug", "ReplayKey", new KeyboardShortcut(KeyCode.F9),
            "Replay des nächstgelegenen Bots abspielen (zum Testen ohne zu sterben)");

        Recorder = gameObject.AddComponent<SnapshotRecorder>();
        ReplayCtrl = gameObject.AddComponent<ReplayController>();

        ReplayPatches.Enable();

        Logger.LogInfo("KillcamReplay loaded");
    }
}
