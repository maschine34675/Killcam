using System.Reflection;
using BepInEx.Configuration;
using Killcam;
using UnityEngine;

var root = Path.Combine(Path.GetTempPath(), "KillcamConfig-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
ConfigFile Open(string name, string contents = null)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, ConfigMigration.CurrentFile);
    if (contents != null) File.WriteAllText(path, contents);
    var config = new ConfigFile(path, false);
    Plugin.BindSettings(config);
    return config;
}
void CheckUnbound(ConfigFile config)
{
    Check(Plugin.DebugReplayKey.Value.MainKey == KeyCode.None, "Debug shortcut must be unbound");
    Check(!Plugin.DebugReplayKey.Value.Modifiers.Any(), "Unbound shortcut must have no modifiers");
    Check(!config.ContainsKey(new ConfigDefinition("Debug", "ReplayKey")), "Old debug key must not be an active setting");
    Check(Plugin.DebugReplayKey.Definition.Key == "DebugReplayHotkey", "Expected new debug setting key");
}

// First prove the old key really loads F9 in the same BepInEx parser.
var controlPath = Path.Combine(root, "control.cfg");
File.WriteAllText(controlPath, "[Debug]\nReplayKey = F9\n");
var control = new ConfigFile(controlPath, false);
Check(control.Bind("Debug", "ReplayKey", new KeyboardShortcut(KeyCode.None)).Value.MainKey == KeyCode.F9,
    "Positive control: the old F9 binding must parse, not silently fall back to None");

var fresh = Open("fresh");
CheckUnbound(fresh);
Check(Plugin.SkipReplayKey.Value.MainKey == KeyCode.Space, "Skip shortcut remains Space");
Check(fresh.Count == 18, "All existing settings remain available");

// Exercise the installed menu reader, not a reimplementation of its tag handling.
var menuType = typeof(ConfigurationManager.ConfigurationManager).Assembly.GetType("ConfigurationManager.ConfigSettingEntry", true);
foreach (var pair in fresh)
{
    var entry = pair.Value;
    var menuEntry = Activator.CreateInstance(menuType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null, new object[] { entry, null }, null);
    object Get(string property) => menuType.GetProperty(property).GetValue(menuEntry);
    Check(Get("DispName") is string label && label.Contains(' ') && label != entry.Definition.Key,
        $"Readable name not consumed by ConfigurationManager: {entry.Definition}");
    Check(Get("Order") is int order && order > 0, $"Missing menu order: {entry.Definition}");
    Check(Equals(Get("IsAdvanced"), entry == Plugin.DebugReplayKey), $"Wrong advanced visibility: {entry.Definition}");
    Check(Equals(Get("ShowRangeAsPercent"), false), $"Ranges must keep their displayed units: {entry.Definition}");
}

const string oldSettings = "[Debug]\nReplayKey = F9\n[Replay]\nDuration = 7\nDeathReplay = false\nLiveTailSeconds = 1.25\nSkipKey = K\nSlowMoFactor = 0.6\n[Recording]\nSampleRate = 30\nBufferSeconds = 21\n";
void CheckPreferences()
{
    Check(Plugin.ReplayDuration.Value == 7 && !Plugin.DeathReplay.Value, "Existing replay settings were reset");
    Check(Plugin.LiveTailSeconds.Value == 1.25f && Plugin.SlowMoFactor.Value == 0.6f, "Existing timing was reset");
    Check(Plugin.SkipReplayKey.Value.MainKey == KeyCode.K, "Existing skip shortcut was reset");
    Check(Plugin.SampleRate.Value == 30 && Plugin.BufferSeconds.Value == 21, "Existing recording settings were reset");
}
var upgraded = Open("upgrade", oldSettings);
CheckUnbound(upgraded);
CheckPreferences();
upgraded.Save();
upgraded = Open("upgrade");
CheckUnbound(upgraded);
CheckPreferences();

// A deliberate new binding remains user-owned, even while the old F9 entry exists.
Plugin.DebugReplayKey.Value = new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl);
upgraded.Save();
upgraded = Open("upgrade");
Check(Plugin.DebugReplayKey.Value.MainKey == KeyCode.F8 && Plugin.DebugReplayKey.Value.Modifiers.SequenceEqual(new[] { KeyCode.LeftControl }),
    "Explicit new binding did not survive save/reopen");
CheckPreferences();
Plugin.DebugReplayKey.Value = new KeyboardShortcut(KeyCode.None);
upgraded.Save();
upgraded = Open("upgrade");
CheckUnbound(upgraded);

// Also cover users upgrading directly from KillcamReplay and custom old keys.
var legacyFolder = Path.Combine(root, "legacy");
Directory.CreateDirectory(legacyFolder);
var legacyPath = Path.Combine(legacyFolder, ConfigMigration.LegacyFile);
File.WriteAllText(legacyPath, oldSettings.Replace("ReplayKey = F9", "ReplayKey = F10"));
Check(ConfigMigration.CopyLegacyIfMissing(Path.Combine(legacyFolder, ConfigMigration.CurrentFile)), "Legacy migration did not copy");
var migrated = Open("legacy");
CheckUnbound(migrated);
CheckPreferences();
Check(File.ReadAllText(legacyPath).Contains("ReplayKey = F10"), "Legacy file must stay untouched");

Console.WriteLine($"KILLCAM_CONFIG_CHECKS_PASSED checks={checks}; fixtures: {root}");
