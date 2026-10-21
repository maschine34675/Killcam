using BepInEx;
using BepInEx.Logging;
using Killcam.Interop;
using Killcam.Patches;
using Killcam.Recording;
using Killcam.Replay;

namespace Killcam;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInIncompatibility("com.maschine.KillcamReplay")]
public partial class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.maschine.Killcam";
    public const string PluginName = "maschine-Killcam";
    public const string PluginVersion = "1.0.0";

    public static Plugin Instance;

    public new ManualLogSource Logger => base.Logger;

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

        BindSettings(Config);

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
