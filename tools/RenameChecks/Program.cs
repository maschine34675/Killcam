using Killcam;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
string root = Path.Combine(Path.GetTempPath(), "KillcamMigration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string current = Path.Combine(root, ConfigMigration.CurrentFile);
string legacy = Path.Combine(root, ConfigMigration.LegacyFile);
const string saved = "[Replay]\r\nDuration = 7\r\nDeathReplay = false\r\nLiveTailSeconds = 1.25\r\n";
Check(!ConfigMigration.CopyLegacyIfMissing(current), "clean install does not invent settings");
Check(!File.Exists(current), "clean install leaves Config.Bind as first writer");
File.WriteAllText(legacy, saved);
Check(ConfigMigration.CopyLegacyIfMissing(current), "legacy-only install imports");
Check(File.ReadAllText(current) == saved, "values copied exactly, not replaced by defaults");
Check(File.ReadAllText(legacy) == saved, "legacy retained as backup");
Check(!ConfigMigration.CopyLegacyIfMissing(current), "migration is idempotent");
File.WriteAllText(current, "[Replay]\nDuration = 3\n");
Check(!ConfigMigration.CopyLegacyIfMissing(current), "new preferences win");
Check(File.ReadAllText(current).Contains("Duration = 3"), "new preferences are not overwritten");
File.Delete(current);
using (var locked = File.Open(legacy, FileMode.Open, FileAccess.Read, FileShare.None))
{
    bool failed = false;
    try { ConfigMigration.CopyLegacyIfMissing(current); } catch (IOException) { failed = true; }
    Check(failed, "unreadable legacy config reports failure instead of claiming import");
}
Check(File.ReadAllText(legacy) == saved, "failed import retains original values");
bool rejected = false;
try { ConfigMigration.CopyLegacyIfMissing(Path.Combine(root, "another-mod.cfg")); }
catch (ArgumentException) { rejected = true; }
Check(rejected, "reject accidental wrong destination");
Check(!File.Exists(Path.Combine(root, "another-mod.cfg")), "foreign config is untouched");
Console.WriteLine($"KILLCAM_CONFIG_MIGRATION_PASSED ({checks}); fixtures: {root}");
