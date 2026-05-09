using System.Diagnostics;
using System.Globalization;
using System.Text;
using NLog;

namespace ShokoRelay.Services;

/// <summary>Wrapper around FFmpeg/FFprobe CLI tools used by the AnimeThemes subsystem.</summary>
public sealed class FfmpegService
{
    #region Fields & Constructor

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly Lock s_ffmpegLock = new();
    private static bool s_ffmpegConfigured;
    private static string s_ffmpegPath = "ffmpeg";
    private static string s_ffprobePath = "ffprobe";
    private static string s_pluginDirectory = string.Empty;
    private static string s_workingDirectory = string.Empty;

    /// <summary>Construct the service, supplying the path to the plugin directory which will be searched for binaries.</summary>
    /// <param name="pluginDirectory">The root directory for the plugin.</param>
    public FfmpegService(string pluginDirectory)
    {
        s_pluginDirectory = pluginDirectory;
        s_workingDirectory = s_pluginDirectory;
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

        string output = await RunProcessCaptureAsync(s_ffprobePath, args, ct);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException("Unable to parse duration from ffprobe output.");
    }

    /// <summary>Convert a media file to an MP3 with embedded metadata.</summary>
    /// <param name="inputPath">Source file path.</param>
    /// <param name="outputPath">Destination path for MP3 (filename or relative path).</param>
    /// <param name="title">Song title metadata tag.</param>
    /// <param name="slugDisplay">Display slug metadata tag (TIT3).</param>
    /// <param name="artist">Artist metadata tag.</param>
    /// <param name="album">Album metadata tag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="workingDir">Optional working directory for the process.</param>
    public async Task ConvertToMp3FileAsync(string inputPath, string outputPath, string title, string slugDisplay, string artist, string album, CancellationToken ct, string? workingDir = null)
    {
        EnsureFfmpegConfigured();
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

        await RunProcessAsync(s_ffmpegPath, args, null, null, ct, workingDir).ConfigureAwait(false);
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

        await RunProcessAsync(s_ffmpegPath, args, null, ms, ct);
        ms.Position = 0;
        return ms;
    }

    #endregion

    #region Configuration Logic

