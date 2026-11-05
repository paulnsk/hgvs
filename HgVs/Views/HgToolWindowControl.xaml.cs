using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EnvDTE80;
using HgVs.Models;
using HgVs.ViewModels;
using Microsoft.VisualStudio.Shell;

namespace HgVs.Views
{
    public partial class HgToolWindowControl : UserControl
    {
        private string _lastSortPropertyName = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        public HgToolWindowControl()
        {
            InitializeComponent();
            var viewModel = new HgToolWindowViewModel();
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SizeChanged += (_, __) => UpdateColumnWidths();
            Loaded += (_, __) => UpdateColumnWidths();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HgToolWindowViewModel.SelectedListMode))
            {
                _lastSortPropertyName = null;
                _lastDirection = ListSortDirection.Ascending;
                ClearSortIndicators();
                var view = CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);
                view?.SortDescriptions.Clear();
                UpdateColumnWidths();
            }
            else if (e.PropertyName == nameof(HgToolWindowViewModel.SelectedItem) &&
                     sender is HgToolWindowViewModel vm &&
                     vm.SelectedItem != null)
            {
                if (vm.IsTreeMode)
                {
                    var row = vm.FileTree.SelectedRow?.File == vm.SelectedItem
                        ? vm.FileTree.SelectedRow
                        : vm.FileTree.SelectFile(vm.SelectedItem);
                    if (row != null) lvTree.ScrollIntoView(row);
                }
                else
                    lvFiles.ScrollIntoView(vm.SelectedItem);
            }
            else if (e.PropertyName == nameof(HgToolWindowViewModel.IsTreeMode) &&
                     sender is HgToolWindowViewModel layoutVm)
            {
                UpdateUndoSelection(layoutVm);
            }
        }

        private void UpdateColumnWidths()
        {
            if (GroupColumn == null || TodoColumn == null || FileColumn == null || lvFiles == null) return;
            var isTodoMode = DataContext is HgToolWindowViewModel vm && vm.IsTodoMode;
            GroupColumn.Width = isTodoMode ? 12 : 0;
            var availableWidth = Math.Max(160, lvFiles.ActualWidth - (isTodoMode ? 94 : 82));

            TodoColumn.Width = isTodoMode ? Math.Max(120, availableWidth * 0.68) : 0;
            FileColumn.Width = isTodoMode
                ? Math.Max(100, availableWidth - TodoColumn.Width)
                : availableWidth;
        }

        public void RefreshList()
        {
            // Используется извне (HgToolWindowCommand.cs), если окно только что открыли
            if (DataContext is HgToolWindowViewModel vm)
            {
                vm.RefreshCommand.Execute(null);
            }
        }

        public void FocusItem(HgFileItem item)
        {
            if (item == null) return;

            if (DataContext is HgToolWindowViewModel vm && vm.IsTreeMode)
            {
                var row = vm.FileTree.SelectFile(item);
                if (row == null) return;
                lvTree.ScrollIntoView(row);
                lvTree.UpdateLayout();
                if (lvTree.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem treeContainer)
                    treeContainer.Focus();
                else
                    lvTree.Focus();
                return;
            }

            lvFiles.SelectedItem = item;
            lvFiles.ScrollIntoView(item);
            lvFiles.UpdateLayout();

            if (lvFiles.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
                container.Focus();
            else
                lvFiles.Focus();
        }

        private void LvFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm)
            {
                if (!vm.IsTreeMode) UpdateUndoSelection(vm);
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
            ActivateItem(hgItem);
        }

        private void ActivateItem(HgFileItem hgItem)
        {
            if (DataContext is HgToolWindowViewModel vm)
            {
                if (vm.IsTodoMode)
                    vm.OpenFileCommand.Execute(hgItem);
                else if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    if (vm.DiffFileCommand.CanExecute(hgItem))
                        vm.DiffFileCommand.Execute(hgItem);
                }
                else
                    vm.OpenFileCommand.Execute(hgItem);
            }
        }

        private void LvFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.IsRepeat || !(DataContext is HgToolWindowViewModel vm) || vm.SelectedItem == null) return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ActivateItem(vm.SelectedItem);
            }
            else if (e.Key == Key.Space)
            {
                e.Handled = true;
                vm.ToggleReviewed(vm.SelectedItem);
            }
        }

        private void ReviewCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount != 1 ||
                !(sender is FrameworkElement element) ||
                !(element.DataContext is HgFileItem item) ||
                !(DataContext is HgToolWindowViewModel vm)) return;

            if (vm.IsTreeMode)
                vm.FileTree.SelectFile(item);
            else
                lvFiles.SelectedItem = item;
            vm.ToggleReviewed(item);
        }

        private void UpdateUndoSelection(HgToolWindowViewModel vm)
        {
            var files = new System.Collections.Generic.List<HgFileItem>();
            if (vm.IsTreeMode)
            {
                foreach (var selected in lvTree.SelectedItems)
                    if (selected is HgTreeRow row && row.File != null) files.Add(row.File);
            }
            else
            {
                foreach (var selected in lvFiles.SelectedItems)
                    if (selected is HgFileItem file) files.Add(file);
            }
            vm.SelectedUndoItems = files;
        }

        private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(DataContext is HgToolWindowViewModel vm) || !vm.IsTreeMode) return;
            UpdateUndoSelection(vm);
            if (lvTree.SelectedItem != null) lvTree.ScrollIntoView(lvTree.SelectedItem);
        }

        private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListBoxItem item) || !(item.DataContext is HgTreeRow row) ||
                !(DataContext is HgToolWindowViewModel vm)) return;
            e.Handled = true;
            vm.FileTree.SelectedRow = row;
            if (row.IsFolder)
            {
                vm.FileTree.Toggle(row);
                lvTree.Focus();
            }
            else ActivateItem(row.File);
        }

        private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListBoxItem item) || !(item.DataContext is HgTreeRow row) ||
                !(DataContext is HgToolWindowViewModel vm)) return;
            vm.FileTree.SelectedRow = row;
            item.Focus();
        }

        private void TreeArrow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount != 1 || !(sender is FrameworkElement element) ||
                !(element.DataContext is HgTreeRow row) || !(DataContext is HgToolWindowViewModel vm)) return;
            vm.FileTree.SelectedRow = row;
            vm.FileTree.Toggle(row);
            lvTree.Focus();
        }

        private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.IsRepeat || !(DataContext is HgToolWindowViewModel vm) ||
                !(lvTree.SelectedItem is HgTreeRow row)) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (row.IsFolder)
                {
                    vm.FileTree.Toggle(row);
                    lvTree.Focus();
                }
                else ActivateItem(row.File);
            }
            else if (e.Key == Key.Space && row.File != null)
            {
                e.Handled = true;
                vm.ToggleReviewed(row.File);
            }
            else if (e.Key == Key.Left)
            {
                e.Handled = true;
                if (row.IsFolder && row.IsExpanded) vm.FileTree.Toggle(row);
                else if (row.Parent != null) vm.FileTree.SelectedRow = row.Parent;
                lvTree.Focus();
            }
            else if (e.Key == Key.Right && row.IsFolder)
            {
                e.Handled = true;
                if (!row.IsExpanded) vm.FileTree.Toggle(row);
                else if (row.Children.Count > 0) vm.FileTree.SelectedRow = row.Children[0];
                lvTree.Focus();
            }
        }

        private void LvFiles_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListViewItem item) || !(item.DataContext is HgFileItem hgItem)) return;

            lvFiles.SelectedItem = hgItem;
            item.Focus();
        }

        private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!(sender is ContextMenu menu) ||
                !(DataContext is HgToolWindowViewModel vm)) return;

            var item = vm.SelectedItem;
            foreach (var entry in menu.Items)
            {
                if (!(entry is MenuItem menuItem)) continue;
                menuItem.CommandParameter = item;

                switch (menuItem.Tag as string)
                {
                    case "Open":
                        menuItem.IsEnabled = item != null;
                        break;
                    case "OpenDiff":
                        menuItem.IsEnabled = vm.CanDiffFile(item);
                        break;
                    case "ToggleViewed":
                        menuItem.IsEnabled = item != null;
                        menuItem.Header = item?.IsReviewed == true
                            ? "Mark as Not viewed"
                            : "Mark as Viewed";
                        menuItem.Icon = CreateReviewIcon(item?.IsReviewed == true);
                        break;
                    case "MarkAllViewed":
                    case "MarkAllNotViewed":
                        menuItem.IsEnabled = !vm.FilesView.IsEmpty;
                        break;
                }
            }
        }

        private static FrameworkElement CreateReviewIcon(bool filled)
        {
            var brush = new SolidColorBrush(Color.FromRgb(185, 166, 106));
            return new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(2),
                Fill = filled ? brush : Brushes.Transparent,
                Stroke = brush,
                StrokeThickness = 1.2
            };
        }

        private void ContextOpen_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm &&
                (sender as MenuItem)?.CommandParameter is HgFileItem item)
                vm.OpenFileCommand.Execute(item);
        }

        private void ContextOpenDiff_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm &&
                (sender as MenuItem)?.CommandParameter is HgFileItem item &&
                vm.DiffFileCommand.CanExecute(item))
                vm.DiffFileCommand.Execute(item);
        }

        private void ContextToggleViewed_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm &&
                (sender as MenuItem)?.CommandParameter is HgFileItem item)
                vm.ToggleReviewed(item);
        }

        private void ContextMarkAllViewed_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm)
                vm.SetVisibleFilesReviewed(true);
        }

        private void ContextMarkAllNotViewed_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is HgToolWindowViewModel vm)
                vm.SetVisibleFilesReviewed(false);
        }

        private void ContextMarkFilesFromListNotViewed_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(DataContext is HgToolWindowViewModel vm)) return;

            var dialog = new MarkFilesNotViewedDialog();
            try
            {
                var dteService = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
                if (dteService is DTE2 dte && dte.MainWindow != null)
                    new WindowInteropHelper(dialog).Owner = dte.MainWindow.HWnd;
            }
            catch
            {
                // ShowDialog is still modal to the WPF dispatcher if the VS owner cannot be resolved.
            }

            if (dialog.ShowDialog() == true)
                vm.MarkMatchingFilesNotViewed(dialog.PatternsText);
        }

        private void CollapseRuleApply_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(DataContext is HgToolWindowViewModel vm)) return;
            var rules = LoadCollapseRules();
            if (rules == null) return;

            var rule = rules.Find(item => string.Equals(item.Name, vm.CollapseRuleName, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
                vm.ApplyCollapseRule(rule);
            else
                OpenCollapseRuleMenu();
        }

        private void CollapseRuleMenu_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenCollapseRuleMenu();
        }

        private void OpenCollapseRuleMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(DataContext is HgToolWindowViewModel vm)) return;
            var rules = LoadCollapseRules();
            if (rules == null) return;

            var menu = new ContextMenu
            {
                PlacementTarget = CollapseRulePanel,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Background = (Brush)FindResource(VsBrushes.WindowKey),
                Foreground = (Brush)FindResource(VsBrushes.WindowTextKey)
            };
            var itemStyle = (Style)FindResource("FileContextMenuItemStyle");

            foreach (var rule in rules)
            {
                var item = new MenuItem
                {
                    Header = new TextBlock { Text = rule.Name },
                    ToolTip = rule.Expression,
                    Style = itemStyle,
                    Icon = string.Equals(rule.Name, vm.CollapseRuleName, StringComparison.OrdinalIgnoreCase)
                        ? new TextBlock { Text = "✓", Foreground = Brushes.CornflowerBlue, FontWeight = FontWeights.Bold }
                        : null
                };
                item.Click += (_, __) => vm.ApplyCollapseRule(rule);
                menu.Items.Add(item);
            }
            if (rules.Count == 0)
                menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "No rules yet" }, Style = itemStyle, IsEnabled = false });

            menu.Items.Add(new Separator { Style = (Style)FindResource("FileContextMenuSeparatorStyle") });

            var edit = new MenuItem { Header = new TextBlock { Text = "Edit rules…" }, Style = itemStyle, ToolTip = HgCollapseRules.FilePath };
            edit.Click += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                EditCollapseRules();
            };
            menu.Items.Add(edit);

            var addDefaults = new MenuItem { Header = new TextBlock { Text = "Add default rules" }, Style = itemStyle };
            addDefaults.Click += (_, __) => AddDefaultCollapseRules();
            menu.Items.Add(addDefaults);

            menu.IsOpen = true;
        }

        private static System.Collections.Generic.List<HgCollapseRule> LoadCollapseRules()
        {
            try { return HgCollapseRules.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read collapse rules from {HgCollapseRules.FilePath}:\n{ex.Message}", "HgVs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        private static void EditCollapseRules()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                HgCollapseRules.EnsureFileExists();
                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
                dte?.ItemOperations.OpenFile(HgCollapseRules.FilePath, EnvDTE.Constants.vsViewKindTextView)?.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open {HgCollapseRules.FilePath}:\n{ex.Message}", "HgVs", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AddDefaultCollapseRules()
        {
            try { HgCollapseRules.AddDefaults(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not write {HgCollapseRules.FilePath}:\n{ex.Message}", "HgVs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Сразу показываем меню с добавленными правилами, чтобы можно было выбрать одно из них.
            _ = ThreadHelper.JoinableTaskFactory.StartOnIdle(OpenCollapseRuleMenu);
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
            if (DataContext is HgToolWindowViewModel vm && vm.IsTodoMode)
            {
                if (propertyName != "Path")
                    dataView.SortDescriptions.Add(new SortDescription("Path", ListSortDirection.Ascending));
                dataView.SortDescriptions.Add(new SortDescription("LineNumber", ListSortDirection.Ascending));
            }
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
            if (status == "♦") return Brushes.Gray;
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
    }
}
