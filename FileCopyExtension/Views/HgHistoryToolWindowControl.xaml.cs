using System.Windows.Controls;
using System.Windows.Input;
using FileCopyExtension.Models;
using FileCopyExtension.ViewModels;

namespace FileCopyExtension.Views
{
    public partial class HgHistoryToolWindowControl : UserControl
    {
        public HgHistoryToolWindowControl()
        {
            InitializeComponent();
            DataContext = new HgHistoryToolWindowViewModel();
        }

        private void LvHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is HgHistoryToolWindowViewModel vm)
            {
                vm.UpdateSelection(lvHistory.SelectedItems);
            }
        }

        private void LvHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListViewItem item)) return;
            if (!(item.DataContext is HgHistoryItem hgItem)) return;

            e.Handled = true;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // WPF ListView при двойном клике с зажатым Ctrl успевает снять выделение 
                // со второго клика (т.к. воспринимает это как Toggle). 
                // Принудительно возвращаем элемент в выделенные, если он оттуда выпал.
                if (!lvHistory.SelectedItems.Contains(hgItem))
                {
                    lvHistory.SelectedItems.Add(hgItem);
                }

                if (DataContext is HgHistoryToolWindowViewModel vm)
                {
                    // Обновляем состояние во вью-модели перед вызовом команды
                    vm.UpdateSelection(lvHistory.SelectedItems);
                    vm.DiffFileCommand.Execute(null);
                }
            }
            else
            {
                // Обычный двойной клик без Ctrl открывает файл.
                lvHistory.SelectedItems.Clear();
                lvHistory.SelectedItem = hgItem;

                if (DataContext is HgHistoryToolWindowViewModel vm)
                {
                    vm.OpenFileCommand.Execute(hgItem);
                }
            }
        }
    }
}