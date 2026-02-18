using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Mp3Converter.Core
{
    public enum ConversionStatus
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Cancelled,
        Skipped
    }

    public partial class ConversionItem : ObservableObject
    {
        [ObservableProperty]
        private string _fileName;

        [ObservableProperty]
        private string _inputPath;

        [ObservableProperty]
        private string? _outputPath;

        [ObservableProperty]
        private TimeSpan _duration;

        [ObservableProperty]
        private ConversionStatus _status = ConversionStatus.Pending;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string? _errorMessage;

        public ConversionItem(string inputPath)
        {
            InputPath = inputPath;
            FileName = System.IO.Path.GetFileName(inputPath);
            Status = ConversionStatus.Pending;
        }
    }
}
