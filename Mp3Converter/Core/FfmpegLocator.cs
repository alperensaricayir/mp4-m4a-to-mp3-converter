using System;
using System.IO;

namespace Mp3Converter.Core
{
    /// <summary>
    /// Resolves ffmpeg/ffprobe executables: system PATH first, then the app's "tools" folder,
    /// then the per-user download cache in LocalAppData.
    /// </summary>
    public static class FfmpegLocator
    {
        public static string ToolsDirectory { get; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");

        /// <summary>Persisted download location so bin/ rebuilds don't force a re-download.</summary>
        public static string CacheToolsDirectory { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mp3Converter",
                "tools");

        public static string? FindFfmpeg() => Find("ffmpeg");

        public static string? FindFfprobe() => Find("ffprobe");

        private static string? Find(string baseName)
        {
            string exeName = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

            // 1) System PATH
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { /* malformed PATH entry */ }
                }
            }

            // 2) Application tools folder
            string local = Path.Combine(ToolsDirectory, exeName);
            if (File.Exists(local)) return local;

            // 3) Application root
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
            if (File.Exists(root)) return root;

            // 4) Per-user download cache
            string cached = Path.Combine(CacheToolsDirectory, exeName);
            if (File.Exists(cached)) return cached;

            return null;
        }
    }
}
