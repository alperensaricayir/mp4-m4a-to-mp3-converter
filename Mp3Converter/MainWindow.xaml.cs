using System.Windows;
using Mp3Converter.ViewModels;

namespace Mp3Converter
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _initialized;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            // Bring window to front before any MessageBox so the UI is actually visible.
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();

            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await _viewModel.InitializeAsync();
        }
    }
}