    /// <summary>Ensures that the paths to the FFmpeg and FFprobe binaries are resolved and verified. Searches configured paths, the plugin directory, and the system PATH.</summary>
    private static void EnsureFfmpegConfigured()
    {
        if (s_ffmpegConfigured)
            return;

        lock (s_ffmpegLock)
        {
            if (s_ffmpegConfigured)
                return;

            string configured = ShokoRelay.Settings.Advanced.FFmpegPath;
            string pluginDir = s_pluginDirectory;
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
                        s_ffmpegPath = full;
                        ffmpegFound = true;
                        string? dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            candidates.Add(dir);
                        locatedDir ??= dir;
                    }
                    else
                        s_logger.Warn("FfmpegService: FFmpeg path does not exist ->{Path}", full);
                }
                catch (Exception ex)
                {
                    s_logger.Warn(ex, "FfmpegService: Failed to configure FFmpeg path");
                }
            }

            if (Directory.Exists(pluginDir))
                candidates.Add(pluginDir);

            foreach (string dir in candidates)
            {
                if (!ffmpegFound)
                    (ffmpegFound, locatedDir) = TryFindBinary(dir, ffmpegName, ref s_ffmpegPath, locatedDir);
                if (!ffprobeFound)
                    (ffprobeFound, locatedDir) = TryFindBinary(dir, ffprobeName, ref s_ffprobePath, locatedDir);
                if (ffmpegFound && ffprobeFound)
                    break;
            }

            if (ffmpegFound || ffprobeFound)
            {
                s_workingDirectory = DetermineWorkingDirectory(locatedDir ?? pluginDir);
                s_logger.Info("FfmpegService: FFmpeg binaries configured at {Path}", locatedDir ?? "system PATH");
            }
            else
            {
                s_ffmpegPath = ffmpegName;
                s_ffprobePath = ffprobeName;
                s_workingDirectory = DetermineWorkingDirectory(pluginDir);
                s_logger.Warn("FfmpegService: FFmpeg binaries not found in configured paths -> falling back to system PATH");
            }

            s_ffmpegConfigured = true;
        }
    }

    /// <summary>Searches a specific directory for a binary and updates the provided path field if found.</summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="binaryName">The filename of the binary (e.g., ffmpeg.exe).</param>
    /// <param name="pathField">A reference to the string field storing the resolved path.</param>
    /// <param name="currentLocatedDir">The directory where a previous binary in the suite was found, used as a hint.</param>
    /// <returns>A tuple containing a boolean indicating success and the directory where the binary was located.</returns>
    private static (bool Found, string? LocatedDir) TryFindBinary(string dir, string binaryName, ref string pathField, string? currentLocatedDir)
    {
        string candidate = Path.Combine(dir, binaryName);
        if (!File.Exists(candidate))
            return (false, currentLocatedDir);

        pathField = candidate;
        return (true, currentLocatedDir ?? dir);
    }

    /// <summary>Resolves a valid working directory for process execution based on a preferred path hint.</summary>
    /// <param name="preferred">The preferred directory or file path to evaluate.</param>
    /// <returns>A confirmed absolute directory path.</returns>
    private static string DetermineWorkingDirectory(string preferred) =>
        File.Exists(preferred) && Path.GetDirectoryName(preferred) is string dir && Directory.Exists(dir) ? dir
        : Directory.Exists(preferred) ? preferred
        : s_pluginDirectory;

    #endregion

    #region Process Execution

    /// <summary>Executes an FFmpeg process with optional stream redirection and robust error handling.</summary>
    /// <param name="fileName">The path to the FFmpeg/FFprobe binary.</param>
    /// <param name="args">The list of command line arguments.</param>
    /// <param name="stdIn">Optional input stream to pipe into the process.</param>
    /// <param name="stdOut">Optional output stream to capture process output.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="workingDir">Optional working directory for the process.</param>
    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> args, Stream? stdIn, Stream? stdOut, CancellationToken ct, string? workingDir = null)
    {
        var psi = CreateProcessStartInfo(fileName, args, workingDir);
        psi.RedirectStandardInput = stdIn != null;
        psi.RedirectStandardOutput = stdOut != null;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();

            var tasks = new List<Task>();
            if (stdIn != null)
            {
                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                await stdIn.CopyToAsync(process.StandardInput.BaseStream, ct).ConfigureAwait(false);
                                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                            }
                            finally
                            {
                                process.StandardInput.Close();
                            }
                        },
                        ct
                    )
                );
            }

            if (stdOut != null)
                tasks.Add(process.StandardOutput.BaseStream.CopyToAsync(stdOut, ct));

            tasks.Add(process.WaitForExitAsync(ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.ToString().Trim()}");
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch { }
            throw;
        }
    }

    /// <summary>Executes a process and captures its standard output stream as a string.</summary>
    /// <param name="fileName">The path to the executable.</param>
    /// <param name="args">The list of command line arguments.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full string content of the process's standard output.</returns>
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

        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return process.ExitCode != 0 ? throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.ToString().Trim()}") : output;
    }

    /// <summary>Initializes a <see cref="ProcessStartInfo"/> object with common plugin requirements.</summary>
    /// <param name="fileName">The path to the executable file.</param>
    /// <param name="args">The list of command line arguments to provide to the process.</param>
    /// <param name="workingDir">Optional directory override for the process execution.</param>
    /// <returns>A pre-configured <see cref="ProcessStartInfo"/> instance.</returns>
    private static ProcessStartInfo CreateProcessStartInfo(string fileName, IReadOnlyList<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? s_workingDirectory,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    #endregion

    #region Helpers

    /// <summary>Sanitizes metadata values by replacing double quotes with single quotes to ensure command-line argument integrity.</summary>
    /// <param name="value">The raw metadata string to sanitize.</param>
    /// <returns>A sanitized string safe for use in FFmpeg metadata arguments.</returns>
    private static string EscapeMetadata(string value) => value?.Replace("\"", "'") ?? string.Empty;

    #endregion
}
