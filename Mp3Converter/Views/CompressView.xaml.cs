using System.Windows.Controls;

namespace Mp3Converter.Views
{
    public partial class CompressView : UserControl
    {
        public CompressView()
        {
            InitializeComponent();
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.ScrollToEnd();
        }
    }
}
