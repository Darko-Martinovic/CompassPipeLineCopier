namespace PipelineCopier;

public static class CompassFolder
{
    /// <summary>
    /// Where Compass keeps saved aggregation pipelines, one JSON file each. Compass is an Electron
    /// app, so this is Electron's userData folder: %APPDATA% on Windows, ~/Library/Application Support
    /// on macOS and $XDG_CONFIG_HOME (or ~/.config) on Linux.
    /// </summary>
    public static string SavedPipelines()
    {
        var appData = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return Path.Combine(appData, "MongoDB Compass", "SavedPipelines");
    }
}
