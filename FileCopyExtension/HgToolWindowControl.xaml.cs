using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace FileCopyExtension
{
    public partial class HgToolWindowControl : UserControl
    {
        private string _lastSortPropertyName = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        public HgToolWindowControl()
        {
            InitializeComponent();
            DataContext = new HgToolWindowViewModel();
        }

        public void RefreshList()
        {
            // Используется извне (HgToolWindowCommand.cs), если окно только что открыли
            if (DataContext is HgToolWindowViewModel vm)
            {
                vm.RefreshCommand.Execute(null);
            }
        }

        private void LvFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm)
            {
                // Принудительно заставляем команду Undo перепроверить CanExecute (т.к. поменялось количество выделенных файлов)
                vm.UndoCommand.NotifyCanExecuteChanged();
            }
        }

        private void LvFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListViewItem item)) return;
            if (!(item.DataContext is HgFileItem hgItem)) return;

            // ГЛАВНЫЙ ФИКС:
            // Прекращаем дальнейшую обработку этого события.
            // Это не дает стандартной логике ListView обработать Ctrl+Click
            // и снять выделение с элемента.
            e.Handled = true;

            // Теперь наша установка SelectedItem будет единственной и сработает корректно.
            lvFiles.SelectedItem = hgItem;

            if (DataContext is HgToolWindowViewModel vm)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    vm.DiffFileCommand.Execute(hgItem);
                else
                    vm.OpenFileCommand.Execute(hgItem);
            }
        }

        // Логика сортировки GridView (остается в View, т.к. это чистый UI-механизм)
        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader headerClicked) || headerClicked.Role == GridViewColumnHeaderRole.Padding) return;
            if (!(headerClicked.Tag is string propertyName) || string.IsNullOrEmpty(propertyName)) return;

            ClearSortIndicators();

            ListSortDirection direction;
            if (propertyName != _lastSortPropertyName)
            {
                direction = ListSortDirection.Ascending;
                _lastSortPropertyName = propertyName;
                _lastDirection = direction;
            }
            else
            {
                if (_lastDirection == ListSortDirection.Ascending)
                {
                    direction = ListSortDirection.Descending;
                    _lastDirection = direction;
                }
                else
                {
                    _lastSortPropertyName = null;
                    _lastDirection = ListSortDirection.Ascending;
                    var view = CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);
                    view.SortDescriptions.Clear();
                    view.Refresh();
                    return;
                }
            }

            var dataView = CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);
            dataView.SortDescriptions.Clear();
            dataView.SortDescriptions.Add(new SortDescription(propertyName, direction));
            dataView.Refresh();

            if (headerClicked.Content is string text)
                headerClicked.Content = text + (direction == ListSortDirection.Ascending ? " ▲" : " ▼");
        }

        private void ClearSortIndicators()
        {
            if (!(lvFiles.View is GridView gridView)) return;
            foreach (var col in gridView.Columns)
            {
                if (col.Header is GridViewColumnHeader headerControl && headerControl.Content is string contentText)
                    headerControl.Content = contentText.Replace(" ▲", "").Replace(" ▼", "");
                else if (col.Header is string headerText)
                    col.Header = headerText.Replace(" ▲", "").Replace(" ▼", "");
            }
        }
    }

    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string;
            if (status == "A") return Brushes.MediumSeaGreen;
            if (status == "M") return Brushes.CornflowerBlue;
            if (status == "R") return Brushes.IndianRed;
            if (status == "?") return Brushes.MediumOrchid;
            return null; // В XAML настроен Fallback на дефолтный цвет
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
    }
}