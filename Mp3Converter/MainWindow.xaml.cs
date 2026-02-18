using System.Windows;
using Mp3Converter.ViewModels;

namespace Mp3Converter
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}
