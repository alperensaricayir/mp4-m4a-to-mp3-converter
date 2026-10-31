using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mp3Converter.Core
{
    /// <summary>Result of a single ffmpeg invocation.</summary>
    public sealed class FfmpegRunResult
    {
        public int ExitCode { get; init; }
        public bool Cancelled { get; init; }
        public IReadOnlyList<string> StderrTail { get; init; } = Array.Empty<string>();
        public bool Success => ExitCode == 0 && !Cancelled;
    }

    /// <summary>
    /// Central FFmpeg integration: executable discovery, download, duration probing,
    /// the MP3/MP4 conversion queue, and a shared process runner with progress parsing.
    /// </summary>
    public class FfmpegService
    {
        private static readonly Regex TimeRegex = new(@"time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

        // Downloads go to the per-user cache so bin/ rebuilds don't force a re-download.
        private readonly string _toolsDir;

        public FfmpegService()
        {
            _toolsDir = FfmpegLocator.CacheToolsDirectory;
        }

        /// <summary>Resolved ffmpeg path (PATH → app tools folder → LocalAppData cache) or null.</summary>
        public string? FfmpegPath => FfmpegLocator.FindFfmpeg();

        /// <summary>Resolved ffprobe path (PATH → app tools folder → LocalAppData cache) or null.</summary>
        public string? FfprobePath => FfmpegLocator.FindFfprobe();

        public bool CheckFfmpegExists() => FfmpegPath != null && FfprobePath != null;

        public string GetFfmpegPath() => FfmpegPath ?? Path.Combine(FfmpegLocator.ToolsDirectory, "ffmpeg.exe");

        public async Task DownloadFfmpegAsync(IProgress<double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(_toolsDir);

            string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            string zipPath = Path.Combine(_toolsDir, "ffmpeg.zip");
            string ffmpegTarget = Path.Combine(_toolsDir, "ffmpeg.exe");
            string ffprobeTarget = Path.Combine(_toolsDir, "ffprobe.exe");

            if (File.Exists(ffmpegTarget) && File.Exists(ffprobeTarget))
            {
                progress?.Report(100);
                return;
            }

            using (var client = new HttpClient())
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? 100 * 1024 * 1024;

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, ct);
                    totalRead += read;
                    progress?.Report((double)totalRead / totalBytes * 100);
                }
            }

            progress?.Report(101); // extraction phase

            string extractPath = Path.Combine(_toolsDir, "temp_extract");
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);

            ZipFile.ExtractToDirectory(zipPath, extractPath);

            var binFiles = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories);
            if (binFiles.Length > 0) File.Copy(binFiles[0], ffmpegTarget, true);

            var probeFiles = Directory.GetFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories);
            if (probeFiles.Length > 0) File.Copy(probeFiles[0], ffprobeTarget, true);

            try
            {
                File.Delete(zipPath);
                Directory.Delete(extractPath, true);
            }
            catch { }
        }

        public async Task<TimeSpan> ProbeDurationAsync(string inputPath)
        {
            string? ffprobe = FfprobePath;
            if (ffprobe != null)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ffprobe,
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
                        {
                            return TimeSpan.FromSeconds(seconds);
                        }
                    }
                }
                catch { /* fall back to ffmpeg */ }
            }

            string? ffmpeg = FfmpegPath;
            if (ffmpeg == null) return TimeSpan.Zero;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-i \"{inputPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    var match = Regex.Match(output, @"Duration: (\d{2}:\d{2}:\d{2}\.\d{2})");
                    if (match.Success && TimeSpan.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out TimeSpan duration))
                    {
                        return duration;
                    }
                }
            }
            catch { }

            return TimeSpan.Zero;
        }

        public static string GetExtension(OutputFormat format) =>
            format == OutputFormat.Mp4 ? ".mp4" : ".mp3";

        public async Task ConvertAsync(
            ConversionItem item,
            string outputFolder,
            Mp3QualitySettings settings,
            OutputFormat outputFormat,
            CancellationToken ct)
        {
            if (FfmpegPath == null)
                throw new InvalidOperationException("FFmpeg bulunamadı.");

            Directory.CreateDirectory(outputFolder);

            string extension = GetExtension(outputFormat);
            string baseName = Path.GetFileNameWithoutExtension(item.InputPath);
            string outputPath = GetUniquePath(outputFolder, baseName, extension);
            item.OutputPath = outputPath;

            string args = outputFormat == OutputFormat.Mp4
                ? BuildMp4Args(item.InputPath, outputPath, settings)
                : BuildMp3Args(item.InputPath, outputPath, settings);

            var progress = new Progress<double>(p => item.Progress = Math.Min(99.9, p));

            FfmpegRunResult result;
            try
            {
                result = await RunFfmpegAsync(args, item.Duration, progress, null, ct);
            }
            catch (OperationCanceledException)
            {
                item.Status = ConversionStatus.Cancelled;
                throw new TaskCanceledException();
            }

            if (result.Cancelled)
            {
                item.Status = ConversionStatus.Cancelled;
                throw new TaskCanceledException();
            }

            if (result.ExitCode == 0)
            {
                item.Progress = 100;
                item.Status = ConversionStatus.Completed;
            }
            else
            {
                item.Status = ConversionStatus.Failed;
                item.ErrorMessage = "FFmpeg exited with code " + result.ExitCode;
                throw new Exception("FFmpeg failed: " + LastMeaningfulLine(result.StderrTail));
            }
        }

        /// <summary>
        /// Runs ffmpeg with the given arguments off the UI thread, parses "time=" from stderr
        /// into percentage progress against <paramref name="totalDuration"/>, and supports cancellation
        /// by killing the process.
        /// </summary>
        public async Task<FfmpegRunResult> RunFfmpegAsync(
            string arguments,
            TimeSpan totalDuration,
            IProgress<double>? progress,
            Action<string>? log,
            CancellationToken ct)
        {
            string ffmpeg = FfmpegPath ?? throw new InvalidOperationException("FFmpeg bulunamadı.");

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            var tail = new Queue<string>();
            const int tailSize = 40;

            if (!process.Start())
                throw new InvalidOperationException("FFmpeg işlemi başlatılamadı.");

            // Drain stdout so ffmpeg never blocks on a full pipe.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) != null)
                    {
                        lock (tail)
                        {
                            tail.Enqueue(line);
                            while (tail.Count > tailSize) tail.Dequeue();
                        }

                        log?.Invoke(line);

                        if (progress != null && totalDuration.TotalSeconds > 0)
                        {
                            var m = TimeRegex.Match(line);
                            if (m.Success)
                            {
                                double h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                                double mi = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                                double s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                                double current = h * 3600 + mi * 60 + s;
                                progress.Report(Math.Clamp(current / totalDuration.TotalSeconds * 100, 0, 100));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ffmpeg stderr reader: " + ex.Message);
                }
            });

            bool cancelled = false;
            using var registration = ct.Register(() =>
            {
                cancelled = true;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            finally
            {
                try { await Task.WhenAll(stderrTask, stdoutTask).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }

            string[] tailCopy;
            lock (tail) tailCopy = tail.ToArray();

            return new FfmpegRunResult
            {
                ExitCode = process.ExitCode,
                Cancelled = cancelled || ct.IsCancellationRequested,
                StderrTail = tailCopy
            };
        }

        /// <summary>Returns a path in <paramref name="folder"/> that does not yet exist, appending " (n)".</summary>
        public static string GetUniquePath(string folder, string baseName, string extension)
        {
            string path = Path.Combine(folder, baseName + extension);
            int counter = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, $"{baseName} ({counter}){extension}");
                counter++;
            }
            return path;
        }

        public static string LastMeaningfulLine(IReadOnlyList<string> lines)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (l.Length > 0 && !l.StartsWith("frame=") && !l.StartsWith("size=")) return l;
            }
            return "(no output)";
        }

        private static string BuildMp3Args(string inputPath, string outputPath, Mp3QualitySettings settings)
        {
            string audioArgs = settings.IsCbr
                ? $"-b:a {settings.Bitrate}k"
                : $"-q:a {settings.VbrQuality}";

            return $"-hide_banner -y -i \"{inputPath}\" -vn -acodec libmp3lame {audioArgs} -ar 44100 -ac 2 \"{outputPath}\"";
        }

        private static string BuildMp4Args(string inputPath, string outputPath, Mp3QualitySettings settings)
        {
            // H.264 + AAC for broad MP4 compatibility (WebM VP8/VP9/Opus cannot be remuxed reliably).
            // Optional maps keep audio-only inputs working as MP4 too.
            return $"-hide_banner -y -i \"{inputPath}\" -map 0:v? -map 0:a? -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p -c:a aac -b:a {settings.Bitrate}k -movflags +faststart \"{outputPath}\"";
        }
    }
}
