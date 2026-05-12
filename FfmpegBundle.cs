using System.Reflection;

namespace CiMeRecorderGui;

public static class FfmpegBundle
{
    private const string ResourceName = "ffmpeg.exe";

    public static string EnsureExtracted()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CiMeRecorder");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "ffmpeg.exe");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            var fallback = @"C:\ffmpeg\bin\ffmpeg.exe";
            if (File.Exists(fallback)) return fallback;
            return "ffmpeg.exe";
        }

        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }
}
