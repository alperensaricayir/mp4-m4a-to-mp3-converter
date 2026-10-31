using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Mp3Converter.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Mp3Converter.ViewModels
{
    public sealed record ResolutionOption(ResolutionPreset Preset, string Label);
    public sealed record QualityOption(int Percent, string Label);

    /// <summary>"Video Küçült" tab: shrinks a video and shows the predicted output size live.</summary>
    public partial class CompressViewModel : ObservableObject
    {
        private const string VideoFilter =
            "Video Dosyaları|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4v|Tüm Dosyalar|*.*";

        private readonly FfmpegService _ffmpeg;
        private readonly VideoCompressService _compress;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _probeCts;
        private MediaInfo? _info;

        public IReadOnlyList<ResolutionOption> Resolutions { get; } = new[]
        {
            new ResolutionOption(ResolutionPreset.Original, "Orijinal"),
            new ResolutionOption(ResolutionPreset.P1080, "1080p"),
            new ResolutionOption(ResolutionPreset.P720, "720p"),
            new ResolutionOption(ResolutionPreset.P480, "480p"),
            new ResolutionOption(ResolutionPreset.P360, "360p"),
        };

        public IReadOnlyList<QualityOption> Qualities { get; } = new[]
        {
            new QualityOption(75, "Yüksek (%75 bitrate)"),
            new QualityOption(50, "Orta (%50 bitrate)"),
            new QualityOption(30, "Düşük (%30 bitrate)"),
            new QualityOption(15, "Çok düşük (%15 bitrate)"),
        };

        public IReadOnlyList<int> AudioBitrates { get; } = new[] { 64, 96, 128, 160, 192 };

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
        private string _videoPath = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
        private string _outputFolder = "";

        [ObservableProperty]
        private string _sourceInfoText = "Henüz dosya seçilmedi.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTargetSizeMode))]
        [NotifyPropertyChangedFor(nameof(IsQualityMode))]
        private CompressMode _mode = CompressMode.TargetSize;

        public bool IsTargetSizeMode
        {
            get => Mode == CompressMode.TargetSize;
            set { if (value) Mode = CompressMode.TargetSize; }
        }

        public bool IsQualityMode
        {
            get => Mode == CompressMode.Quality;
            set { if (value) Mode = CompressMode.Quality; }
        }

        [ObservableProperty]
        private string _targetSizeMbText = "50";

        [ObservableProperty]
        private QualityOption _selectedQuality;

        [ObservableProperty]
        private ResolutionOption _selectedResolution;

        [ObservableProperty]
        private int _audioBitrateKbps = 128;

        [ObservableProperty]
        private bool _twoPass = true;

        [ObservableProperty]
        private string _estimateText = "Önce bir video seçin.";

        [ObservableProperty]
        private string _estimateDetailText = "";

        [ObservableProperty]
        private string? _estimateWarning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        private bool _isBusy;

        public bool IsIdle => !IsBusy;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _statusMessage = "Hazır";

        [ObservableProperty]
        private CompressStage _stage = CompressStage.Idle;

        [ObservableProperty]
        private string _logText = "";

        [ObservableProperty]
        private bool _isLogVisible;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
        private string? _lastOutputPath;

        public CompressViewModel(FfmpegService ffmpeg)
        {
            _ffmpeg = ffmpeg;
            _compress = new VideoCompressService(ffmpeg, new MediaProbeService(ffmpeg));
            _selectedQuality = Qualities[1];
            _selectedResolution = Resolutions[0];
        }

        // Any option change refreshes the live estimate.
        partial void OnModeChanged(CompressMode value) => UpdateEstimate();
        partial void OnTargetSizeMbTextChanged(string value) => UpdateEstimate();
        partial void OnSelectedQualityChanged(QualityOption value) => UpdateEstimate();
        partial void OnSelectedResolutionChanged(ResolutionOption value) => UpdateEstimate();
        partial void OnAudioBitrateKbpsChanged(int value) => UpdateEstimate();

        private void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Current?.Dispatcher.InvokeAsync(() => LogText += line + Environment.NewLine);
        }

        private CompressOptions BuildOptions()
        {
            double.TryParse(TargetSizeMbText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb);
            return new CompressOptions
            {
                VideoPath = VideoPath,
                OutputFolder = OutputFolder,
                Mode = Mode,
                TargetSizeMb = mb,
                QualityPercent = SelectedQuality.Percent,
                Resolution = SelectedResolution.Preset,
                AudioBitrateKbps = AudioBitrateKbps,
                TwoPass = TwoPass
            };
        }

        private void UpdateEstimate()
        {
            if (_info == null)
            {
                EstimateText = string.IsNullOrWhiteSpace(VideoPath) ? "Önce bir video seçin." : "Dosya analiz ediliyor...";
                EstimateDetailText = "";
                EstimateWarning = null;
                return;
            }

            var o = BuildOptions();
            if (o.Mode == CompressMode.TargetSize && o.TargetSizeMb <= 0)
            {
                EstimateText = "Geçerli bir hedef boyut girin (MB).";
                EstimateDetailText = "";
                EstimateWarning = null;
                return;
            }

            var est = VideoCompressService.Estimate(_info, o);
            EstimateText = $"Tahmini çıktı boyutu: ~{est.EstimatedMb:0.0} MB";
            EstimateDetailText =
                $"{est.SourceMb:0.0} MB → ~{est.EstimatedMb:0.0} MB  (%{est.ReductionPercent:0} küçülme)   " +
                $"video {est.VideoBitrateKbps} kbps, ses {(est.HasAudio ? (est.CopyAudio ? "kopya" : $"{est.AudioBitrateKbps} kbps") : "yok")}, " +
                $"çözünürlük {(est.OutputHeight.HasValue ? est.OutputHeight + "p" : "orijinal")}";
            EstimateWarning = est.Warning;
        }

        [RelayCommand]
        private async Task SelectVideo()
        {
            var dialog = new OpenFileDialog { Title = "Küçültülecek Videoyu Seç", Filter = VideoFilter };
            if (dialog.ShowDialog() != true) return;

            VideoPath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(OutputFolder))
                OutputFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
            Log($"Video seçildi: {VideoPath}");
            await ProbeSelectedAsync();
        }

        private async Task ProbeSelectedAsync()
        {
            _probeCts?.Cancel();
            _probeCts = new CancellationTokenSource();
            var ct = _probeCts.Token;

            _info = null;
            SourceInfoText = "Dosya analiz ediliyor...";
            UpdateEstimate();

            if (!_ffmpeg.CheckFfmpegExists())
            {
                SourceInfoText = "FFmpeg bulunamadı.";
                EstimateText = "FFmpeg bulunamadı; tahmin yapılamıyor.";
                Log("FFmpeg/FFprobe bulunamadı.");
                return;
            }

            try
            {
                var info = await _compress.ProbeAsync(VideoPath, ct);
                if (ct.IsCancellationRequested) return;
                _info = info;

                var v = info.FirstVideo;
                if (v == null)
                {
                    SourceInfoText = "Bu dosyada görüntü akışı yok.";
                    EstimateText = "Görüntü akışı bulunamadı.";
                    Log("Görüntü akışı bulunamadı.");
                    return;
                }

                SourceInfoText =
                    $"{info.FileSize / 1024.0 / 1024.0:0.0} MB  •  {info.EffectiveDuration:hh\\:mm\\:ss}  •  {v.Width}x{v.Height} {v.CodecName}  •  " +
                    $"video ≈{info.EstimatedVideoBitRate / 1000} kbps  •  ses {(info.HasAudio ? $"{info.FirstAudio!.CodecName} ≈{info.EstimatedAudioBitRate / 1000} kbps" : "yok")}";
                Log("ffprobe: " + SourceInfoText);
                foreach (var s in info.Streams) Log("   " + s);
                UpdateEstimate();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SourceInfoText = "Dosya analiz edilemedi.";
                EstimateText = "Analiz başarısız.";
                Log("Analiz hatası: " + ex.Message);
            }
        }

        [RelayCommand]
        private void SelectOutputFolder()
        {
            var dialog = new OpenFolderDialog { Title = "Çıktı Konumu Seç" };
            if (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder))
                dialog.InitialDirectory = OutputFolder;
            if (dialog.ShowDialog() == true)
                OutputFolder = dialog.FolderName;
        }

        private bool CanOpenOutputFolder() =>
            !string.IsNullOrWhiteSpace(LastOutputPath) || (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder));

        [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
        private void OpenOutputFolder()
        {
            if (LastOutputPath != null && File.Exists(LastOutputPath))
                Process.Start("explorer.exe", $"/select,\"{LastOutputPath}\"");
            else if (Directory.Exists(OutputFolder))
                Process.Start("explorer.exe", OutputFolder);
        }

        [RelayCommand]
        private void ToggleLog() => IsLogVisible = !IsLogVisible;

        private bool CanCompress() =>
            !IsBusy && !string.IsNullOrWhiteSpace(VideoPath) && !string.IsNullOrWhiteSpace(OutputFolder);

        [RelayCommand(CanExecute = nameof(CanCompress))]
        private async Task Compress()
        {
            if (IsBusy) return;

            if (!_ffmpeg.CheckFfmpegExists())
            {
                StatusMessage = "FFmpeg bulunamadı.";
                Stage = CompressStage.Error;
                MessageBox.Show(Application.Current.MainWindow,
                    "FFmpeg bulunamadı.\n\nffmpeg.exe ve ffprobe.exe dosyalarını sistem PATH'ine veya uygulamanın 'tools' klasörüne ekleyin.",
                    "FFmpeg bulunamadı", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var options = BuildOptions();
            if (options.Mode == CompressMode.TargetSize && options.TargetSizeMb <= 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "Geçerli bir hedef boyut girin (MB).", "Eksik bilgi",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            Progress = 0;
            LastOutputPath = null;
            _cts = new CancellationTokenSource();

            var progress = new Progress<CompressProgress>(p =>
            {
                Stage = p.Stage;
                StatusMessage = p.Message;
                if (p.Stage is CompressStage.Pass1 or CompressStage.Encoding or CompressStage.Completed)
                    Progress = p.Percent;
            });

            Log("=== Küçültme başlıyor ===");
            try
            {
                var result = await _compress.CompressAsync(options, _info, progress, Log, _cts.Token);
                LastOutputPath = result.OutputPath;
                Progress = 100;
                Stage = CompressStage.Completed;
                StatusMessage = $"Tamamlandı: {result.OutputBytes / 1024.0 / 1024.0:0.0} MB (tahmin {result.Estimate.EstimatedMb:0.0} MB)";
                Log($"Çıktı: {result.OutputPath}");
            }
            catch (OperationCanceledException)
            {
                Stage = CompressStage.Cancelled;
                StatusMessage = "İptal edildi";
                Progress = 0;
                Log("İşlem kullanıcı tarafından iptal edildi.");
            }
            catch (MergeException ex)
            {
                Stage = CompressStage.Error;
                StatusMessage = "Hata oluştu: " + ex.UserMessage;
                Progress = 0;
                Log("HATA: " + ex.UserMessage);
                if (ex.Message != ex.UserMessage) Log("Detay: " + ex.Message);
                IsLogVisible = true;
                MessageBox.Show(Application.Current.MainWindow, ex.UserMessage, "Küçültme başarısız",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Stage = CompressStage.Error;
                StatusMessage = "Hata oluştu";
                Progress = 0;
                Log("BEKLENMEYEN HATA: " + ex);
                IsLogVisible = true;
                MessageBox.Show(Application.Current.MainWindow, ex.Message, "Küçültme başarısız",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private bool CanCancel() => IsBusy;

        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void Cancel()
        {
            if (_cts is { IsCancellationRequested: false })
            {
                StatusMessage = "İptal ediliyor...";
                Log("İptal istendi; FFmpeg işlemi sonlandırılıyor.");
                _cts.Cancel();
            }
        }
    }
}
