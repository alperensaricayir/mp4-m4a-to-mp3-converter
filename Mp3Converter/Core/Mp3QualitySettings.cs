using CommunityToolkit.Mvvm.ComponentModel;

namespace Mp3Converter.Core
{
    public partial class Mp3QualitySettings : ObservableObject
    {
        [ObservableProperty]
        private bool _isCbr = true;

        [ObservableProperty]
        private int _bitrate = 192; // 128, 192, 256, 320

        [ObservableProperty]
        private int _vbrQuality = 2; // 0-5 (0 best)
    }
}
