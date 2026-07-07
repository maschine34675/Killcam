using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KillcamReplay.Patches;
using KillcamReplay.Recording;
using KillcamReplay.Replay;
using UnityEngine;

namespace KillcamReplay;

[BepInPlugin("com.local.killcamreplay", "Killcam Replay", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance;

    public new ManualLogSource Logger => base.Logger;

    public static ConfigEntry<int> SampleRate;
    public static ConfigEntry<float> BufferSeconds;
    public static ConfigEntry<float> ReplayDuration;
    public static ConfigEntry<bool> DeathReplay;
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
        DeathReplay = Config.Bind("Replay", "DeathReplay", true,
            "Replay beim eigenen Tod abspielen");
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
