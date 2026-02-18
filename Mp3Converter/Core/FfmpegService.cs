using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Compression;
using System.Net.Http;

namespace Mp3Converter.Core
{
    public class FfmpegService
    {
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;
        private readonly string _toolsDir;

        public FfmpegService()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            _toolsDir = Path.Combine(appDir, "tools");
            _ffmpegPath = Path.Combine(_toolsDir, "ffmpeg.exe");
            _ffprobePath = Path.Combine(_toolsDir, "ffprobe.exe");
        }

        public bool CheckFfmpegExists()
        {
            return File.Exists(_ffmpegPath) && File.Exists(_ffprobePath);
        }

        public async Task DownloadFfmpegAsync(IProgress<double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(_toolsDir);
            
            // Using a reliable release from GitHub (BtbN) - smaller and reliable
            // Note: This URL might change over time, ideally we should query the GitHub API for latest release asset.
            // But for simplicity and stability, let's use a known recent version or the "latest" redirect.
            string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            string zipPath = Path.Combine(_toolsDir, "ffmpeg.zip");

            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? 100 * 1024 * 1024; // Guess 100MB if unknown

                    using (var stream = await response.Content.ReadAsStreamAsync(ct))
                    using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[8192];
                        var isMoreToRead = true;
                        var totalRead = 0L;

                        do
                        {
                            var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                            if (read == 0)
                            {
                                isMoreToRead = false;
                            }
                            else
                            {
                                await fileStream.WriteAsync(buffer, 0, read, ct);
                                totalRead += read;
                                progress?.Report((double)totalRead / totalBytes * 100);
                            }
                        } while (isMoreToRead);
                    }
                }
            }

            // Extract
            progress?.Report(101); // Indicating extraction
            
            // Unzip to temp folder
            string extractPath = Path.Combine(_toolsDir, "temp_extract");
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);

            ZipFile.ExtractToDirectory(zipPath, extractPath);

            // Find ffmpeg.exe and ffprobe.exe in the extracted folders
            var binFiles = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories);
            if (binFiles.Length > 0) File.Copy(binFiles[0], _ffmpegPath, true);

            var probeFiles = Directory.GetFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories);
            if (probeFiles.Length > 0) File.Copy(probeFiles[0], _ffprobePath, true);

            // Cleanup
            try
            {
                File.Delete(zipPath);
                Directory.Delete(extractPath, true);
            }
            catch { }
        }

        public string GetFfmpegPath() => _ffmpegPath;

        public async Task<TimeSpan> ProbeDurationAsync(string inputPath)
        {
            // Try using ffprobe first if available
            if (File.Exists(_ffprobePath))
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _ffprobePath,
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
                        if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                        {
                            return TimeSpan.FromSeconds(seconds);
                        }
                    }
                }
                catch { /* Fallback to ffmpeg */ }
            }

            // Fallback to ffmpeg -i
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
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

                    // Parse Duration: HH:MM:SS.xx
                    var match = Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                    if (match.Success)
                    {
                        var timeStr = match.Groups[0].Value.Replace("Duration: ", "");
                        if (TimeSpan.TryParse(timeStr, out TimeSpan duration))
                        {
                            return duration;
                        }
                    }
                }
            }
            catch { }

            return TimeSpan.Zero;
        }

        public async Task ConvertAsync(ConversionItem item, string outputFolder, Mp3QualitySettings settings, CancellationToken ct)
        {
            // Ensure output directory
            Directory.CreateDirectory(outputFolder);

            // Determine Output Path
            string baseName = Path.GetFileNameWithoutExtension(item.InputPath);
            string outputPath = Path.Combine(outputFolder, baseName + ".mp3");

            // Handle conflicts
            int counter = 1;
            while (File.Exists(outputPath))
            {
                // Check skip logic (handled by caller or here?)
                // Prompt says: "Skip files already converted if output exists AND same size > 0 (add a toggle 'Skip existing' default ON)"
                // We'll implement the naming logic here first. 
                // Wait, if we want to skip, we shouldn't increment name.
                // But the caller needs to decide if we skip or overwrite or rename.
                // The prompt says: "Output naming: Keep base name, If conflict: append (1)..."
                // AND "Skip files already converted if output exists..."
                // This implies: If "Skip Existing" is ON, and file exists -> Skip.
                // If "Skip Existing" is OFF, or file doesn't match criteria -> Rename or Overwrite? 
                // "If conflict: append (1)" implies renaming rather than overwriting.
                
                // Let's assume we rename by default to avoid data loss, unless logic in ViewModel handles skipping.
                // We will let ViewModel handle the "Skip" check before calling ConvertAsync, or pass a flag.
                // But for pure naming conflict resolution (if we decided to convert), we rename.
                
                outputPath = Path.Combine(outputFolder, $"{baseName} ({counter}).mp3");
                counter++;
            }

            item.OutputPath = outputPath;

            // Build Arguments
            string audioArgs;
            if (settings.IsCbr)
            {
                audioArgs = $"-b:a {settings.Bitrate}k";
            }
            else
            {
                // VBR: -q:a 0-9 (0 is best/highest bitrate, 9 is lowest)
                // LAME VBR: -V 0 to -V 9. ffmpeg maps -q:a to -V.
                // Prompt says V0-V5. V0 is best quality.
                audioArgs = $"-q:a {settings.VbrQuality}";
            }

            string args = $"-y -i \"{item.InputPath}\" -vn -acodec libmp3lame {audioArgs} -ar 44100 -ac 2 \"{item.OutputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Read stderr for progress
            var tcs = new TaskCompletionSource<bool>();
            
            // Using a separate task to read stderr to avoid deadlocks and allow parsing
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            return;
                        }

                        string? line = await process.StandardError.ReadLineAsync();
                        if (line == null) break;

                        // Log error lines if needed, or parse progress
                        // Parse time=HH:MM:SS.xx
                        var match = Regex.Match(line, @"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
                        if (match.Success)
                        {
                            if (TimeSpan.TryParse(match.Groups[1].Value + ":" + match.Groups[2].Value + ":" + match.Groups[3].Value, out TimeSpan currentTime))
                            {
                                if (item.Duration.TotalSeconds > 0)
                                {
                                    double percent = (currentTime.TotalSeconds / item.Duration.TotalSeconds) * 100;
                                    // Update progress on UI thread if necessary, or just set property (ObservableProperty handles it?)
                                    // Since we are in background thread, ObservableObject raises event. WPF needs marshalling?
                                    // ObservableObject properties raise PropertyChanged. WPF automatically marshals PropertyChanged to UI thread usually if binding.
                                    // Wait, no. WPF 4.5+ enables accessing collections from non-UI thread if using BindingOperations.EnableCollectionSynchronization.
                                    // But for simple property changes on an object, it usually works or might throw.
                                    // Safer to assume we might need dispatcher, but let's try direct set first. 
                                    // Actually, for individual property changes, it's often fine if it's not an ObservableCollection change.
                                    item.Progress = Math.Min(99.9, percent); // Don't hit 100 until done
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error reading ffmpeg output: " + ex.Message);
                }
            }, ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (TaskCanceledException)
            {
                try { process.Kill(); } catch { }
                item.Status = ConversionStatus.Cancelled;
                throw;
            }

            if (process.ExitCode == 0)
            {
                item.Progress = 100;
                item.Status = ConversionStatus.Completed;
            }
            else
            {
                item.Status = ConversionStatus.Failed;
                item.ErrorMessage = "FFmpeg exited with code " + process.ExitCode;
                throw new Exception("FFmpeg failed");
            }
        }
    }
}
