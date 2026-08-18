using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CiMeRecorderGui;

public sealed class ChannelStatusEventArgs : EventArgs
{
    public required ChannelConfig Channel { get; init; }
    public required ChannelState State { get; init; }
    public string Message { get; init; } = "";
    public bool? IsBroadcastOnline { get; init; }
    public DateTime? RecordingStartedAt { get; init; }
    public string? RecordingPath { get; init; }
    public int? FinalizeProgressPercent { get; init; }
}

public sealed class RecorderEngine
{
    private readonly string _root;
    private readonly Dictionary<string, RecordingSession> _active = new();
    private readonly HashSet<string> _completedChannels = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public event EventHandler<ChannelStatusEventArgs>? StatusChanged;
    public event EventHandler<string>? LogWritten;

    public RecorderEngine(string root)
    {
        _root = root;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start(AppConfig config)
    {
        if (IsRunning) return;
        _completedChannels.Clear();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => WatchAsync(config, _cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        var sessions = _active.Values.ToArray();
        _active.Clear();

        foreach (var session in sessions)
        {
            try
            {
                if (!session.Process.HasExited)
                {
                    StopFfmpeg(session.Process);
                }
                SetStatus(session.Channel, ChannelState.Finalizing, "finalizing", true, session.StartedAt, session.TempPath, 0);
                await Task.Run(() => FinalizeRecording(session));
                SetStatus(session.Channel, ChannelState.Offline, "offline", false, null);
            }
            catch
            {
            }
        }
    }

    public async Task DownloadCiMeVodAsync(AppConfig config, string vodUrl, IProgress<VodDownloadProgress> progress, CancellationToken token)
    {
        var channel = new ChannelConfig
        {
            Url = vodUrl,
            Name = ChannelName.FromUrl(vodUrl),
            Quality = "default",
            Enabled = true
        };

        progress.Report(new VodDownloadProgress { Url = vodUrl, State = "\uC900\uBE44\uC911", Percent = 0 });
        var page = await GetStringAsync(vodUrl, config, channel, token);
        var streamUrl = FindM3u8Url(page);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            throw new InvalidOperationException("VOD \uC2A4\uD2B8\uB9BC \uC8FC\uC18C\uB97C \uCC3E\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.");
        }

        var title = ExtractBroadcastTitle(page, channel);
        var chosenUrl = await SelectQualityAsync(streamUrl, config, channel, token);
        var totalSeconds = await GetPlaylistDurationSecondsAsync(chosenUrl, config, channel, token);

        var outputRoot = config.OutputDirectory;
        if (!Path.IsPathRooted(outputRoot)) outputRoot = Path.Combine(_root, outputRoot);
        Directory.CreateDirectory(outputRoot);

        var baseName = $"{DateTime.Now:yyMMdd}_{ChannelName.SafeFileName(title)}";
        var outputPath = UniquePath(Path.Combine(outputRoot, baseName + ".mp4"));
        var tempPath = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(outputPath) + ".part.mp4");

        var args = new List<string>
        {
            "-hide_banner",
            "-y",
            "-headers", $"User-Agent: {config.UserAgent}\r\nReferer: {GetReferer(config, channel)}\r\n",
            "-i", chosenUrl,
            "-c", "copy",
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            "-nostats",
            tempPath
        };

        progress.Report(new VodDownloadProgress { Url = vodUrl, State = "\uB2E4\uC6B4\uB85C\uB4DC\uC911", OutputPath = tempPath, Percent = 0, Bytes = FileSizeOrNull(tempPath) });
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = config.FfmpegPath,
            Arguments = JoinArgs(args),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process == null) throw new InvalidOperationException("ffmpeg\uB97C \uC2DC\uC791\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.");

        try
        {
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var lastProgressReport = DateTime.MinValue;
            var lastPercent = -1;
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                token.ThrowIfCancellationRequested();
                var percent = totalSeconds > 0 ? ParseProgressPercent(line, totalSeconds) : null;
                var now = DateTime.Now;
                var shouldReport = percent.HasValue
                    ? percent.Value != lastPercent || now - lastProgressReport >= TimeSpan.FromSeconds(1)
                    : now - lastProgressReport >= TimeSpan.FromSeconds(1);
                if (shouldReport)
                {
                    lastProgressReport = now;
                    lastPercent = percent ?? lastPercent;
                    progress.Report(new VodDownloadProgress
                    {
                        Url = vodUrl,
                        State = "\uB2E4\uC6B4\uB85C\uB4DC\uC911",
                        OutputPath = tempPath,
                        Percent = percent,
                        Bytes = FileSizeOrNull(tempPath)
                    });
                }
            }

