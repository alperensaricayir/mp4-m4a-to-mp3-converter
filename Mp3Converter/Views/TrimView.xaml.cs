using System.Windows.Controls;

namespace Mp3Converter.Views
{
    public partial class TrimView : UserControl
    {
        public TrimView()
        {
            InitializeComponent();
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.ScrollToEnd();
        }
    }
}
