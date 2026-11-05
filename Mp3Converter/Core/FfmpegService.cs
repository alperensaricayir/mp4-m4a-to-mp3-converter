using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
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
        /// Runs ffmpeg off the UI thread. Progress is read from <c>-progress pipe:1</c> (stdout)
        /// at a throttled rate so a multi-hour encode cannot flood the dispatcher or grow the log.
        /// Stderr is kept as a short tail of real errors; per-frame warnings are discarded.
        /// </summary>
        public async Task<FfmpegRunResult> RunFfmpegAsync(
            string arguments,
            TimeSpan totalDuration,
            IProgress<double>? progress,
            Action<string>? log,
            CancellationToken ct)
        {
            string ffmpeg = FfmpegPath ?? throw new InvalidOperationException("FFmpeg bulunamadı.");

            // Machine-readable progress on stdout; hide per-frame stats/warnings that
            // previously filled RAM and crashed the UI near the end of long encodes.
            string args = arguments.Contains("-progress ", StringComparison.Ordinal)
                ? arguments
                : "-nostats -loglevel error -progress pipe:1 " + arguments;

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = utf8,
                StandardOutputEncoding = utf8
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            var tail = new Queue<string>();
            const int tailSize = 40;
            int loggedErrors = 0;
            long lastProgressTicks = 0;
            double lastPercent = -1;

            if (!process.Start())
                throw new InvalidOperationException("FFmpeg işlemi başlatılamadı.");

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            void Remember(string line)
            {
                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > tailSize) tail.Dequeue();
                }
            }

            void MaybeReport(double percent)
            {
                if (progress == null) return;
                long now = Environment.TickCount64;
                if (percent < 99.5 && now - lastProgressTicks < 250 && Math.Abs(percent - lastPercent) < 0.25)
                    return;
                lastProgressTicks = now;
                lastPercent = percent;
                progress.Report(Math.Clamp(percent, 0, 100));
            }

            // stdout: key=value progress records. Never buffer the whole stream.
            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                    {
                        if (progress == null || totalDuration.TotalSeconds <= 0) continue;

                        double seconds = -1;
                        if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                            && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)
                            && us >= 0)
                        {
                            seconds = us / 1_000_000.0;
                        }
                        else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal)
                                 && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)
                                 && ms >= 0)
                        {
                            seconds = ms / 1000.0;
                        }
                        else if (line.StartsWith("out_time=", StringComparison.Ordinal))
                        {
                            var m = TimeRegex.Match("time=" + line.Substring(9));
                            if (m.Success)
                            {
                                seconds = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                                          + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                                          + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                            }
                        }

                        if (seconds >= 0)
                            MaybeReport(seconds / totalDuration.TotalSeconds * 100);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ffmpeg stdout reader: " + ex.Message);
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line) || IsProgressLine(line)) continue;
                        Remember(line);

                        // Cap forwarded errors: a 32 GB file can emit millions of DTS warnings.
                        if (log != null && loggedErrors < 30 && LooksLikeError(line))
                        {
                            loggedErrors++;
                            log(line);
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
                try { await Task.WhenAll(stderrTask, stdoutTask).WaitAsync(TimeSpan.FromSeconds(8)); } catch { }
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

        /// <summary>"base.ext", then "base_2.ext", "base_3.ext", ... never overwriting.</summary>
        public static string GetUniqueSuffixedPath(string folder, string baseName, string extension)
        {
            string candidate = Path.Combine(folder, baseName + extension);
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, $"{baseName}_{n}{extension}");
                n++;
            }
            return candidate;
        }

        private static bool IsProgressLine(string line)
        {
            var l = line.TrimStart();
            return l.StartsWith("frame=") || l.StartsWith("size=") || (l.Contains("time=") && l.Contains("bitrate="));
        }

        private static bool LooksLikeError(string line)
        {
            var l = line.ToLowerInvariant();
            return l.Contains("error") || l.Contains("failed") || l.Contains("invalid")
                   || l.Contains("not found") || l.Contains("denied") || l.Contains("no space")
                   || l.Contains("could not") || l.Contains("unable to") || l.Contains("cannot");
        }

        public static string LastMeaningfulLine(IReadOnlyList<string> lines)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (l.Length > 0 && !IsProgressLine(l)) return l;
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
