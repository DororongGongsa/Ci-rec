namespace CiMeRecorderGui;

public static class AppPaths
{
    public static string ConfigDirectory
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CiMeRecorder");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string ConfigPath => Path.Combine(ConfigDirectory, "recorder-config.json");

    public static string LogDirectory
    {
        get
        {
            var directory = Path.Combine(ConfigDirectory, "logs");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string ExecutableDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(directory)) return directory;
            }

            return AppContext.BaseDirectory;
        }
    }
}
