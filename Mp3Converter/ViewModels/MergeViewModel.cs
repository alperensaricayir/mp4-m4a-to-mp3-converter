using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Mp3Converter.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Mp3Converter.ViewModels
{
    /// <summary>
    /// "Video + Ses Birleştir" tab: picks a video and an audio/media source and muxes them
    /// through <see cref="MediaMergeService"/>.
    /// </summary>
    public partial class MergeViewModel : ObservableObject
    {
        private const string VideoFilter =
            "Video Dosyaları|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4v|Tüm Dosyalar|*.*";

        private const string AudioFilter =
            "Ses / Medya Dosyaları|*.mp3;*.aac;*.m4a;*.wav;*.ogg;*.opus;*.flac;*.webm;*.mp4;*.mkv|Tüm Dosyalar|*.*";

        public sealed record MergeQualityOption(MergeQuality Quality, string Label);

        private readonly FfmpegService _ffmpeg;
        private readonly MediaMergeService _merge;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _probeCts;
        private MediaInfo? _videoInfo;
        private MediaInfo? _audioInfo;

        public IReadOnlyList<MergeQualityOption> Qualities { get; } = new[]
        {
            new MergeQualityOption(MergeQuality.Same, "Aynı kalite (video boyutu korunur)"),
            new MergeQualityOption(MergeQuality.VeryHigh, "Çok kaliteli"),
            new MergeQualityOption(MergeQuality.High, "Yüksek"),
            new MergeQualityOption(MergeQuality.Medium, "Orta"),
            new MergeQualityOption(MergeQuality.Low, "Düşük"),
        };

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        private string _videoPath = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        private string _audioPath = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        private string _outputFolder = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsMkv))]
        [NotifyPropertyChangedFor(nameof(IsMp4))]
        private MergeContainer _container = MergeContainer.Mkv;

        public bool IsMkv
        {
            get => Container == MergeContainer.Mkv;
            set { if (value) Container = MergeContainer.Mkv; }
        }

        public bool IsMp4
        {
            get => Container == MergeContainer.Mp4;
            set { if (value) Container = MergeContainer.Mp4; }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVideoLength))]
        [NotifyPropertyChangedFor(nameof(IsShortest))]
        private MergeDurationMode _durationMode = MergeDurationMode.VideoLength;

        public bool IsVideoLength
        {
            get => DurationMode == MergeDurationMode.VideoLength;
            set { if (value) DurationMode = MergeDurationMode.VideoLength; }
        }

        public bool IsShortest
        {
            get => DurationMode == MergeDurationMode.Shortest;
            set { if (value) DurationMode = MergeDurationMode.Shortest; }
        }

        [ObservableProperty]
        private int _audioOffsetMs;

        [ObservableProperty]
        private bool _useMetadataOffset;

        [ObservableProperty]
        private int _audioBitrateKbps = 192;

        [ObservableProperty]
        private MergeQualityOption _selectedQuality;

        [ObservableProperty]
        private string _estimateText = "Önce bir video seçin.";

        [ObservableProperty]
        private string _estimateDetailText = "";

        [ObservableProperty]
        private string? _estimateWarning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        private bool _isBusy;

        public bool IsIdle => !IsBusy;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _statusMessage = "Hazır";

        [ObservableProperty]
        private MergeStage _stage = MergeStage.Idle;

        [ObservableProperty]
        private string _logText = "";

        [ObservableProperty]
        private bool _isLogVisible;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
        private string? _lastOutputPath;

        public MergeViewModel(FfmpegService ffmpeg)
        {
            _ffmpeg = ffmpeg;
            _merge = new MediaMergeService(ffmpeg, new MediaProbeService(ffmpeg));
            _selectedQuality = Qualities[0];
        }

        partial void OnSelectedQualityChanged(MergeQualityOption value) => UpdateEstimate();
        partial void OnContainerChanged(MergeContainer value) => UpdateEstimate();
        partial void OnAudioBitrateKbpsChanged(int value) => UpdateEstimate();

        private void UpdateEstimate()
        {
            if (_videoInfo == null)
            {
                EstimateText = string.IsNullOrWhiteSpace(VideoPath) ? "Önce bir video seçin." : "Video analiz ediliyor...";
                EstimateDetailText = "";
                EstimateWarning = null;
                return;
            }

            var est = MediaMergeService.Estimate(_videoInfo, _audioInfo, SelectedQuality.Quality, Container, AudioBitrateKbps);
            EstimateText = $"Birleşince tahmini boyut: ~{est.EstimatedMb:0.0} MB";
            EstimateDetailText =
                $"Kaynak video {est.SourceVideoMb:0.0} MB  →  ~{est.EstimatedMb:0.0} MB   " +
                (est.CopyVideo
                    ? "görüntü kopyalanır (aynı kalite)"
                    : $"görüntü encode CRF {est.Crf}, ~{est.VideoBitrateKbps} kbps") +
                $", ses {est.AudioBitrateKbps} kbps";
            EstimateWarning = est.Warning;
        }

        private void Log(string message)
        {
            DiagnosticsLog.Write("[Merge] " + message);
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Current?.Dispatcher.InvokeAsync(() => LogText = UiLog.Append(LogText, line));
        }

        [RelayCommand]
        private async Task SelectVideo()
        {
            var dialog = new OpenFileDialog { Title = "Video Dosyası Seç", Filter = VideoFilter };
            if (dialog.ShowDialog() != true) return;

            VideoPath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(OutputFolder))
                OutputFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
            Log($"Video seçildi: {VideoPath}");
            await ProbeVideoAsync();
        }

        [RelayCommand]
        private async Task SelectAudio()
        {
            var dialog = new OpenFileDialog { Title = "Ses / Medya Dosyası Seç", Filter = AudioFilter };
            if (dialog.ShowDialog() != true) return;

            AudioPath = dialog.FileName;
            Log($"Ses kaynağı seçildi: {AudioPath}");
            await ProbeAudioAsync();
        }

        private async Task ProbeVideoAsync()
        {
            _videoInfo = null;
            UpdateEstimate();
            if (!_ffmpeg.CheckFfmpegExists()) return;
            try
            {
                _videoInfo = await _merge.ProbeAsync(VideoPath, CancellationToken.None);
                Log($"Video: {_videoInfo.FileSize / 1024.0 / 1024.0:0.0} MB, {_videoInfo.EffectiveDuration:hh\\:mm\\:ss}");
                UpdateEstimate();
            }
            catch (Exception ex)
            {
                EstimateText = "Video analiz edilemedi.";
                Log("Video analiz hatası: " + ex.Message);
            }
        }

        private async Task ProbeAudioAsync()
        {
            _audioInfo = null;
            UpdateEstimate();
            if (!_ffmpeg.CheckFfmpegExists() || string.IsNullOrWhiteSpace(AudioPath)) return;
            try
            {
                _audioInfo = await _merge.ProbeAsync(AudioPath, CancellationToken.None);
                UpdateEstimate();
            }
            catch (Exception ex)
            {
                Log("Ses analiz hatası: " + ex.Message);
                UpdateEstimate();
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

        [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
        private void OpenOutputFolder()
        {
            string? folder = LastOutputPath != null ? Path.GetDirectoryName(LastOutputPath) : OutputFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Log("Çıktı klasörü henüz yok.");
                return;
            }

            if (LastOutputPath != null && File.Exists(LastOutputPath))
                Process.Start("explorer.exe", $"/select,\"{LastOutputPath}\"");
            else
                Process.Start("explorer.exe", folder);
        }

        private bool CanOpenOutputFolder() =>
            !string.IsNullOrWhiteSpace(LastOutputPath) || (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder));

        [RelayCommand]
        private void ToggleLog() => IsLogVisible = !IsLogVisible;

        private bool CanMerge() =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(VideoPath) &&
            !string.IsNullOrWhiteSpace(AudioPath) &&
            !string.IsNullOrWhiteSpace(OutputFolder);

        [RelayCommand(CanExecute = nameof(CanMerge))]
        private async Task Merge()
        {
            if (IsBusy) return;

            if (!_ffmpeg.CheckFfmpegExists())
            {
                StatusMessage = "FFmpeg bulunamadı.";
                Stage = MergeStage.Error;
                Log("FFmpeg/FFprobe bulunamadı. Lütfen PATH'e ekleyin veya 'tools' klasörüne koyun.");
                MessageBox.Show(Application.Current.MainWindow,
                    "FFmpeg bulunamadı.\n\nffmpeg.exe ve ffprobe.exe dosyalarını sistem PATH'ine veya uygulamanın 'tools' klasörüne ekleyin.",
                    "FFmpeg bulunamadı", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsBusy = true;
            Progress = 0;
            LastOutputPath = null;
            _cts = new CancellationTokenSource();

            var options = new MergeOptions
            {
                VideoPath = VideoPath,
                AudioPath = AudioPath,
                OutputFolder = OutputFolder,
                Container = Container,
                DurationMode = DurationMode,
                AudioOffsetMs = AudioOffsetMs,
                UseMetadataOffset = UseMetadataOffset,
                AudioBitrateKbps = AudioBitrateKbps,
                Quality = SelectedQuality.Quality
            };

            var progress = new Progress<MergeProgress>(p =>
            {
                Stage = p.Stage;
                StatusMessage = p.Message;
                if (p.Stage is MergeStage.Merging or MergeStage.ExtractingAudio or MergeStage.Completed)
                    Progress = p.Percent;
            });

            Log("=== Birleştirme başlıyor ===");

            try
            {
                var result = await _merge.MergeAsync(options, progress, Log, _cts.Token);
                LastOutputPath = result.OutputPath;
                Progress = 100;
                Stage = MergeStage.Completed;
                StatusMessage = "Tamamlandı";
                try
                {
                    long bytes = new FileInfo(result.OutputPath).Length;
                    StatusMessage = $"Tamamlandı: {bytes / 1024.0 / 1024.0:0.0} MB";
                }
                catch { }
                Log($"Tamamlandı: {result.OutputPath}");
            }
            catch (OperationCanceledException)
            {
                Stage = MergeStage.Cancelled;
                StatusMessage = "İptal edildi";
                Progress = 0;
                Log("İşlem kullanıcı tarafından iptal edildi.");
            }
            catch (MergeException ex)
            {
                Stage = MergeStage.Error;
                StatusMessage = "Hata oluştu: " + ex.UserMessage;
                Progress = 0;
                Log("HATA: " + ex.UserMessage);
                if (ex.Message != ex.UserMessage) Log("Detay: " + ex.Message);
                IsLogVisible = true;
                MessageBox.Show(Application.Current.MainWindow, ex.UserMessage, "Birleştirme başarısız",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Stage = MergeStage.Error;
                StatusMessage = "Hata oluştu";
                Progress = 0;
                Log("BEKLENMEYEN HATA: " + ex);
                IsLogVisible = true;
                MessageBox.Show(Application.Current.MainWindow, ex.Message, "Birleştirme başarısız",
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
