using System;
using System.IO;

namespace Mp3Converter.Core
{
    /// <summary>
    /// Append-only session log next to the executable. Used to reconstruct what the user
    /// was doing if the process disappears without a managed exception (no crash.log).
    /// </summary>
    public static class DiagnosticsLog
    {
        private static readonly object Gate = new();
        private const long MaxBytes = 2 * 1024 * 1024;

        public static string Path { get; } =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = Path + ".old";
                        File.Delete(old);
                        File.Move(Path, old);
                    }
                    File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
