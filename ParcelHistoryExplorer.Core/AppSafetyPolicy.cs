namespace ParcelHistoryExplorer.Core;

public static class AppSafetyPolicy
{
    public static bool PersistentCacheEnabled => ReadBoolean("PHE_ALLOW_PERSISTENT_CACHE", defaultValue: true);

    public static bool ExternalProcessLaunchEnabled => ReadBoolean("PHE_ALLOW_EXTERNAL_TOOLS");

    public static bool NotepadPlusPlusLaunchEnabled =>
        ExternalProcessLaunchEnabled || ReadBoolean("PHE_ALLOW_NOTEPADPP", defaultValue: true);

    private static bool ReadBoolean(string name, bool defaultValue = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            _ => false
        };
    }
}
