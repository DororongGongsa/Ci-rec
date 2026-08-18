using System.Text.Json.Serialization;

namespace CiMeRecorderGui;

public sealed class AppConfig
{
    public int CheckIntervalSeconds { get; set; } = 30;
    public string OutputDirectory { get; set; } = "recordings";
    public string FfmpegPath { get; set; } = "";
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    public string Referer { get; set; } = "https://ci.me/";
    public string Quality { get; set; } = "1080p";
    public bool DarkMode { get; set; }
    public List<ChannelConfig> Channels { get; set; } = new();
}

public sealed class ChannelConfig
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string Quality { get; set; } = "default";
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? ChannelName.FromUrl(Url) : Name.Trim();
}

public sealed class VariantInfo
{
    public string Url { get; set; } = "";
    public int Bandwidth { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string Label
    {
        get
        {
            if (Height > 0) return Height + "p";
            if (Bandwidth > 0) return Math.Round(Bandwidth / 1000d) + " kbps";
            return "source";
        }
    }
}

public enum ChannelState
{
    Offline,
    Checking,
    Recording,
    Finalizing,
    Failed,
    Disabled
}

public sealed class VodDownloadProgress
{
    public string Url { get; set; } = "";
    public string State { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public int? Percent { get; set; }
    public long? Bytes { get; set; }
    public string Message { get; set; } = "";
}

public static class ChannelName
{
    public static string FromUrl(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url, "@([^/]+)/live", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Safe(uri.AbsolutePath.Trim('/'));
        return "channel";
    }

    public static string Safe(string value)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(value, "[^A-Za-z0-9_.-]", "_");
        return string.IsNullOrWhiteSpace(safe) ? "channel" : safe;
    }

    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\s+", " ").Trim();
        safe = safe.Trim('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "recording" : safe;
    }
}
