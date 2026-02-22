using System.Diagnostics;
using System.Globalization;
using System.Text;
using NLog;

namespace ShokoRelay.AnimeThemes;

internal sealed class FfmpegService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly object FfmpegLock = new();
    private static bool _ffmpegConfigured;
    private static string _ffmpegPath = "ffmpeg";
    private static string _ffprobePath = "ffprobe";
    private static string _pluginDirectory = string.Empty;
    private static string _workingDirectory = string.Empty;

    public FfmpegService(string pluginDirectory)
    {
        _pluginDirectory = pluginDirectory;
        _workingDirectory = _pluginDirectory;
    }

    public async Task<TimeSpan> ProbeDurationAsync(string inputPath, CancellationToken ct)
    {
        EnsureFfmpegConfigured();
        var args = new List<string> { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", inputPath };

        string output = await RunProcessCaptureAsync(_ffprobePath, args, ct);
        if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return TimeSpan.FromSeconds(seconds);

        throw new InvalidOperationException("Unable to parse duration from ffprobe output.");
    }

    public async Task ConvertToMp3FileAsync(string inputPath, string outputPath, string title, string slugDisplay, string artist, string album, CancellationToken ct)
    {
        EnsureFfmpegConfigured();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var args = new List<string>
        {
            "-y",
            "-loglevel",
            "error",
            "-i",
            inputPath,
            "-vn",
            "-acodec",
            "libmp3lame",
            "-b:a",
            "320k",
            "-metadata",
            $"title={EscapeMetadata(title)}",
            "-metadata",
            $"TIT3={EscapeMetadata(slugDisplay)}",
            "-metadata",
            $"artist={EscapeMetadata(artist)}",
            "-metadata",
            $"album={EscapeMetadata(album)}",
            outputPath,
        };

        await RunProcessAsync(_ffmpegPath, args, null, null, ct);
    }

    public async Task<MemoryStream> ConvertToMp3StreamAsync(string inputPath, CancellationToken ct)
    {
        EnsureFfmpegConfigured();
        var ms = new MemoryStream();
        var args = new List<string> { "-loglevel", "error", "-i", inputPath, "-vn", "-acodec", "libmp3lame", "-b:a", "320k", "-f", "mp3", "pipe:1" };

        await RunProcessAsync(_ffmpegPath, args, null, ms, ct);
        ms.Position = 0;
        return ms;
    }

    private static string EscapeMetadata(string value)
    {
        return value?.Replace("\"", "'") ?? string.Empty;
    }

    private static void EnsureFfmpegConfigured()
    {
        if (_ffmpegConfigured)
            return;

        lock (FfmpegLock)
        {
            if (_ffmpegConfigured)
                return;

            string configured = ShokoRelay.Settings.FFmpegPath;
            string pluginDir = _pluginDirectory;
            string ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            string ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

            bool ffmpegFound = false;
            bool ffprobeFound = false;
            string? locatedDir = null;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    string full = Path.GetFullPath(configured);
                    if (Directory.Exists(full))
                    {
                        candidates.Add(full);
                    }
                    else if (File.Exists(full))
                    {
                        _ffmpegPath = full;
                        ffmpegFound = true;
                        string? dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            candidates.Add(dir);
                        locatedDir ??= dir;
                    }
                    else
                    {
                        Logger.Warn("FFmpeg path does not exist: {Path}", full);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to configure FFmpeg path");
                }
            }

            if (Directory.Exists(pluginDir))
                candidates.Add(pluginDir);

            foreach (string dir in candidates)
            {
                string ffmpegCandidate = Path.Combine(dir, ffmpegName);
                string ffprobeCandidate = Path.Combine(dir, ffprobeName);

                if (!ffmpegFound && File.Exists(ffmpegCandidate))
                {
                    _ffmpegPath = ffmpegCandidate;
                    ffmpegFound = true;
                    locatedDir ??= dir;
                }

                if (!ffprobeFound && File.Exists(ffprobeCandidate))
                {
                    _ffprobePath = ffprobeCandidate;
                    ffprobeFound = true;
                    locatedDir ??= dir;
                }

                if (ffmpegFound && ffprobeFound)
                    break;
            }

            if (ffmpegFound || ffprobeFound)
            {
                _workingDirectory = DetermineWorkingDirectory(locatedDir ?? pluginDir);
                Logger.Info("FFmpeg binaries configured at {Path}", locatedDir ?? "system PATH");
            }
            else
            {
                _ffmpegPath = ffmpegName;
                _ffprobePath = ffprobeName;
                _workingDirectory = DetermineWorkingDirectory(pluginDir);
                Logger.Warn("FFmpeg binaries not found in configured paths; falling back to system PATH.");
            }

            _ffmpegConfigured = true;
        }
    }

    private static string DetermineWorkingDirectory(string preferred)
    {
        if (File.Exists(preferred))
        {
            string? dir = Path.GetDirectoryName(preferred);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        if (Directory.Exists(preferred))
            return preferred;

        return _pluginDirectory;
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> args, Stream? stdIn, Stream? stdOut, CancellationToken ct)
    {
        var psi = CreateProcessStartInfo(fileName, args);
        psi.RedirectStandardInput = stdIn != null;
        psi.RedirectStandardOutput = stdOut != null;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        var tasks = new List<Task>();
        if (stdIn != null)
        {
            tasks.Add(
                Task.Run(
                    async () =>
                    {
                        await stdIn.CopyToAsync(process.StandardInput.BaseStream, ct);
                        await process.StandardInput.FlushAsync();
                        process.StandardInput.Close();
                    },
                    ct
                )
            );
        }

        if (stdOut != null)
        {
            tasks.Add(process.StandardOutput.BaseStream.CopyToAsync(stdOut, ct));
        }

        tasks.Add(process.WaitForExitAsync(ct));
        await Task.WhenAll(tasks);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.ToString().Trim()}");
    }

    private static async Task<string> RunProcessCaptureAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = CreateProcessStartInfo(fileName, args);
        psi.RedirectStandardOutput = true;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.ToString().Trim()}");

        return output;
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }
}
