using System.Windows;

namespace HgVs.Views
{
    public partial class MarkFilesNotViewedDialog : Window
    {
        public MarkFilesNotViewedDialog()
        {
            InitializeComponent();
            Loaded += (_, __) => PatternsTextBox.Focus();
        }

        public string PatternsText => PatternsTextBox.Text;

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}