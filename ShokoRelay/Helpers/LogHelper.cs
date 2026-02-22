namespace ShokoRelay.Helpers
{
    public static class LogHelper
    {
        /// Ensure the log directory exists and return its path using a pre-resolved plugin directory. All callers should pass the plugin directory obtained via <see cref="ConfigConstants.GetPluginDirectory"/>.
        public static string GetLogsDir(string pluginDir)
        {
            if (string.IsNullOrWhiteSpace(pluginDir))
                throw new ArgumentException("pluginDir is required", nameof(pluginDir));

            string dir = Path.Combine(pluginDir, "logs");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            { /* ignore */
            }
            return dir;
        }

        // Write content to a named file under the logs directory and return the full path to the file. Plugin directory must already be known.
        public static string WriteLog(string pluginDir, string fileName, string content)
        {
            var dir = GetLogsDir(pluginDir);
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
