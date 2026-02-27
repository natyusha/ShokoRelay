using NLog;

namespace ShokoRelay.Helpers
{
    /// <summary>
    /// Utility methods for writing plugin-specific diagnostic logs.
    /// </summary>
    public static class LogHelper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Ensure that the <c>logs</c> subdirectory exists beneath <paramref name="pluginDir"/> and return its path. Throws if <paramref name="pluginDir"/> is null or whitespace.
        /// </summary>
        public static string GetLogsDir(string pluginDir)
        {
            if (string.IsNullOrWhiteSpace(pluginDir))
                throw new ArgumentException("pluginDir is required", nameof(pluginDir));

            string dir = Path.Combine(pluginDir, "logs");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to create logs directory '{Dir}'", dir);
            }
            return dir;
        }

        /// <summary>
        /// Write <paramref name="content"/> to <paramref name="fileName"/> inside the logs directory obtained from <paramref name="pluginDir"/>. Returns the full path to the created file.
        /// </summary>
        public static string WriteLog(string pluginDir, string fileName, string content)
        {
            var dir = GetLogsDir(pluginDir);
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
