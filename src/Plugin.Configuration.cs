using BepInEx.Configuration;
using UnityEngine;

namespace Killcam;

public partial class Plugin
{
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

    // Stored keys stay stable; ConfigurationManager reads the labels from tags.
    // Kept separate from Awake so the actual bindings can be checked without EFT.
    internal static void BindSettings(ConfigFile config)
    {
        DeathReplay = config.Bind("Replay", "DeathReplay", true,
            Describe("Replay on death", "Play the replay when you die. Recording continues when this is disabled.", 150));
        SkipReplayKey = config.Bind("Replay", "SkipKey", new KeyboardShortcut(KeyCode.Space),
            Describe("Skip replay shortcut", "Skip the running replay. A waiting respawn or raid end then follows behind a short fade to black.", 140));
        ReplayDuration = config.Bind("Replay", "Duration", 10f,
            Describe("Replay duration (seconds)", "Length of the recorded replay. ArenaMode can select its own duration.", 130, new AcceptableValueRange<float>(2f, 12f)));
        LiveTailSeconds = config.Bind("Replay", "LiveTailSeconds", 5f,
            Describe("Live view duration (seconds)", "Keep showing the live view after the recording ends. Set to 0 to disable. Arena raids use half this duration.", 120, new AcceptableValueRange<float>(0f, 10f)));
        FollowKillerInTail = config.Bind("Replay", "FollowKillerInTail", true,
            Describe("Follow killer in live view", "Follow the killer during the live view instead of holding the last recorded position.", 110));
        SlowMoSeconds = config.Bind("Replay", "SlowMoSeconds", 1.5f,
            Describe("Slow motion duration (seconds)", "Play this many recorded seconds before the kill in slow motion. Set to 0 to disable.", 100, new AcceptableValueRange<float>(0f, 5f)));
        SlowMoFactor = config.Bind("Replay", "SlowMoFactor", 0.35f,
            Describe("Slow motion speed", "Playback speed during slow motion. 1 is normal speed; 0.35 is 35% speed.", 90, new AcceptableValueRange<float>(0.1f, 1f)));
        WaitForReplayBeforeRespawn = config.Bind("Replay", "WaitForReplayBeforeRespawn", true,
            Describe("Finish replay before respawn", "Let a compatible Redeploy respawn wait for the replay and fade to black. When disabled, the respawn cuts the replay short. ArenaMode manages its own timing.", 80));
        ShowVictimGhost = config.Bind("Replay", "ShowVictimGhost", true,
            Describe("Show victim", "Show your recorded character or a silhouette during the replay.", 70));
        UseRealModelGhost = config.Bind("Replay", "UseRealModelGhost", true,
            Describe("Use victim character model", "Rebuild the victim's visual model with recorded poses. Use a silhouette instead when disabled or when the model is unavailable.", 60));
        HideLiveBots = config.Bind("Replay", "HideLiveBots", true,
            Describe("Hide live characters", "Hide live characters during the recorded replay so they do not walk through the scene. AI continues running.", 50));
        ShowShotLines = config.Bind("Replay", "ShowShotLines", true,
            Describe("Show shot lines", "Show recorded aim lines and brief muzzle lights: killer shots in red, your own in cyan. These are not confirmed hit paths.", 40));
        ShowKillerWeapon = config.Bind("Replay", "ShowKillerWeapon", true,
            Describe("Show killer weapon", "Show a visual copy of the killer's weapon following its recorded path.", 30));
        HudTopFraction = config.Bind("Replay", "HudTopFraction", 0.87f,
            Describe("Overlay vertical position", "Position measured from the top of the screen. 0.87 places the overlay at 87% of the screen height.", 20, new AcceptableValueRange<float>(0f, 0.9f)));
        InvertPitch = config.Bind("Replay", "InvertPitch", false,
            Describe("Invert vertical camera direction", "Enable only if the replay camera tilts in the wrong vertical direction.", 10));

        SampleRate = config.Bind("Recording", "SampleRate", 20,
            Describe("Recording rate (Hz)", "Snapshots recorded per second. Higher values use more resources. Applies at the next raid start.", 20, new AcceptableValueRange<int>(5, 60)));
        BufferSeconds = config.Bind("Recording", "BufferSeconds", 15f,
            Describe("Recording history (seconds)", "History kept per character. Keep this at least as long as the replay duration. Applies at the next raid start.", 10, new AcceptableValueRange<float>(5f, 60f)));

        // Deliberately do not bind/migrate the old Debug/ReplayKey (default F9).
        // A user's explicit binding to the new key survives later launches.
        DebugReplayKey = config.Bind("Debug", "DebugReplayHotkey", new KeyboardShortcut(KeyCode.None),
            Describe("Debug replay shortcut", "Optional test shortcut: replay the nearest recorded bot without dying, with no live view afterward. Unbound by default. The old ReplayKey binding is ignored.", 10, advanced: true));
    }

    private static ConfigDescription Describe(string name, string description, int order,
        AcceptableValueBase range = null, bool advanced = false) =>
        new ConfigDescription(description, range, new ConfigurationManagerAttributes
        {
            DispName = name,
            Order = order,
            IsAdvanced = advanced,
            ShowRangeAsPercent = false
        });
}

// ConfigurationManager discovers this tag by type name and public fields.
internal sealed class ConfigurationManagerAttributes
{
    public string DispName;
    public int? Order;
    public bool? IsAdvanced;
    public bool? ShowRangeAsPercent;
}
