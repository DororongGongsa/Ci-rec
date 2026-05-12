namespace CiMeRecorderGui;

public static class AppLogger
{
    private static readonly object Sync = new();

    public static void Write(string line)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogDirectory, DateTime.Now.ToString("yyyyMMdd") + ".log");
            lock (Sync)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
