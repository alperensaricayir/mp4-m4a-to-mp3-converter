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
    public sealed record TrimQualityOption(TrimQuality Quality, string Label);

    public partial class TrimViewModel : ObservableObject
    {
        private const string VideoFilter =
            "Video Dosyaları|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4v|Tüm Dosyalar|*.*";

        private readonly FfmpegService _ffmpeg;
        private readonly VideoTrimService _trim;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _probeCts;
        private MediaInfo? _info;

        public IReadOnlyList<TrimQualityOption> Qualities { get; } = new[]
        {
            new TrimQualityOption(TrimQuality.Same, "Aynı kalite (bilgisayar / kopya)"),
            new TrimQualityOption(TrimQuality.VeryHigh, "Çok kaliteli"),
            new TrimQualityOption(TrimQuality.High, "Yüksek"),
            new TrimQualityOption(TrimQuality.Medium, "Orta"),
            new TrimQualityOption(TrimQuality.Low, "Düşük"),
        };

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TrimCommand))]
        private string _videoPath = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TrimCommand))]
        private string _outputFolder = "";

        [ObservableProperty]
        private string _sourceInfoText = "Henüz dosya seçilmedi.";

        [ObservableProperty]
        private string _durationText = "";

        [ObservableProperty]
        private string _startText = "00:00:00";

        [ObservableProperty]
        private string _endText = "00:00:00";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsQualityMode))]
        [NotifyPropertyChangedFor(nameof(IsTargetSizeMode))]
        private TrimMode _mode = TrimMode.Quality;

        public bool IsQualityMode
        {
            get => Mode == TrimMode.Quality;
            set { if (value) Mode = TrimMode.Quality; }
        }

        public bool IsTargetSizeMode
        {
            get => Mode == TrimMode.TargetSize;
            set { if (value) Mode = TrimMode.TargetSize; }
        }

        [ObservableProperty]
        private TrimQualityOption _selectedQuality;

        [ObservableProperty]
        private string _targetSizeMbText = "50";

        [ObservableProperty]
        private string _estimateText = "Önce bir video seçin.";

        [ObservableProperty]
        private string _estimateDetailText = "";

        [ObservableProperty]
        private string? _estimateWarning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyCanExecuteChangedFor(nameof(TrimCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        private bool _isBusy;

        public bool IsIdle => !IsBusy;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _statusMessage = "Hazır";

        [ObservableProperty]
        private TrimStage _stage = TrimStage.Idle;

        [ObservableProperty]
        private string _logText = "";

        [ObservableProperty]
        private bool _isLogVisible;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
        private string? _lastOutputPath;

        public TrimViewModel(FfmpegService ffmpeg)
        {
            _ffmpeg = ffmpeg;
            _trim = new VideoTrimService(ffmpeg, new MediaProbeService(ffmpeg));
            _selectedQuality = Qualities[0];
        }

        partial void OnModeChanged(TrimMode value) => UpdateEstimate();
        partial void OnSelectedQualityChanged(TrimQualityOption value) => UpdateEstimate();
        partial void OnTargetSizeMbTextChanged(string value) => UpdateEstimate();
        partial void OnStartTextChanged(string value) => UpdateEstimate();
        partial void OnEndTextChanged(string value) => UpdateEstimate();

        private void Log(string message)
        {
            DiagnosticsLog.Write("[Trim] " + message);
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Current?.Dispatcher.InvokeAsync(() => LogText = UiLog.Append(LogText, line));
        }

        private bool TryBuildOptions(out TrimOptions options, out string? error)
        {
            options = new TrimOptions();
            error = null;
            if (!VideoTrimService.TryParseTime(StartText, out var start))
            {
                error = "Başlangıç saati geçersiz. Örn: 00:01:30 veya 90";
                return false;
            }
            if (!VideoTrimService.TryParseTime(EndText, out var end))
            {
                error = "Bitiş saati geçersiz. Örn: 00:05:00";
                return false;
            }
            double.TryParse(TargetSizeMbText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb);
            options = new TrimOptions
            {
                VideoPath = VideoPath,
                OutputFolder = OutputFolder,
                Start = start,
                End = end,
                Mode = Mode,
                Quality = SelectedQuality.Quality,
                TargetSizeMb = mb
            };
            return true;
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

            if (!TryBuildOptions(out var o, out var parseError))
            {
                EstimateText = parseError ?? "Geçersiz zaman.";
                EstimateDetailText = "";
                EstimateWarning = parseError;
                return;
            }

            var est = VideoTrimService.Estimate(_info, o);
            if (est.ClipDuration <= TimeSpan.Zero)
            {
                EstimateText = est.Warning ?? "Geçersiz aralık.";
                EstimateDetailText = "";
                EstimateWarning = est.Warning;
                return;
            }

            EstimateText = $"Kırpınca tahmini boyut: ~{est.EstimatedMb:0.0} MB";
            string qualityBit = est.StreamCopy
                ? "akış kopyası (yeniden encode yok)"
                : o.Mode == TrimMode.TargetSize
                    ? $"hedef {o.TargetSizeMb:0.#} MB, video {est.VideoBitrateKbps} kbps"
                    : $"{SelectedQuality.Label}, CRF {est.Crf}";
            EstimateDetailText =
                $"Aralık {VideoTrimService.Fmt(o.Start)} → {VideoTrimService.Fmt(o.Start + est.ClipDuration)}  ({VideoTrimService.Fmt(est.ClipDuration)})   " +
                qualityBit;
            EstimateWarning = est.Warning;
        }

        [RelayCommand]
        private async Task SelectVideo()
        {
            var dialog = new OpenFileDialog { Title = "Kırpılacak Videoyu Seç", Filter = VideoFilter };
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
            DurationText = "";
            UpdateEstimate();

            if (!_ffmpeg.CheckFfmpegExists())
            {
                SourceInfoText = "FFmpeg bulunamadı.";
                EstimateText = "FFmpeg bulunamadı; tahmin yapılamıyor.";
                return;
            }

            try
            {
                var info = await _trim.ProbeAsync(VideoPath, ct);
                if (ct.IsCancellationRequested) return;
                _info = info;
                var v = info.FirstVideo;
                if (v == null)
                {
                    SourceInfoText = "Bu dosyada görüntü akışı yok.";
                    EstimateText = "Görüntü akışı bulunamadı.";
                    return;
                }

                DurationText = VideoTrimService.Fmt(info.EffectiveDuration);
                StartText = "00:00:00";
                EndText = FormatHms(info.EffectiveDuration);
                SourceInfoText =
                    $"{info.FileSize / 1024.0 / 1024.0:0.0} MB  •  {DurationText}  •  {v.Width}x{v.Height} {v.CodecName}";
                Log("ffprobe: " + SourceInfoText);
                UpdateEstimate();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SourceInfoText = "Dosya analiz edilemedi.";
                Log("Analiz hatası: " + ex.Message);
            }
        }

        private static string FormatHms(TimeSpan t) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

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

        private bool CanTrim() =>
            !IsBusy && !string.IsNullOrWhiteSpace(VideoPath) && !string.IsNullOrWhiteSpace(OutputFolder);

        [RelayCommand(CanExecute = nameof(CanTrim))]
        private async Task Trim()
        {
            if (IsBusy) return;
            if (!_ffmpeg.CheckFfmpegExists())
            {
                StatusMessage = "FFmpeg bulunamadı.";
                MessageBox.Show(Application.Current.MainWindow, "FFmpeg bulunamadı.", "FFmpeg bulunamadı",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!TryBuildOptions(out var options, out var parseError))
            {
                MessageBox.Show(Application.Current.MainWindow, parseError, "Eksik bilgi",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_info != null)
            {
                var est = VideoTrimService.Estimate(_info, options);
                if (est.ClipDuration <= TimeSpan.Zero)
                {
                    MessageBox.Show(Application.Current.MainWindow, est.Warning ?? "Geçersiz kırpma aralığı.", "Kırpma",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (options.Mode == TrimMode.TargetSize && options.TargetSizeMb <= 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "Geçerli bir hedef boyut girin (MB).", "Eksik bilgi",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            Progress = 0;
            LastOutputPath = null;
            _cts = new CancellationTokenSource();
            var progress = new Progress<TrimProgress>(p =>
            {
                Stage = p.Stage;
                StatusMessage = p.Message;
                if (p.Stage is TrimStage.Trimming or TrimStage.Completed)
                    Progress = p.Percent;
            });

            Log("=== Kırpma başlıyor ===");
            try
            {
                var result = await _trim.TrimAsync(options, _info, progress, Log, _cts.Token);
                LastOutputPath = result.OutputPath;
                Progress = 100;
                Stage = TrimStage.Completed;
                StatusMessage = $"Tamamlandı: {result.OutputBytes / 1024.0 / 1024.0:0.0} MB (tahmin {result.Estimate.EstimatedMb:0.0} MB)";
                Log($"Çıktı: {result.OutputPath}");
            }
            catch (OperationCanceledException)
            {
                Stage = TrimStage.Cancelled;
                StatusMessage = "İptal edildi";
                Progress = 0;
                Log("İşlem kullanıcı tarafından iptal edildi.");
            }
            catch (MergeException ex)
            {
                Stage = TrimStage.Error;
                StatusMessage = "Hata oluştu: " + ex.UserMessage;
                Progress = 0;
                Log("HATA: " + ex.UserMessage);
                IsLogVisible = true;
                MessageBox.Show(Application.Current.MainWindow, ex.UserMessage, "Kırpma başarısız",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Stage = TrimStage.Error;
                StatusMessage = "Hata oluştu";
                Progress = 0;
                Log("BEKLENMEYEN HATA: " + ex);
                IsLogVisible = true;
                MessageBox.Show(Application.Current.MainWindow, ex.Message, "Kırpma başarısız",
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
