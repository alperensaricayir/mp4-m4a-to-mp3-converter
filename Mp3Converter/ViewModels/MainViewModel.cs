using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Mp3Converter.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace Mp3Converter.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly FfmpegService _ffmpegService;
        private readonly object _lock = new object();
        private CancellationTokenSource? _cts;
        private bool _requestPause = false;

        [ObservableProperty]
        private ObservableCollection<ConversionItem> _items = new();

        [ObservableProperty]
        private string _outputFolder;

        [ObservableProperty]
        private Mp3QualitySettings _qualitySettings = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConverting))]
        private bool _isConverting;

        public bool IsNotConverting => !IsConverting;

        [ObservableProperty]
        private bool _skipExisting = true;

        [ObservableProperty]
        private string _logText = "";

        [ObservableProperty]
        private bool _isDownloadingFfmpeg;

        [ObservableProperty]
        private double _downloadProgress;

        public MainViewModel()
        {
            _ffmpegService = new FfmpegService();
            string docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            OutputFolder = Path.Combine(docPath, "Mp3ConverterOutput");

            BindingOperations.EnableCollectionSynchronization(Items, _lock);

            // Check for FFmpeg asynchronously on startup
            CheckAndDownloadFfmpeg();
        }

        private async void CheckAndDownloadFfmpeg()
        {
            if (!_ffmpegService.CheckFfmpegExists())
            {
                var result = MessageBox.Show("FFmpeg tools are missing. Do you want to download them automatically? (Required for conversion)", "Missing Components", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    IsDownloadingFfmpeg = true;
                    Log("Downloading FFmpeg tools... This may take a few minutes.");
                    
                    try
                    {
                        var progress = new Progress<double>(p => 
                        {
                            if (p > 100) DownloadProgress = 100; // Extraction phase
                            else DownloadProgress = p;
                        });

                        await _ffmpegService.DownloadFfmpegAsync(progress, CancellationToken.None);
                        Log("FFmpeg downloaded and installed successfully.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error downloading FFmpeg: {ex.Message}");
                        MessageBox.Show($"Failed to download FFmpeg: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    finally
                    {
                        IsDownloadingFfmpeg = false;
                    }
                }
                else
                {
                    Log("ERROR: ffmpeg.exe not found. Please place it in the 'tools' folder manually.");
                }
            }
            else
            {
                Log("Ready. FFmpeg found.");
            }
        }

        private void Log(string message)
        {
            var msg = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Current?.Dispatcher.InvokeAsync(() => LogText += msg + Environment.NewLine);
        }

        [RelayCommand]
        private void AddFiles()
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Audio Files|*.mp4;*.m4a;*.mp3|MP4 Files|*.mp4|M4A Files|*.m4a|MP3 Files|*.mp3|All Files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                {
                    AddItem(file);
                }
            }
        }

        [RelayCommand]
        private void AddFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Folder to Scan"
            };

            if (dialog.ShowDialog() == true)
            {
                ScanFolder(dialog.FolderName);
            }
        }

        private void ScanFolder(string path)
        {
            try
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || 
                                f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

                int count = 0;
                foreach (var file in files)
                {
                    AddItem(file);
                    count++;
                }
                Log($"Added {count} files from {path}");
            }
            catch (Exception ex)
            {
                Log($"Error scanning folder: {ex.Message}");
            }
        }

        private void AddItem(string path)
        {
            lock (_lock)
            {
                if (Items.Any(i => i.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    return;

                var item = new ConversionItem(path);
                Items.Add(item);
                
                // Probe duration
                Task.Run(async () =>
                {
                    try
                    {
                        var duration = await _ffmpegService.ProbeDurationAsync(path);
                        Application.Current?.Dispatcher.Invoke(() => item.Duration = duration);
                    }
                    catch { }
                });
            }
        }

        [RelayCommand]
        private void SelectOutputFolder()
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                OutputFolder = dialog.FolderName;
            }
        }

        [RelayCommand]
        private void OpenOutputFolder()
        {
            if (Directory.Exists(OutputFolder))
            {
                Process.Start("explorer.exe", OutputFolder);
            }
            else
            {
                Log("Output folder does not exist yet.");
            }
        }

        [RelayCommand]
        private async Task Start()
        {
            if (IsConverting) return;

            // Reset request pause
            _requestPause = false;

            // If all complete, ask to reset
            if (Items.Count > 0 && Items.All(i => i.Status == ConversionStatus.Completed || i.Status == ConversionStatus.Skipped))
            {
                 if (MessageBox.Show("All items completed. Reset statuses to convert again?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    foreach (var i in Items) 
                    {
                        i.Status = ConversionStatus.Pending;
                        i.Progress = 0;
                        i.ErrorMessage = null;
                    }
                }
                else
                {
                    return;
                }
            }

            IsConverting = true;
            _cts = new CancellationTokenSource();
            Log("Starting conversion...");

            try
            {
                // ToList to avoid modification during enumeration if we were modifying the collection (we aren't)
                var itemsToProcess = Items.Where(i => i.Status == ConversionStatus.Pending || i.Status == ConversionStatus.Failed).ToList();
                
                foreach (var item in itemsToProcess)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    
                    if (_requestPause)
                    {
                        Log("Paused queue.");
                        break;
                    }

                    // Check Skip Logic
                    if (SkipExisting)
                    {
                        // Ensure directory exists for check
                        Directory.CreateDirectory(OutputFolder);
                        string baseName = Path.GetFileNameWithoutExtension(item.InputPath);
                        string potentialOutput = Path.Combine(OutputFolder, baseName + ".mp3");
                        
                        if (File.Exists(potentialOutput) && new FileInfo(potentialOutput).Length > 0)
                        {
                            item.Status = ConversionStatus.Skipped;
                            item.OutputPath = potentialOutput;
                            item.Progress = 100;
                            Log($"Skipped (Exists): {item.FileName}");
                            continue;
                        }
                    }

                    item.Status = ConversionStatus.InProgress;
                    item.Progress = 0;
                    item.ErrorMessage = null;
                    Log($"Converting: {item.FileName}...");

                    try
                    {
                        await _ffmpegService.ConvertAsync(item, OutputFolder, QualitySettings, _cts.Token);
                        Log($"Completed: {item.FileName}");
                    }
                    catch (TaskCanceledException)
                    {
                        Log($"Cancelled: {item.FileName}");
                        item.Status = ConversionStatus.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed: {item.FileName} - {ex.Message}");
                        item.Status = ConversionStatus.Failed;
                        item.ErrorMessage = ex.Message;
                    }
                }
            }
            finally
            {
                IsConverting = false;
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                
                if (_requestPause)
                {
                    Log("Conversion paused. Press Start to resume.");
                }
                else
                {
                    Log("Conversion finished or stopped.");
                }
            }
        }

        [RelayCommand]
        private void Pause()
        {
            if (IsConverting)
            {
                _requestPause = true;
                Log("Pause requested. Will stop after current file.");
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            if (IsConverting && _cts != null)
            {
                _cts.Cancel();
                Log("Cancelling current operation...");
            }
        }

        [RelayCommand]
        private void RemoveItem(ConversionItem item)
        {
            if (item != null && Items.Contains(item))
            {
                Items.Remove(item);
            }
        }

        [RelayCommand]
        private void ClearCompleted()
        {
            var toRemove = Items.Where(i => i.Status == ConversionStatus.Completed || i.Status == ConversionStatus.Skipped).ToList();
            foreach (var item in toRemove)
            {
                Items.Remove(item);
            }
        }
    }
}
