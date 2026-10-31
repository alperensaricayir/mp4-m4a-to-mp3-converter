using System.Windows.Controls;

namespace Mp3Converter.Views
{
    public partial class MergeView : UserControl
    {
        public MergeView()
        {
            InitializeComponent();
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.ScrollToEnd();
        }
    }
}