            await process.WaitForExitAsync(token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(tempPath))
        {
            throw new InvalidOperationException("VOD \uB2E4\uC6B4\uB85C\uB4DC\uC5D0 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4. part \uD30C\uC77C\uC740 \uB0A8\uACA8\uB450\uC5C8\uC2B5\uB2C8\uB2E4.");
        }

        if (File.Exists(outputPath)) outputPath = UniquePath(outputPath);
        File.Move(tempPath, outputPath);
        progress.Report(new VodDownloadProgress { Url = vodUrl, State = "\uC644\uB8CC", OutputPath = outputPath, Percent = 100, Bytes = FileSizeOrNull(outputPath) });
    }
    private async Task WatchAsync(AppConfig config, CancellationToken token)
    {
        Log("monitor started");
        while (!token.IsCancellationRequested)
        {
            foreach (var channel in config.Channels.ToArray())
            {
                if (token.IsCancellationRequested) break;
                await CheckChannelAsync(config, channel, token);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, config.CheckIntervalSeconds)), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
        Log("monitor stopped");
    }

    private async Task CheckChannelAsync(AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        if (!channel.Enabled)
        {
            SetStatus(channel, ChannelState.Disabled, "disabled", false, null);
            return;
        }

        if (_completedChannels.Contains(channel.Url))
        {
            SetStatus(channel, ChannelState.Offline, "offline", false, null);
            return;
        }

        if (_active.TryGetValue(channel.Url, out var active))
        {
            if (!active.Process.HasExited)
            {
                SetStatus(channel, ChannelState.Recording, "recording", true, active.StartedAt, active.TempPath);
                return;
            }
            _active.Remove(channel.Url);
            SetStatus(channel, ChannelState.Finalizing, "finalizing", true, active.StartedAt, active.TempPath, 0);
            await Task.Run(() => FinalizeRecording(active), token);
            _completedChannels.Add(channel.Url);
            SetStatus(channel, ChannelState.Offline, "offline", false, null);
            Log("recording finished: " + channel.Url);
            return;
        }

        try
        {
            SetStatus(channel, ChannelState.Checking, "checking");
            var stream = await ResolveStreamAsync(config, channel, token);
            if (stream == null)
            {
                SetStatus(channel, ChannelState.Offline, "offline", false, null);
                return;
            }

            if (!await IsLivePlaylistAsync(stream.StreamUrl, config, channel, token))
            {
                SetStatus(channel, ChannelState.Offline, "offline", false, null);
                return;
            }

            var chosenUrl = await SelectQualityAsync(stream.StreamUrl, config, channel, token);
            var session = StartRecording(config, channel, chosenUrl, stream.Title);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (session.Process.HasExited)
            {
                if (File.Exists(session.TempPath))
                {
                    SetStatus(channel, ChannelState.Finalizing, "finalizing", true, session.StartedAt, session.TempPath, 0);
                    await Task.Run(() => FinalizeRecording(session), token);
                }
                SetStatus(channel, ChannelState.Offline, "offline", false, null);
                return;
            }

            _active[channel.Url] = session;
            SetStatus(channel, ChannelState.Recording, "recording", true, session.StartedAt, session.TempPath);
        }
        catch (Exception ex)
        {
            SetStatus(channel, ChannelState.Failed, ex.Message, null, null);
        }
    }

    private async Task<StreamInfo?> ResolveStreamAsync(AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        return await ResolveCiMeStreamAsync(config, channel, token);
    }

    private async Task<StreamInfo?> ResolveCiMeStreamAsync(AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        var page = await GetStringAsync(channel.Url, config, channel, token);
        var streamUrl = FindM3u8Url(page);
        return string.IsNullOrWhiteSpace(streamUrl)
            ? null
            : new StreamInfo(streamUrl, ExtractBroadcastTitle(page, channel));
    }

    private async Task<string> GetStringAsync(string url, AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", GetReferer(config, channel));
        using var response = await client.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    private bool IsLive(string text)
    {
        return text.Contains("\uC2A4\uD2B8\uB9AC\uBC0D \uC911") ||
            text.Contains("\uBA85 \uC2DC\uCCAD \uC911") ||
            text.Contains("watching", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsLivePlaylistAsync(string streamUrl, AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        try
        {
            var playlist = await GetStringAsync(streamUrl, config, channel, token);
            if (!playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return false;
            if (playlist.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)) return false;
            return playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) ||
                playlist.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string? FindM3u8Url(string text)
    {
        var decoded = Regex.Unescape(text).Replace("\\/", "/");
        decoded = WebUtility.HtmlDecode(decoded);
        var patterns = new[]
        {
            "https?://[^'\"\\s<>]+\\.m3u8[^'\"\\s<>]*",
            "https?%3A%2F%2F[^'\"\\s<>]+?\\.m3u8[^'\"\\s<>]*"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(decoded, pattern, RegexOptions.IgnoreCase);
            if (match.Success) return Uri.UnescapeDataString(match.Value);
        }
        return null;
    }

    private string ExtractBroadcastTitle(string text, ChannelConfig channel)
    {
        var decoded = WebUtility.HtmlDecode(Regex.Unescape(text).Replace("\\/", "/"));
        var patterns = new[]
        {
            "<meta\\s+property=[\"']og:title[\"']\\s+content=[\"']([^\"']+)[\"']",
            "<meta\\s+name=[\"']title[\"']\\s+content=[\"']([^\"']+)[\"']",
            "<title>(.*?)</title>"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(decoded, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) continue;
            var title = Regex.Replace(match.Groups[1].Value, "\\s+", " ").Trim();
            title = Regex.Replace(title, "\\s*[-|]\\s*ci\\.me\\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }

        return channel.EffectiveName;
    }

    private async Task<string> SelectQualityAsync(string masterUrl, AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        var quality = channel.Quality == "default" ? config.Quality : channel.Quality;
        if (quality == "best") return masterUrl;

        string playlist;
        try
        {
            playlist = await GetStringAsync(masterUrl, config, channel, token);
        }
        catch
        {
            return masterUrl;
        }

        var variants = ParseVariants(masterUrl, playlist);
        if (variants.Count == 0) return masterUrl;
        if (quality == "worst") return variants.OrderBy(v => v.Bandwidth).First().Url;
        if (quality.EndsWith("p") && int.TryParse(quality[..^1], out var target))
        {
            return variants
                .OrderBy(v => v.Height <= target ? 0 : 1)
                .ThenBy(v => v.Height <= target ? Math.Abs(target - v.Height) : v.Height)
                .ThenByDescending(v => v.Bandwidth)
                .First()
                .Url;
        }

        return masterUrl;
    }

    private async Task<double> GetPlaylistDurationSecondsAsync(string playlistUrl, AppConfig config, ChannelConfig channel, CancellationToken token)
    {
        try
        {
            var playlist = await GetStringAsync(playlistUrl, config, channel, token);
            var seconds = 0d;
            foreach (Match match in Regex.Matches(playlist, "#EXTINF:([0-9.]+)", RegexOptions.IgnoreCase))
            {
                if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    seconds += value;
                }
            }
            return seconds;
        }
        catch
        {
            return 0;
        }
    }

    private List<VariantInfo> ParseVariants(string masterUrl, string playlist)
    {
        var variants = new List<VariantInfo>();
        var lines = playlist.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
            var next = lines.Skip(i + 1).FirstOrDefault(line => !line.StartsWith("#") && !string.IsNullOrWhiteSpace(line));
            if (string.IsNullOrWhiteSpace(next)) continue;

            var info = new VariantInfo { Url = MakeAbsolute(masterUrl, next.Trim()) };
            var bw = Regex.Match(lines[i], "BANDWIDTH=(\\d+)");
            if (bw.Success) info.Bandwidth = int.Parse(bw.Groups[1].Value);
            var res = Regex.Match(lines[i], "RESOLUTION=(\\d+)x(\\d+)");
            if (res.Success)
            {
                info.Width = int.Parse(res.Groups[1].Value);
                info.Height = int.Parse(res.Groups[2].Value);
            }
            variants.Add(info);
        }
        return variants;
    }

    private string MakeAbsolute(string baseUrl, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)) return absolute.ToString();
        return new Uri(new Uri(baseUrl), url).ToString();
    }

    private string GetReferer(AppConfig config, ChannelConfig channel)
    {
        return config.Referer;
    }

    private RecordingSession StartRecording(AppConfig config, ChannelConfig channel, string streamUrl, string title)
    {
        var outputRoot = config.OutputDirectory;
        if (!Path.IsPathRooted(outputRoot)) outputRoot = Path.Combine(_root, outputRoot);
        Directory.CreateDirectory(outputRoot);

        var baseName = $"{DateTime.Now:yyMMdd}_{ChannelName.SafeFileName(title)}";
        var outputPath = UniquePath(Path.Combine(outputRoot, baseName + ".mp4"));
        var tempPath = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(outputPath) + ".part.ts");
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_at_eof", "1",
            "-reconnect_delay_max", "30",
            "-headers", $"User-Agent: {config.UserAgent}\r\nReferer: {GetReferer(config, channel)}\r\n",
            "-i", streamUrl,
            "-c", "copy",
            "-f", "mpegts",
            tempPath
        };

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = config.FfmpegPath,
            Arguments = JoinArgs(args),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        });

        if (process == null) throw new InvalidOperationException("ffmpeg failed to start");
        Log("recording start: " + tempPath);
        return new RecordingSession(channel, process, tempPath, outputPath, config.FfmpegPath, DateTime.Now);
    }

    private string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? _root;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{name}_{DateTime.Now:HHmmss}{extension}");
    }

    private long? FileSizeOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private void FinalizeRecording(RecordingSession session)
    {
        if (!File.Exists(session.TempPath))
        {
            Log("recording temp file missing: " + session.TempPath);
            return;
        }

        var args = new List<string>
        {
            "-hide_banner",
            "-y",
            "-i", session.TempPath,
            "-c", "copy",
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            "-nostats",
            session.OutputPath
        };

        try
        {
            Log("finalizing mp4: " + session.OutputPath);
            var finalizingSeconds = Math.Max(1, (DateTime.Now - session.StartedAt).TotalSeconds);
            using var remux = Process.Start(new ProcessStartInfo
            {
                FileName = session.FfmpegPath,
                Arguments = JoinArgs(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (remux == null)
            {
                Log("mp4 finalize failed; ffmpeg did not start");
                return;
            }

            remux.ErrorDataReceived += (_, _) => { };
            remux.BeginErrorReadLine();
            while (!remux.StandardOutput.EndOfStream)
            {
                var line = remux.StandardOutput.ReadLine();
                var percent = ParseProgressPercent(line, finalizingSeconds);
                if (percent.HasValue)
                {
                    SetStatus(session.Channel, ChannelState.Finalizing, "finalizing", true, session.StartedAt, session.TempPath, percent.Value);
                }
            }
            remux.WaitForExit();
            if (remux?.ExitCode == 0 && File.Exists(session.OutputPath))
            {
                SetStatus(session.Channel, ChannelState.Finalizing, "finalizing", true, session.StartedAt, session.TempPath, 100);
                File.Delete(session.TempPath);
                Log("mp4 ready: " + session.OutputPath);
            }
            else
            {
                Log("mp4 finalize failed; kept temp file: " + session.TempPath);
            }
        }
        catch (Exception ex)
        {
            Log("mp4 finalize error: " + ex.Message);
        }
    }

    private int? ParseProgressPercent(string? line, double totalSeconds)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(line["out_time_ms=".Length..], out var micros))
        {
            return ClampPercent(micros / 1_000_000d / totalSeconds);
        }
        if (line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase) &&
            TimeSpan.TryParse(line["out_time=".Length..], out var time))
        {
            return ClampPercent(time.TotalSeconds / totalSeconds);
        }
        return null;
    }

    private int ClampPercent(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return 0;
        return Math.Max(0, Math.Min(100, (int)Math.Round(ratio * 100)));
    }

    private void StopFfmpeg(Process process)
    {
        try
        {
            process.StandardInput.WriteLine("q");
            if (process.WaitForExit(8000)) return;
        }
        catch
        {
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    private string JoinArgs(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(arg => arg.Any(char.IsWhiteSpace) || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg));
    }

    private void SetStatus(ChannelConfig channel, ChannelState state, string message, bool? isBroadcastOnline = null, DateTime? recordingStartedAt = null, string? recordingPath = null, int? finalizeProgressPercent = null)
    {
        StatusChanged?.Invoke(this, new ChannelStatusEventArgs
        {
            Channel = channel,
            State = state,
            Message = message,
            IsBroadcastOnline = isBroadcastOnline,
            RecordingStartedAt = recordingStartedAt,
            RecordingPath = recordingPath,
            FinalizeProgressPercent = finalizeProgressPercent
        });
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        LogWritten?.Invoke(this, line);
    }
}

internal sealed record RecordingSession(ChannelConfig Channel, Process Process, string TempPath, string OutputPath, string FfmpegPath, DateTime StartedAt);

internal sealed record StreamInfo(string StreamUrl, string Title);

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
        return config;
    }

    public static void Save(string path, AppConfig config)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }
}

