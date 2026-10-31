using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mp3Converter.Core
{
    public enum MergeContainer
    {
        Mkv,
        Mp4
    }

    public enum MergeDurationMode
    {
        /// <summary>Output lasts as long as the video; longer audio is cut, shorter audio just ends.</summary>
        VideoLength,
        /// <summary>Output ends when the shorter of the two inputs ends.</summary>
        Shortest
    }

    public enum MergeStage
    {
        Idle,
        Analyzing,
        ExtractingAudio,
        Merging,
        Completed,
        Cancelled,
        Error
    }

    public sealed class MergeOptions
    {
        public string VideoPath { get; init; } = "";
        public string AudioPath { get; init; } = "";
        public string OutputFolder { get; init; } = "";
        public MergeContainer Container { get; init; } = MergeContainer.Mkv;
        public MergeDurationMode DurationMode { get; init; } = MergeDurationMode.VideoLength;
        /// <summary>Manual offset. Positive delays the audio, negative trims its start.</summary>
        public int AudioOffsetMs { get; init; }
        /// <summary>Add (audio.start_time - video.start_time) from container metadata to the offset.</summary>
        public bool UseMetadataOffset { get; init; }
        public int AudioBitrateKbps { get; init; } = 192;
    }

    public sealed class MergeProgress
    {
        public MergeStage Stage { get; init; }
        public double Percent { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class MergeResult
    {
        public string OutputPath { get; init; } = "";
        public MediaInfo Video { get; init; } = new();
        public MediaInfo Audio { get; init; } = new();
        public string Command { get; init; } = "";
    }

    /// <summary>Error with a short user-facing message; full details go to the log.</summary>
    public class MergeException : Exception
    {
        public string UserMessage { get; }

        public MergeException(string userMessage, string? details = null)
            : base(details ?? userMessage)
        {
            UserMessage = userMessage;
        }
    }

    /// <summary>
    /// Combines the video stream of one file with the audio stream of another using
    /// the shared <see cref="FfmpegService"/> runner and <see cref="MediaProbeService"/>.
    /// </summary>
    public class MediaMergeService
    {
        private static readonly HashSet<string> Mp4VideoCodecs = new(StringComparer.OrdinalIgnoreCase)
            { "h264", "hevc", "mpeg4", "av1", "mpeg2video" };

        private static readonly HashSet<string> Mp4AudioCodecs = new(StringComparer.OrdinalIgnoreCase)
            { "aac", "mp3", "ac3", "eac3", "alac" };

        private readonly FfmpegService _ffmpeg;
        private readonly MediaProbeService _probe;

        public MediaMergeService(FfmpegService ffmpeg, MediaProbeService probe)
        {
            _ffmpeg = ffmpeg;
            _probe = probe;
        }

        public static string GetExtension(MergeContainer container) =>
            container == MergeContainer.Mp4 ? ".mp4" : ".mkv";

        /// <summary>"Video_BIRLESMIS.mkv", then "_2", "_3", ... never overwriting.</summary>
        public static string BuildOutputPath(string videoPath, string outputFolder, MergeContainer container)
        {
            string baseName = Path.GetFileNameWithoutExtension(videoPath) + "_BIRLESMIS";
            string ext = GetExtension(container);
            string candidate = Path.Combine(outputFolder, baseName + ext);
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(outputFolder, $"{baseName}_{n}{ext}");
                n++;
            }
            return candidate;
        }

        public async Task<MergeResult> MergeAsync(
            MergeOptions options,
            IProgress<MergeProgress>? progress,
            Action<string>? log,
            CancellationToken ct)
        {
            Validate(options);

            if (_ffmpeg.FfmpegPath == null) throw new MergeException("FFmpeg bulunamadı.");
            if (_ffmpeg.FfprobePath == null) throw new MergeException("FFprobe bulunamadı.");

            string? tempAudio = null;
            string? outputPath = null;

            try
            {
                // ---------- 1) Analyze ----------
                Report(progress, MergeStage.Analyzing, 0, "Dosyalar analiz ediliyor...");
                log?.Invoke($"ffprobe: {options.VideoPath}");
                var video = await ProbeSafe(options.VideoPath, "Video dosyası", ct);
                LogInfo(log, "VIDEO", video);

                log?.Invoke($"ffprobe: {options.AudioPath}");
                var audio = await ProbeSafe(options.AudioPath, "Ses/medya dosyası", ct);
                LogInfo(log, "SES KAYNAĞI", audio);

                var videoStream = video.FirstVideo
                    ?? throw new MergeException("Seçtiğiniz video dosyasında görüntü akışı bulunamadı.");
                var audioStream = audio.FirstAudio
                    ?? throw new MergeException("Seçtiğiniz dosyada ses akışı bulunamadı.");

                if (video.HasAudio)
                    log?.Invoke("Not: Video dosyasının kendi sesi var; bu ses KULLANILMAYACAK.");

                // ---------- 2) Offset ----------
                int offsetMs = options.AudioOffsetMs;
                if (options.UseMetadataOffset)
                {
                    double vStart = (videoStream.StartTime ?? video.StartTime ?? TimeSpan.Zero).TotalSeconds;
                    double aStart = (audioStream.StartTime ?? audio.StartTime ?? TimeSpan.Zero).TotalSeconds;
                    int metaMs = (int)Math.Round((aStart - vStart) * 1000);
                    log?.Invoke($"Metadata offset: ses start={aStart:0.###}s, video start={vStart:0.###}s → {metaMs:+0;-0;0} ms");
                    offsetMs += metaMs;
                }
                log?.Invoke($"Uygulanacak toplam ses offseti: {offsetMs:+0;-0;0} ms");

                // ---------- 3) Codec decisions ----------
                bool copyVideo, copyAudio;
                if (options.Container == MergeContainer.Mkv)
                {
                    copyVideo = true;
                    copyAudio = true;
                }
                else
                {
                    copyVideo = Mp4VideoCodecs.Contains(videoStream.CodecName);
                    copyAudio = Mp4AudioCodecs.Contains(audioStream.CodecName);
                }

                string videoCodecArgs = copyVideo
                    ? "-c:v copy"
                    : "-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p";
                string audioCodecArgs = copyAudio
                    ? "-c:a copy"
                    : $"-c:a aac -b:a {options.AudioBitrateKbps}k";

                log?.Invoke($"Video codec: {videoStream.CodecName} → {(copyVideo ? "stream copy" : "libx264 (yeniden encode; MP4 ile uyumsuz)")}");
                log?.Invoke($"Ses codec:   {audioStream.CodecName} → {(copyAudio ? "stream copy" : $"aac {options.AudioBitrateKbps}k (yeniden encode)")}");

                // ---------- 4) Optional temp extraction ----------
                string audioInput = options.AudioPath;
                int audioStreamSpecifier = 0; // index among audio streams
                TimeSpan audioDuration = audioStream.Duration ?? audio.EffectiveDuration;

                if (audio.HasVideo)
                {
                    // The audio source is a video container (webm/mp4/mkv). Pull its audio
                    // into a temp Matroska-audio file (stream copy) so the final merge works
                    // from a clean, audio-only input regardless of the container's seeking quirks.
                    Report(progress, MergeStage.ExtractingAudio, 0, "Ses çıkarılıyor...");
                    tempAudio = Path.Combine(Path.GetTempPath(), $"mp3conv_merge_{Guid.NewGuid():N}.mka");
                    string extractArgs = $"-hide_banner -y -i \"{options.AudioPath}\" -vn -map 0:a:0 -c:a copy \"{tempAudio}\"";
                    log?.Invoke("ffmpeg " + extractArgs);

                    var extractProgress = new Progress<double>(p =>
                        Report(progress, MergeStage.ExtractingAudio, p, $"Ses çıkarılıyor... %{p:0}"));

                    var extractResult = await _ffmpeg.RunFfmpegAsync(extractArgs, audioDuration, extractProgress, log, ct);
                    ct.ThrowIfCancellationRequested();

                    if (extractResult.Success && File.Exists(tempAudio) && new FileInfo(tempAudio).Length > 0)
                    {
                        audioInput = tempAudio;
                        log?.Invoke($"Geçici ses dosyası: {tempAudio}");
                    }
                    else
                    {
                        log?.Invoke("Ses çıkarma başarısız; doğrudan kaynak dosyadan map edilecek.");
                        TryDelete(tempAudio);
                        tempAudio = null;
                    }
                }

                // ---------- 5) Build merge command ----------
                Directory.CreateDirectory(options.OutputFolder);
                outputPath = BuildOutputPath(options.VideoPath, options.OutputFolder, options.Container);

                TimeSpan videoDuration = videoStream.Duration ?? video.EffectiveDuration;
                TimeSpan targetDuration = options.DurationMode == MergeDurationMode.VideoLength
                    ? videoDuration
                    : Min(videoDuration, TimeSpan.FromMilliseconds(Math.Max(0, audioDuration.TotalMilliseconds - Math.Max(0, -offsetMs))));

                string audioInputOpts = "";
                if (offsetMs > 0)
                    audioInputOpts = $"-itsoffset {(offsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} ";
                else if (offsetMs < 0)
                    audioInputOpts = $"-ss {(-offsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} ";

                string durationArgs = options.DurationMode == MergeDurationMode.VideoLength && videoDuration > TimeSpan.Zero
                    ? $"-t {videoDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}"
                    : "-shortest";

                string containerArgs = options.Container == MergeContainer.Mp4 ? "-movflags +faststart" : "";

                string args =
                    $"-hide_banner -y -i \"{options.VideoPath}\" {audioInputOpts}-i \"{audioInput}\" " +
                    $"-map 0:v:0 -map 1:a:{audioStreamSpecifier} {videoCodecArgs} {audioCodecArgs} " +
                    $"-avoid_negative_ts make_zero {durationArgs} {containerArgs} \"{outputPath}\"";

                string command = "ffmpeg " + args;
                log?.Invoke(command);

                // ---------- 6) Run merge ----------
                Report(progress, MergeStage.Merging, 0, "Birleştiriliyor...");
                var mergeProgress = new Progress<double>(p =>
                    Report(progress, MergeStage.Merging, p, $"Birleştiriliyor... %{p:0}"));

                var result = await _ffmpeg.RunFfmpegAsync(args, targetDuration, mergeProgress, log, ct);

                if (result.Cancelled || ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);

                if (!result.Success)
                {
                    string detail = FfmpegService.LastMeaningfulLine(result.StderrTail);
                    throw new MergeException(FriendlyFfmpegError(detail, result.ExitCode), detail);
                }

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    throw new MergeException("Çıktı dosyası oluşturulamadı.");

                Report(progress, MergeStage.Completed, 100, "Tamamlandı");
                var finalPath = outputPath;
                outputPath = null; // keep it

                return new MergeResult { OutputPath = finalPath, Video = video, Audio = audio, Command = command };
            }
            catch (OperationCanceledException)
            {
                Report(progress, MergeStage.Cancelled, 0, "İptal edildi");
                throw;
            }
            catch (MergeException)
            {
                Report(progress, MergeStage.Error, 0, "Hata oluştu");
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                Report(progress, MergeStage.Error, 0, "Hata oluştu");
                throw new MergeException("Çıktı konumuna yazılamıyor.", ex.ToString());
            }
            catch (IOException ex)
            {
                Report(progress, MergeStage.Error, 0, "Hata oluştu");
                throw new MergeException("Dosya okunamadı veya yazılamadı: " + ex.Message, ex.ToString());
            }
            catch (Exception ex)
            {
                Report(progress, MergeStage.Error, 0, "Hata oluştu");
                throw new MergeException("Beklenmeyen bir hata oluştu: " + ex.Message, ex.ToString());
            }
            finally
            {
                // Remove partial output on failure/cancel and always remove the temp file.
                if (outputPath != null) TryDelete(outputPath);
                if (tempAudio != null) TryDelete(tempAudio);
            }
        }

        private static void Validate(MergeOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.VideoPath)) throw new MergeException("Lütfen bir video dosyası seçin.");
            if (string.IsNullOrWhiteSpace(o.AudioPath)) throw new MergeException("Lütfen bir ses/medya dosyası seçin.");
            if (!File.Exists(o.VideoPath)) throw new MergeException("Video dosyası bulunamadı.", o.VideoPath);
            if (!File.Exists(o.AudioPath)) throw new MergeException("Ses/medya dosyası bulunamadı.", o.AudioPath);
            if (string.IsNullOrWhiteSpace(o.OutputFolder)) throw new MergeException("Lütfen bir çıktı konumu seçin.");
        }

        private async Task<MediaInfo> ProbeSafe(string path, string label, CancellationToken ct)
        {
            try
            {
                return await _probe.ProbeAsync(path, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (MergeException) { throw; }
            catch (Exception ex)
            {
                throw new MergeException($"{label} analiz edilemedi (FFprobe).", ex.ToString());
            }
        }

        private static void LogInfo(Action<string>? log, string label, MediaInfo info)
        {
            if (log == null) return;
            log($"[{label}] {Path.GetFileName(info.FilePath)}  format={info.FormatName}  " +
                $"süre={(info.Duration.HasValue ? info.Duration.Value.ToString(@"hh\:mm\:ss\.fff") : "?")}  " +
                $"start={(info.StartTime.HasValue ? info.StartTime.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s" : "?")}");
            foreach (var s in info.Streams) log("   " + s);
        }

        private static string FriendlyFfmpegError(string detail, int exitCode)
        {
            string d = detail.ToLowerInvariant();
            if (d.Contains("permission denied") || d.Contains("access is denied"))
                return "Çıktı dosyasına yazılamıyor (erişim reddedildi).";
            if (d.Contains("no space left"))
                return "Diskte yeterli alan yok.";
            if (d.Contains("could not find tag") || d.Contains("not supported in") || d.Contains("unsupported codec") || d.Contains("incorrect codec parameters"))
                return "Codec bu çıktı formatıyla uyumlu değil. MKV formatını deneyin.";
            if (d.Contains("no such file") || d.Contains("does not exist"))
                return "Dosya bulunamadı.";
            if (d.Contains("invalid data found"))
                return "Dosya okunamadı veya bozuk.";
            return $"FFmpeg hata koduyla sonlandı ({exitCode}).";
        }

        private static void Report(IProgress<MergeProgress>? p, MergeStage stage, double percent, string message) =>
            p?.Report(new MergeProgress { Stage = stage, Percent = percent, Message = message });

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
