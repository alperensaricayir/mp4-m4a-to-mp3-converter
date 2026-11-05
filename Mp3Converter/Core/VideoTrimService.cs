using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mp3Converter.Core
{
    public enum TrimMode
    {
        Quality,
        TargetSize
    }

    public enum TrimQuality
    {
        Same,
        VeryHigh,
        High,
        Medium,
        Low
    }

    public enum TrimStage
    {
        Idle,
        Analyzing,
        Trimming,
        Completed,
        Cancelled,
        Error
    }

    public sealed class TrimOptions
    {
        public string VideoPath { get; init; } = "";
        public string OutputFolder { get; init; } = "";
        public TimeSpan Start { get; init; }
        public TimeSpan End { get; init; }
        public TrimMode Mode { get; init; } = TrimMode.Quality;
        public TrimQuality Quality { get; init; } = TrimQuality.Same;
        public double TargetSizeMb { get; init; }
    }

    public sealed class TrimEstimate
    {
        public TimeSpan ClipDuration { get; init; }
        public bool StreamCopy { get; init; }
        public int VideoBitrateKbps { get; init; }
        public int AudioBitrateKbps { get; init; }
        public int Crf { get; init; }
        public long EstimatedBytes { get; init; }
        public long SourceBytes { get; init; }
        public string? Warning { get; init; }
        public string OutputExtension { get; init; } = ".mp4";

        public double EstimatedMb => EstimatedBytes / 1024.0 / 1024.0;
        public double SourceMb => SourceBytes / 1024.0 / 1024.0;
    }

    public sealed class TrimProgress
    {
        public TrimStage Stage { get; init; }
        public double Percent { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class TrimResult
    {
        public string OutputPath { get; init; } = "";
        public long OutputBytes { get; init; }
        public TrimEstimate Estimate { get; init; } = new();
    }

    /// <summary>
    /// Cuts a time range out of a video. Estimate() is cheap and is called as the user
    /// edits start/end, quality, or target size — before they click Trim.
    /// </summary>
    public class VideoTrimService
    {
        private const int MinVideoKbps = 100;
        private const double ContainerOverhead = 1.02;

        private static readonly HashSet<string> CopyFriendlyVideo = new(StringComparer.OrdinalIgnoreCase)
            { "h264", "hevc", "mpeg4", "vp8", "vp9", "av1" };
        private static readonly HashSet<string> Mp4Video = new(StringComparer.OrdinalIgnoreCase)
            { "h264", "hevc", "mpeg4", "av1" };
        private static readonly HashSet<string> Mp4Audio = new(StringComparer.OrdinalIgnoreCase)
            { "aac", "mp3", "ac3", "eac3", "alac" };

        private readonly FfmpegService _ffmpeg;
        private readonly MediaProbeService _probe;

        public VideoTrimService(FfmpegService ffmpeg, MediaProbeService probe)
        {
            _ffmpeg = ffmpeg;
            _probe = probe;
        }

        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => _probe.ProbeAsync(path, ct);

        public static int QualityFactorPercent(TrimQuality q) => q switch
        {
            TrimQuality.Same => 100,
            TrimQuality.VeryHigh => 100,
            TrimQuality.High => 75,
            TrimQuality.Medium => 50,
            TrimQuality.Low => 28,
            _ => 50
        };

        public static int QualityCrf(TrimQuality q) => q switch
        {
            TrimQuality.Same => 18,
            TrimQuality.VeryHigh => 16,
            TrimQuality.High => 20,
            TrimQuality.Medium => 23,
            TrimQuality.Low => 28,
            _ => 23
        };

        public static TrimEstimate Estimate(MediaInfo info, TrimOptions o)
        {
            var video = info.FirstVideo;
            if (video == null)
                return new TrimEstimate { SourceBytes = info.FileSize, Warning = "Video akışı bulunamadı." };

            TimeSpan total = info.EffectiveDuration;
            if (total <= TimeSpan.Zero)
                return new TrimEstimate { SourceBytes = info.FileSize, Warning = "Video süresi okunamadı." };

            if (o.End <= o.Start)
                return new TrimEstimate { SourceBytes = info.FileSize, Warning = "Bitiş, başlangıçtan sonra olmalı." };
            if (o.Start < TimeSpan.Zero)
                return new TrimEstimate { SourceBytes = info.FileSize, Warning = "Başlangıç 0'dan küçük olamaz." };
            if (o.Start >= total)
                return new TrimEstimate { SourceBytes = info.FileSize, Warning = "Başlangıç video süresinin dışında." };

            TimeSpan end = o.End > total ? total : o.End;
            TimeSpan clip = end - o.Start;
            if (clip <= TimeSpan.Zero)
                return new TrimEstimate { SourceBytes = info.FileSize, Warning = "Kırpma süresi 0." };

            double clipSec = clip.TotalSeconds;
            string? warning = end < o.End ? "Bitiş, video sonuna çekildi." : null;

            bool canCopy = o.Mode == TrimMode.Quality
                           && o.Quality == TrimQuality.Same
                           && CopyFriendlyVideo.Contains(video.CodecName ?? "");

            if (canCopy)
            {
                long est = (long)(info.FileSize * (clipSec / Math.Max(0.001, total.TotalSeconds)));
                string ext = GuessCopyExtension(info);
                return new TrimEstimate
                {
                    ClipDuration = clip,
                    StreamCopy = true,
                    EstimatedBytes = Math.Max(est, 1024),
                    SourceBytes = info.FileSize,
                    Warning = warning ?? "Aynı kalite: yeniden encode yok (keyframe hizası ±1–2 sn sapabilir).",
                    OutputExtension = ext
                };
            }

            int audioKbps = info.HasAudio ? (int)Math.Max(64, info.EstimatedAudioBitRate / 1000) : 0;
            if (audioKbps > 192) audioKbps = 192;

            int videoKbps;
            int crf = QualityCrf(o.Quality);

            if (o.Mode == TrimMode.TargetSize)
            {
                if (o.TargetSizeMb <= 0)
                    return new TrimEstimate { ClipDuration = clip, SourceBytes = info.FileSize, Warning = "Geçerli bir hedef boyut girin (MB)." };

                double targetBits = o.TargetSizeMb * 1024 * 1024 * 8 / ContainerOverhead;
                videoKbps = (int)Math.Floor(targetBits / clipSec / 1000.0) - audioKbps;
                if (videoKbps < MinVideoKbps)
                {
                    videoKbps = MinVideoKbps;
                    warning = "Hedef boyut bu süre için çok küçük; görüntü kalitesi düşük olur.";
                }
                crf = 0;
            }
            else
            {
                long srcVideo = info.EstimatedVideoBitRate;
                if (srcVideo <= 0) srcVideo = 2_000_000;
                videoKbps = (int)Math.Round(srcVideo / 1000.0 * QualityFactorPercent(o.Quality) / 100.0);
                if (videoKbps < MinVideoKbps) videoKbps = MinVideoKbps;
            }

            long estBytes = (long)((videoKbps + audioKbps) * 1000.0 * clipSec / 8.0 * ContainerOverhead);
            return new TrimEstimate
            {
                ClipDuration = clip,
                StreamCopy = false,
                VideoBitrateKbps = videoKbps,
                AudioBitrateKbps = audioKbps,
                Crf = crf,
                EstimatedBytes = estBytes,
                SourceBytes = info.FileSize,
                Warning = warning,
                OutputExtension = ".mp4"
            };
        }

        public static string BuildOutputPath(string videoPath, string outputFolder, string extension) =>
            FfmpegService.GetUniqueSuffixedPath(
                outputFolder,
                Path.GetFileNameWithoutExtension(videoPath) + "_KIRPILMIS",
                extension);

        public async Task<TrimResult> TrimAsync(
            TrimOptions o,
            MediaInfo? preProbed,
            IProgress<TrimProgress>? progress,
            Action<string>? log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(o.VideoPath)) throw new MergeException("Lütfen bir video dosyası seçin.");
            if (!File.Exists(o.VideoPath)) throw new MergeException("Video dosyası bulunamadı.", o.VideoPath);
            if (string.IsNullOrWhiteSpace(o.OutputFolder)) throw new MergeException("Lütfen bir çıktı konumu seçin.");
            if (_ffmpeg.FfmpegPath == null) throw new MergeException("FFmpeg bulunamadı.");
            if (_ffmpeg.FfprobePath == null) throw new MergeException("FFprobe bulunamadı.");

            string? outputPath = null;
            try
            {
                Report(progress, TrimStage.Analyzing, 0, "Dosya analiz ediliyor...");
                MediaInfo info = preProbed ?? await _probe.ProbeAsync(o.VideoPath, ct);
                var video = info.FirstVideo ?? throw new MergeException("Seçtiğiniz dosyada görüntü akışı bulunamadı.");
                var est = Estimate(info, o);
                if (est.ClipDuration <= TimeSpan.Zero)
                    throw new MergeException(est.Warning ?? "Geçersiz kırpma aralığı.");
                if (est.Warning != null && est.EstimatedBytes <= 0 && !est.StreamCopy)
                    throw new MergeException(est.Warning);

                TimeSpan total = info.EffectiveDuration;
                TimeSpan start = o.Start < TimeSpan.Zero ? TimeSpan.Zero : o.Start;
                TimeSpan end = o.End > total ? total : o.End;
                TimeSpan clip = end - start;
                if (clip <= TimeSpan.Zero) throw new MergeException("Kırpma süresi geçersiz.");

                Directory.CreateDirectory(o.OutputFolder);
                outputPath = BuildOutputPath(o.VideoPath, o.OutputFolder, est.OutputExtension);
                log?.Invoke($"Kırpma: {Fmt(start)} → {Fmt(end)}  ({Fmt(clip)})  tahmin {est.EstimatedMb:0.0} MB");
                if (est.Warning != null) log?.Invoke("Uyarı: " + est.Warning);

                string startSec = start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                string durSec = clip.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

                string args;
                if (est.StreamCopy)
                {
                    args = $"-hide_banner -y -ss {startSec} -i \"{o.VideoPath}\" -t {durSec} -map 0 -c copy -avoid_negative_ts make_zero \"{outputPath}\"";
                }
                else
                {
                    string videoArgs = o.Mode == TrimMode.TargetSize
                        ? $"-c:v libx264 -preset medium -b:v {est.VideoBitrateKbps}k -maxrate {(int)(est.VideoBitrateKbps * 1.5)}k -bufsize {est.VideoBitrateKbps * 2}k -pix_fmt yuv420p -x264-params mbtree=0"
                        : $"-c:v libx264 -preset medium -crf {est.Crf} -pix_fmt yuv420p";

                    string audioArgs;
                    if (!info.HasAudio) audioArgs = "-an";
                    else if (Mp4Audio.Contains(info.FirstAudio!.CodecName ?? "")) audioArgs = "-c:a copy";
                    else audioArgs = $"-c:a aac -b:a {Math.Max(est.AudioBitrateKbps, 96)}k";

                    string mov = "-movflags +faststart";
                    args = $"-hide_banner -y -ss {startSec} -i \"{o.VideoPath}\" -t {durSec} -map 0:v:0 -map 0:a:0? {videoArgs} {audioArgs} {mov} \"{outputPath}\"";
                }

                log?.Invoke("ffmpeg " + args);
                Report(progress, TrimStage.Trimming, 0, "Kırpılıyor...");
                var pr = new Progress<double>(p =>
                    Report(progress, TrimStage.Trimming, p, p >= 99 ? "Dosya sonlandırılıyor..." : $"Kırpılıyor... %{p:0}"));

                var result = await _ffmpeg.RunFfmpegAsync(args, clip, pr, log, ct);
                if (result.Cancelled || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (!result.Success)
                {
                    string d = FfmpegService.LastMeaningfulLine(result.StderrTail);
                    throw new MergeException("Kırpma başarısız oldu: " + d, d);
                }

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    throw new MergeException("Çıktı dosyası oluşturulamadı.");

                long outBytes = new FileInfo(outputPath).Length;
                log?.Invoke($"Tamamlandı: {outBytes / 1024.0 / 1024.0:0.0} MB (tahmin {est.EstimatedMb:0.0} MB)");
                Report(progress, TrimStage.Completed, 100, "Tamamlandı");

                string finalPath = outputPath;
                outputPath = null;
                return new TrimResult { OutputPath = finalPath, OutputBytes = outBytes, Estimate = est };
            }
            catch (OperationCanceledException)
            {
                Report(progress, TrimStage.Cancelled, 0, "İptal edildi");
                throw;
            }
            catch (MergeException)
            {
                Report(progress, TrimStage.Error, 0, "Hata oluştu");
                throw;
            }
            catch (Exception ex)
            {
                Report(progress, TrimStage.Error, 0, "Hata oluştu");
                throw new MergeException("Beklenmeyen bir hata oluştu: " + ex.Message, ex.ToString());
            }
            finally
            {
                if (outputPath != null) TryDelete(outputPath);
            }
        }

        public static bool TryParseTime(string? text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim().Replace(',', '.');

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && !text.Contains(':'))
            {
                if (sec < 0) return false;
                time = TimeSpan.FromSeconds(sec);
                return true;
            }

            string[] parts = text.Split(':');
            try
            {
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s2)
                    && m >= 0 && s2 >= 0)
                {
                    time = TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s2);
                    return true;
                }
                if (parts.Length == 3
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mi)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s3)
                    && h >= 0 && mi >= 0 && s3 >= 0)
                {
                    time = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(mi) + TimeSpan.FromSeconds(s3);
                    return true;
                }
            }
            catch { return false; }

            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time);
        }

        public static string Fmt(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{t.Minutes:00}:{t.Seconds:00}";
        }

        private static string GuessCopyExtension(MediaInfo info)
        {
            string ext = Path.GetExtension(info.FilePath);
            if (string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mkv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mov", StringComparison.OrdinalIgnoreCase))
                return ext;

            var v = info.FirstVideo?.CodecName;
            if (v != null && Mp4Video.Contains(v)
                && (!info.HasAudio || Mp4Audio.Contains(info.FirstAudio!.CodecName ?? "")))
                return ".mp4";
            return ".mkv";
        }

        private static void Report(IProgress<TrimProgress>? p, TrimStage stage, double percent, string message) =>
            p?.Report(new TrimProgress { Stage = stage, Percent = percent, Message = message });

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
