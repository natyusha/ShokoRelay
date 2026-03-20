using System.Diagnostics;
using System.Globalization;
using System.Text;
using NLog;

namespace ShokoRelay.Services;

/// <summary>Wrapper around FFmpeg/FFprobe CLI tools used by the AnimeThemes subsystem.</summary>
public sealed class FfmpegService
{
    #region Fields & Constructor

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Lock FfmpegLock = new();
    private static bool _ffmpegConfigured;
    private static string _ffmpegPath = "ffmpeg";
    private static string _ffprobePath = "ffprobe";
    private static string _pluginDirectory = string.Empty;
    private static string _workingDirectory = string.Empty;

    /// <summary>Construct the service, supplying the path to the plugin directory which will be searched for binaries.</summary>
    /// <param name="pluginDirectory">The root directory for the plugin.</param>
    public FfmpegService(string pluginDirectory)
    {
        _pluginDirectory = pluginDirectory;
        _workingDirectory = _pluginDirectory;
    }

    #endregion

    #region Public API

    /// <summary>Probe a media file's duration using ffprobe and return the result as a TimeSpan.</summary>
    /// <param name="inputPath">Path to the media file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The duration of the media.</returns>
    /// <exception cref="InvalidOperationException">Thrown if ffprobe output is unparseable.</exception>
    public async Task<TimeSpan> ProbeDurationAsync(string inputPath, CancellationToken ct)
    {
        EnsureFfmpegConfigured();
        var args = new List<string> { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", inputPath };

        string output = await RunProcessCaptureAsync(_ffprobePath, args, ct);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException("Unable to parse duration from ffprobe output.");
    }

    /// <summary>Convert a media file to an MP3 with embedded metadata.</summary>
    /// <param name="inputPath">Source file path.</param>
    /// <param name="outputPath">Destination path for MP3.</param>
    /// <param name="title">Song title metadata tag.</param>
    /// <param name="slugDisplay">Display slug metadata tag (TIT3).</param>
    /// <param name="artist">Artist metadata tag.</param>
    /// <param name="album">Album metadata tag.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConvertToMp3FileAsync(string inputPath, string outputPath, string title, string slugDisplay, string artist, string album, CancellationToken ct)
    {
        EnsureFfmpegConfigured();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // csharpier-ignore-start
        var args = new List<string>
        {
            "-y", "-loglevel", "error", "-i", inputPath, "-vn", "-acodec", "libmp3lame", "-b:a", "320k",
            "-metadata",
            $"title={EscapeMetadata(title)}",      // Title
            "-metadata",
            $"TIT3={EscapeMetadata(slugDisplay)}", // Subtitle
            "-metadata",
            $"artist={EscapeMetadata(artist)}",    // Contributing artists
            "-metadata",
            $"album={EscapeMetadata(album)}",      // Album
            outputPath,
        };
        // csharpier-ignore-end

        await RunProcessAsync(_ffmpegPath, args, null, null, ct);
    }

    /// <summary>Convert a media file to MP3 audio and return it in a MemoryStream.</summary>
    /// <param name="inputPath">Source file path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream containing the MP3-encoded audio.</returns>
    public async Task<MemoryStream> ConvertToMp3StreamAsync(string inputPath, CancellationToken ct)
    {
        EnsureFfmpegConfigured();
        var ms = new MemoryStream();
        var args = new List<string> { "-loglevel", "error", "-i", inputPath, "-vn", "-acodec", "libmp3lame", "-b:a", "320k", "-f", "mp3", "pipe:1" };

        await RunProcessAsync(_ffmpegPath, args, null, ms, ct);
        ms.Position = 0;
        return ms;
    }

    #endregion

    #region Configuration Logic

    private static void EnsureFfmpegConfigured()
    {
        if (_ffmpegConfigured)
            return;

        lock (FfmpegLock)
        {
            if (_ffmpegConfigured)
                return;

            string configured = ShokoRelay.Settings.Advanced.FFmpegPath;
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
                        candidates.Add(full);
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
                        Logger.Warn("FFmpeg path does not exist: {Path}", full);
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
                if (!ffmpegFound)
                    (ffmpegFound, locatedDir) = TryFindBinary(dir, ffmpegName, ref _ffmpegPath, locatedDir);
                if (!ffprobeFound)
                    (ffprobeFound, locatedDir) = TryFindBinary(dir, ffprobeName, ref _ffprobePath, locatedDir);
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

    private static (bool Found, string? LocatedDir) TryFindBinary(string dir, string binaryName, ref string pathField, string? currentLocatedDir)
    {
        string candidate = Path.Combine(dir, binaryName);
        if (!File.Exists(candidate))
            return (false, currentLocatedDir);

        pathField = candidate;
        return (true, currentLocatedDir ?? dir);
    }

    private static string DetermineWorkingDirectory(string preferred)
    {
        if (File.Exists(preferred))
        {
            string? dir = Path.GetDirectoryName(preferred);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        return Directory.Exists(preferred) ? preferred : _pluginDirectory;
    }

    #endregion

    #region Process Execution

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
            tasks.Add(process.StandardOutput.BaseStream.CopyToAsync(stdOut, ct));

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

        return process.ExitCode != 0 ? throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.ToString().Trim()}") : output;
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

    #endregion

    #region Helpers

    private static string EscapeMetadata(string value) => value?.Replace("\"", "'") ?? string.Empty;

    #endregion
}
