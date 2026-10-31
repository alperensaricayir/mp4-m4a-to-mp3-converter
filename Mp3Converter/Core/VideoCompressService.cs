using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mp3Converter.Core
{
    public enum CompressMode
    {
        /// <summary>User picks a target size in MB; video bitrate is derived from it.</summary>
        TargetSize,
        /// <summary>Video bitrate is a percentage of the source bitrate.</summary>
        Quality
    }

    public enum ResolutionPreset
    {
        Original,
        P1080,
        P720,
        P480,
        P360
    }

    public enum CompressStage
    {
        Idle,
        Analyzing,
        Pass1,
        Encoding,
        Completed,
        Cancelled,
        Error
    }

    public sealed class CompressOptions
    {
        public string VideoPath { get; init; } = "";
        public string OutputFolder { get; init; } = "";
        public CompressMode Mode { get; init; } = CompressMode.TargetSize;
        public double TargetSizeMb { get; init; } = 50;
        /// <summary>Quality mode: percent of the source video bitrate to keep (e.g. 50).</summary>
        public int QualityPercent { get; init; } = 50;
        public ResolutionPreset Resolution { get; init; } = ResolutionPreset.Original;
        public int AudioBitrateKbps { get; init; } = 128;
        /// <summary>Two-pass ABR hits the target size much more precisely at ~2× encode time.</summary>
        public bool TwoPass { get; init; } = true;
    }

    public sealed class CompressEstimate
    {
        public int VideoBitrateKbps { get; init; }
        public int AudioBitrateKbps { get; init; }
        public bool CopyAudio { get; init; }
        public bool HasAudio { get; init; }
        public int? OutputHeight { get; init; }
        public long EstimatedBytes { get; init; }
        public long SourceBytes { get; init; }
        public string? Warning { get; init; }

        public double EstimatedMb => EstimatedBytes / 1024.0 / 1024.0;
        public double SourceMb => SourceBytes / 1024.0 / 1024.0;
        public double ReductionPercent => SourceBytes > 0 ? Math.Max(0, 100.0 - EstimatedBytes * 100.0 / SourceBytes) : 0;
    }

    public sealed class CompressProgress
    {
        public CompressStage Stage { get; init; }
        public double Percent { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class CompressResult
    {
        public string OutputPath { get; init; } = "";
        public long OutputBytes { get; init; }
        public CompressEstimate Estimate { get; init; } = new();
    }

    /// <summary>
    /// Shrinks a video to H.264/AAC MP4 at a bitrate derived from a target size or quality percentage,
    /// and predicts the output size before encoding.
    /// </summary>
    public class VideoCompressService
    {
        private const int MinVideoKbps = 100;
        private const double ContainerOverhead = 1.02; // MP4 muxing overhead, ~2%

        private readonly FfmpegService _ffmpeg;
        private readonly MediaProbeService _probe;

        public VideoCompressService(FfmpegService ffmpeg, MediaProbeService probe)
        {
            _ffmpeg = ffmpeg;
            _probe = probe;
        }

        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => _probe.ProbeAsync(path, ct);

        public static int? PresetHeight(ResolutionPreset p) => p switch
        {
            ResolutionPreset.P1080 => 1080,
            ResolutionPreset.P720 => 720,
            ResolutionPreset.P480 => 480,
            ResolutionPreset.P360 => 360,
            _ => null
        };

        /// <summary>Predicts output bitrates and size for the given source and options. Pure/cheap: safe to call on every UI change.</summary>
        public static CompressEstimate Estimate(MediaInfo info, CompressOptions o)
        {
            var video = info.FirstVideo;
            double seconds = info.EffectiveDuration.TotalSeconds;
            if (video == null || seconds <= 0)
            {
                return new CompressEstimate { SourceBytes = info.FileSize, Warning = "Video akışı veya süre okunamadı." };
            }

            // ---- resolution ----
            int? outHeight = null;
            double pixelFactor = 1.0;
            int? presetH = PresetHeight(o.Resolution);
            if (presetH.HasValue && video.Height.HasValue && video.Height.Value > presetH.Value)
            {
                outHeight = presetH.Value;
                // Bitrate need scales sub-linearly with pixel count.
                pixelFactor = Math.Pow((double)presetH.Value / video.Height.Value, 2 * 0.75);
            }

            // ---- audio ----
            bool hasAudio = info.HasAudio;
            bool copyAudio = false;
            int audioKbps = 0;
            if (hasAudio)
            {
                long srcAudio = info.EstimatedAudioBitRate;
                bool srcIsAac = string.Equals(info.FirstAudio!.CodecName, "aac", StringComparison.OrdinalIgnoreCase);
                if (srcIsAac && srcAudio > 0 && srcAudio <= o.AudioBitrateKbps * 1000L * 1.1)
                {
                    copyAudio = true;
                    audioKbps = (int)Math.Round(srcAudio / 1000.0);
                }
                else
                {
                    audioKbps = o.AudioBitrateKbps;
                }
            }

            // ---- video bitrate ----
            long srcVideoBps = info.EstimatedVideoBitRate;
            int videoKbps;
            string? warning = null;

            if (o.Mode == CompressMode.TargetSize)
            {
                double targetBits = o.TargetSizeMb * 1024 * 1024 * 8 / ContainerOverhead;
                videoKbps = (int)Math.Floor(targetBits / seconds / 1000.0) - audioKbps;
                if (videoKbps < MinVideoKbps)
                {
                    videoKbps = MinVideoKbps;
                    warning = "Hedef boyut bu süre için çok küçük; görüntü kalitesi çok düşük olur. Minimum bitrate uygulanacak.";
                }
            }
            else
            {
                if (srcVideoBps <= 0)
                {
                    srcVideoBps = 2_000_000;
                    warning = "Kaynak bitrate okunamadı; 2000 kbps varsayıldı.";
                }
                videoKbps = (int)Math.Round(srcVideoBps / 1000.0 * o.QualityPercent / 100.0 * pixelFactor);
                if (videoKbps < MinVideoKbps) videoKbps = MinVideoKbps;
            }

            if (srcVideoBps > 0 && videoKbps * 1000L >= srcVideoBps && warning == null)
                warning = "Seçilen ayarlar kaynaktan daha yüksek bitrate üretir; dosya küçülmeyebilir.";

            long estBytes = (long)((videoKbps + audioKbps) * 1000.0 * seconds / 8.0 * ContainerOverhead);

            return new CompressEstimate
            {
                VideoBitrateKbps = videoKbps,
                AudioBitrateKbps = audioKbps,
                CopyAudio = copyAudio,
                HasAudio = hasAudio,
                OutputHeight = outHeight,
                EstimatedBytes = estBytes,
                SourceBytes = info.FileSize,
                Warning = warning
            };
        }

        public static string BuildOutputPath(string videoPath, string outputFolder) =>
            FfmpegService.GetUniqueSuffixedPath(outputFolder, Path.GetFileNameWithoutExtension(videoPath) + "_KUCULTULMUS", ".mp4");

        public async Task<CompressResult> CompressAsync(
            CompressOptions o,
            MediaInfo? preProbed,
            IProgress<CompressProgress>? progress,
            Action<string>? log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(o.VideoPath)) throw new MergeException("Lütfen bir video dosyası seçin.");
            if (!File.Exists(o.VideoPath)) throw new MergeException("Video dosyası bulunamadı.", o.VideoPath);
            if (string.IsNullOrWhiteSpace(o.OutputFolder)) throw new MergeException("Lütfen bir çıktı konumu seçin.");
            if (_ffmpeg.FfmpegPath == null) throw new MergeException("FFmpeg bulunamadı.");
            if (_ffmpeg.FfprobePath == null) throw new MergeException("FFprobe bulunamadı.");

            string? outputPath = null;
            string passLog = Path.Combine(Path.GetTempPath(), $"mp3conv_x264_{Guid.NewGuid():N}");

            try
            {
                Report(progress, CompressStage.Analyzing, 0, "Dosya analiz ediliyor...");
                MediaInfo info = preProbed ?? await _probe.ProbeAsync(o.VideoPath, ct);
                var video = info.FirstVideo ?? throw new MergeException("Seçtiğiniz dosyada görüntü akışı bulunamadı.");
                var est = Estimate(info, o);
                if (est.Warning != null) log?.Invoke("Uyarı: " + est.Warning);

                log?.Invoke($"Kaynak: {info.FileSize / 1024.0 / 1024.0:0.0} MB, {info.EffectiveDuration:hh\\:mm\\:ss}, {video.Width}x{video.Height}, video≈{info.EstimatedVideoBitRate / 1000} kbps, ses≈{info.EstimatedAudioBitRate / 1000} kbps");
                log?.Invoke($"Hedef: video {est.VideoBitrateKbps} kbps, ses {(est.HasAudio ? (est.CopyAudio ? "copy" : $"aac {est.AudioBitrateKbps} kbps") : "yok")}, çözünürlük {(est.OutputHeight.HasValue ? est.OutputHeight + "p" : "orijinal")}, tahmini {est.EstimatedMb:0.0} MB");

                Directory.CreateDirectory(o.OutputFolder);
                outputPath = BuildOutputPath(o.VideoPath, o.OutputFolder);

                string vf = est.OutputHeight.HasValue ? $"-vf scale=-2:{est.OutputHeight.Value} " : "";
                int maxrate = (int)(est.VideoBitrateKbps * 1.5);
                int bufsize = est.VideoBitrateKbps * 3;
                string videoArgs = $"-c:v libx264 -preset medium -b:v {est.VideoBitrateKbps}k -maxrate {maxrate}k -bufsize {bufsize}k -pix_fmt yuv420p {vf}";
                string audioArgs = !est.HasAudio ? "-an"
                    : est.CopyAudio ? "-c:a copy"
                    : $"-c:a aac -b:a {est.AudioBitrateKbps}k";

                TimeSpan duration = info.EffectiveDuration;

                if (o.TwoPass)
                {
                    string pass1 = $"-hide_banner -y -i \"{o.VideoPath}\" -map 0:v:0 {videoArgs}-pass 1 -passlogfile \"{passLog}\" -an -f null NUL";
                    log?.Invoke("ffmpeg " + pass1);
                    Report(progress, CompressStage.Pass1, 0, "1. geçiş (analiz)...");
                    var p1 = new Progress<double>(p => Report(progress, CompressStage.Pass1, p / 2, $"1. geçiş (analiz)... %{p:0}"));
                    var r1 = await _ffmpeg.RunFfmpegAsync(pass1, duration, p1, log, ct);
                    if (r1.Cancelled || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (!r1.Success)
                    {
                        string d = FfmpegService.LastMeaningfulLine(r1.StderrTail);
                        throw new MergeException("1. geçiş başarısız oldu: " + d, d);
                    }

                    string pass2 = $"-hide_banner -y -i \"{o.VideoPath}\" -map 0:v:0 -map 0:a:0? {videoArgs}-pass 2 -passlogfile \"{passLog}\" {audioArgs} -movflags +faststart \"{outputPath}\"";
                    log?.Invoke("ffmpeg " + pass2);
                    Report(progress, CompressStage.Encoding, 50, "2. geçiş (sıkıştırma)...");
                    var p2 = new Progress<double>(p => Report(progress, CompressStage.Encoding, 50 + p / 2, $"2. geçiş (sıkıştırma)... %{p:0}"));
                    var r2 = await _ffmpeg.RunFfmpegAsync(pass2, duration, p2, log, ct);
                    if (r2.Cancelled || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (!r2.Success)
                    {
                        string d = FfmpegService.LastMeaningfulLine(r2.StderrTail);
                        throw new MergeException("Sıkıştırma başarısız oldu: " + d, d);
                    }
                }
                else
                {
                    string args = $"-hide_banner -y -i \"{o.VideoPath}\" -map 0:v:0 -map 0:a:0? {videoArgs}{audioArgs} -movflags +faststart \"{outputPath}\"";
                    log?.Invoke("ffmpeg " + args);
                    Report(progress, CompressStage.Encoding, 0, "Sıkıştırılıyor...");
                    var pr = new Progress<double>(p => Report(progress, CompressStage.Encoding, p, $"Sıkıştırılıyor... %{p:0}"));
                    var r = await _ffmpeg.RunFfmpegAsync(args, duration, pr, log, ct);
                    if (r.Cancelled || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (!r.Success)
                    {
                        string d = FfmpegService.LastMeaningfulLine(r.StderrTail);
                        throw new MergeException("Sıkıştırma başarısız oldu: " + d, d);
                    }
                }

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    throw new MergeException("Çıktı dosyası oluşturulamadı.");

                long outBytes = new FileInfo(outputPath).Length;
                log?.Invoke($"Tamamlandı: {outBytes / 1024.0 / 1024.0:0.0} MB (tahmin {est.EstimatedMb:0.0} MB)");
                Report(progress, CompressStage.Completed, 100, "Tamamlandı");

                string finalPath = outputPath;
                outputPath = null;
                return new CompressResult { OutputPath = finalPath, OutputBytes = outBytes, Estimate = est };
            }
            catch (OperationCanceledException)
            {
                Report(progress, CompressStage.Cancelled, 0, "İptal edildi");
                throw;
            }
            catch (MergeException)
            {
                Report(progress, CompressStage.Error, 0, "Hata oluştu");
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                Report(progress, CompressStage.Error, 0, "Hata oluştu");
                throw new MergeException("Çıktı konumuna yazılamıyor.", ex.ToString());
            }
            catch (Exception ex)
            {
                Report(progress, CompressStage.Error, 0, "Hata oluştu");
                throw new MergeException("Beklenmeyen bir hata oluştu: " + ex.Message, ex.ToString());
            }
            finally
            {
                if (outputPath != null) TryDelete(outputPath);
                // x264 two-pass artifacts
                TryDelete(passLog + "-0.log");
                TryDelete(passLog + "-0.log.mbtree");
                TryDelete(passLog + "-0.log.temp");
                TryDelete(passLog + "-0.log.mbtree.temp");
            }
        }

        private static void Report(IProgress<CompressProgress>? p, CompressStage stage, double percent, string message) =>
            p?.Report(new CompressProgress { Stage = stage, Percent = percent, Message = message });

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
