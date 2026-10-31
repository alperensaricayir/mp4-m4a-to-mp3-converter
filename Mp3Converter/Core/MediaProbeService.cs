using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mp3Converter.Core
{
    /// <summary>
    /// Wraps ffprobe to read container/stream metadata as JSON.
    /// </summary>
    public class MediaProbeService
    {
        private readonly FfmpegService _ffmpeg;

        public MediaProbeService(FfmpegService ffmpeg)
        {
            _ffmpeg = ffmpeg;
        }

        public async Task<MediaInfo> ProbeAsync(string filePath, CancellationToken ct)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Dosya bulunamadı.", filePath);

            string ffprobe = _ffmpeg.FfprobePath ?? throw new InvalidOperationException("FFprobe bulunamadı.");

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -print_format json -show_format -show_streams \"{filePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("FFprobe başlatılamadı.");

            using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();

            string json = await stdoutTask;
            string err = await stderrTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(
                    $"FFprobe dosyayı okuyamadı: {(string.IsNullOrWhiteSpace(err) ? "bilinmeyen hata" : err.Trim())}");

            return Parse(filePath, json);
        }

        private static MediaInfo Parse(string filePath, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var streams = new List<MediaStreamInfo>();
            if (root.TryGetProperty("streams", out var streamsEl) && streamsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streamsEl.EnumerateArray())
                {
                    streams.Add(new MediaStreamInfo
                    {
                        Index = GetInt(s, "index") ?? streams.Count,
                        CodecType = GetString(s, "codec_type") ?? "",
                        CodecName = GetString(s, "codec_name") ?? "",
                        Duration = GetSeconds(s, "duration"),
                        StartTime = GetSeconds(s, "start_time"),
                        SampleRate = GetInt(s, "sample_rate"),
                        Channels = GetInt(s, "channels"),
                        Width = GetInt(s, "width"),
                        Height = GetInt(s, "height"),
                        BitRate = GetLong(s, "bit_rate"),
                    });
                }
            }

            TimeSpan? duration = null, start = null;
            long? bitRate = null;
            string formatName = "";
            if (root.TryGetProperty("format", out var fmt))
            {
                duration = GetSeconds(fmt, "duration");
                start = GetSeconds(fmt, "start_time");
                formatName = GetString(fmt, "format_name") ?? "";
                bitRate = GetLong(fmt, "bit_rate");
            }

            long fileSize = 0;
            try { fileSize = new FileInfo(filePath).Length; } catch { }

            return new MediaInfo
            {
                FilePath = filePath,
                FormatName = formatName,
                Duration = duration,
                StartTime = start,
                BitRate = bitRate,
                FileSize = fileSize,
                Streams = streams
            };
        }

        private static long? GetLong(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long i)) return i;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long j)) return j;
            return null;
        }

        private static string? GetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? GetInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int j)) return j;
            return null;
        }

        private static TimeSpan? GetSeconds(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return null;
            string? text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
            if (text == null || text == "N/A") return null;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && sec >= 0 && !double.IsInfinity(sec)
                ? TimeSpan.FromSeconds(sec)
                : null;
        }
    }
}
