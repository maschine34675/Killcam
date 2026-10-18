using System;
using System.IO;

namespace Killcam;

internal static class ConfigMigration
{
    internal const string CurrentFile = "com.maschine.Killcam.cfg";
    internal const string LegacyFile = "com.maschine.KillcamReplay.cfg";

    internal static bool CopyLegacyIfMissing(string currentPath)
    {
        if (!string.Equals(Path.GetFileName(currentPath), CurrentFile, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unexpected Killcam config destination.", nameof(currentPath));
        // A new config always wins; never merge or overwrite explicit preferences.
        if (File.Exists(currentPath)) return false;
        string legacyPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(currentPath))!, LegacyFile);
        if (!File.Exists(legacyPath)) return false;
        File.Copy(legacyPath, currentPath, overwrite: false);
        return true;
    }
}
