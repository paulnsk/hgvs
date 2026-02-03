using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Process = System.Diagnostics.Process;

namespace FileCopyExtension
{
    public partial class HgToolWindowControl : UserControl
    {
        public ObservableCollection<HgFileItem> Files { get; set; } = new ObservableCollection<HgFileItem>();
        private string _currentBaseRev = ".";

        // Для сортировки
        private GridViewColumnHeader _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        public HgToolWindowControl()
        {
            InitializeComponent();
            lvFiles.ItemsSource = Files;

            // Инициализация фильтрации (по умолчанию пустая)
            var view = CollectionViewSource.GetDefaultView(Files);
            view.Filter = FileFilter;
        }

        // --- ЛОГИКА ФИЛЬТРАЦИИ ---
        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Получаем представление коллекции и просим его обновиться
            var view = CollectionViewSource.GetDefaultView(Files);
            view.Refresh();
        }

        private bool FileFilter(object item)
        {
            if (string.IsNullOrEmpty(txtFilter.Text)) return true;

            var fileItem = item as HgFileItem;
            if (fileItem == null) return false;

            // Игнорируем регистр, НЕ игнорируем пробелы
            return fileItem.Path.IndexOf(txtFilter.Text, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        // -------------------------

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            // Скрываем кнопку Undo, если мы не в режиме Uncommitted
            if (btnUndo != null && rbUncommitted != null)
            {
                btnUndo.Visibility = (rbUncommitted.IsChecked == true)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            RefreshList();
        }

        private void CheckBox_Checked(object sender, RoutedEventArgs e)
        {
            // Обновляем список при переключении галочки Untracked
            RefreshList();
        }

        // --- ЛОГИКА СОРТИРОВКИ ---
        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var headerClicked = e.OriginalSource as GridViewColumnHeader;
            if (headerClicked == null || headerClicked.Role == GridViewColumnHeaderRole.Padding) return;

            var propertyName = headerClicked.Tag as string;
            if (string.IsNullOrEmpty(propertyName)) return;

            ListSortDirection direction;

            if (headerClicked != _lastHeaderClicked)
            {
                direction = ListSortDirection.Ascending;
            }
            else
            {
                direction = (_lastDirection == ListSortDirection.Ascending)
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }

            Sort(propertyName, direction);
            _lastHeaderClicked = headerClicked;
            _lastDirection = direction;
        }

        private void Sort(string sortBy, ListSortDirection direction)
        {
            var dataView = CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);
            dataView.SortDescriptions.Clear();
            var sd = new SortDescription(sortBy, direction);
            dataView.SortDescriptions.Add(sd);
            dataView.Refresh();
        }
        // -------------------------

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private void BtnOpenAll_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Внимание: Files содержит все файлы, но lvFiles.Items содержит только отфильтрованные!
            // Если нужно открывать только те, что сейчас видны в списке (с учетом фильтра):
            var filteredItems = lvFiles.Items;

            if (filteredItems.Count > 10)
            {
                var result = MessageBox.Show(
                    $"Вы собираетесь открыть {filteredItems.Count} файлов. Продолжить?",
                    "Подтверждение",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.OK) return;
            }

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);

            foreach (HgFileItem item in filteredItems)
            {
                var fullPath = Path.Combine(repoRoot, item.Path);
                if (File.Exists(fullPath))
                {
                    dte.ItemOperations.OpenFile(fullPath);
                }
            }
        }


        private void LvFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Включаем кнопку только если что-то выбрано
            if (btnUndo != null)
            {
                btnUndo.IsEnabled = lvFiles.SelectedItems.Count > 0;
            }
        }

        private async void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var selectedItems = new System.Collections.Generic.List<HgFileItem>();
            foreach (var item in lvFiles.SelectedItems)
            {
                if (item is HgFileItem hgItem) selectedItems.Add(hgItem);
            }

            if (selectedItems.Count == 0) return;

            // 1. Формируем сообщение для подтверждения
            var msgBuilder = new StringBuilder();
            msgBuilder.AppendLine($"Вы уверены, что хотите откатить изменения (revert) для {selectedItems.Count} файлов?");
            msgBuilder.AppendLine();

            var maxDisplay = 15;
            for (var i = 0; i < selectedItems.Count; i++)
            {
                if (i < maxDisplay)
                {
                    msgBuilder.AppendLine($"{selectedItems[i].Path}");
                }
                else
                {
                    msgBuilder.AppendLine($"... и еще {selectedItems.Count - maxDisplay} файлов");
                    break;
                }
            }

            var result = MessageBox.Show(msgBuilder.ToString(), "Подтверждение отката", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            // 2. Выполняем откат
            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);
            if (string.IsNullOrEmpty(repoRoot)) return;

            try
            {
                btnUndo.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                // Формируем аргументы для hg revert
                // Используем --no-backup, чтобы не создавать .orig файлы (чистый откат)
                var args = new StringBuilder("revert --no-backup");

                foreach (var item in selectedItems)
                {
                    // Обязательно кавычки, если в путях есть пробелы
                    args.Append($" \"{item.Path}\"");
                }

                await System.Threading.Tasks.Task.Run(() =>
                {
                    // Внимание: командная строка имеет лимит длины. 
                    // Если файлов ОЧЕНЬ много, это может упасть, но для 20-50 файлов обычно ок.
                    // Если hg revert выдаст ошибку, мы её покажем.
                    RunHgCommand(repoRoot, args.ToString(), out var output, out var error);

                    if (!string.IsNullOrEmpty(error))
                    {
                        throw new Exception(error);
                    }
                });

                // 3. Автоматический рефреш после успеха
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при откате: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                btnUndo.IsEnabled = true; // Возвращаем кнопку, если рефреш не прошел
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void LvFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = ((FrameworkElement)e.OriginalSource).DataContext as HgFileItem;
            if (item == null) return;

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);

            var fullPath = Path.Combine(repoRoot, item.Path);
            if (!File.Exists(fullPath)) return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenDiff(repoRoot, item.Path, fullPath);
            }
            else
            {
                dte.ItemOperations.OpenFile(fullPath);
            }
        }

        private void OpenDiff(string repoRoot, string relativePath, string currentFilePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrEmpty(_currentBaseRev) || _currentBaseRev == "null")
            {
                MessageBox.Show("Cannot diff untracked file (no base version).");
                return;
            }

            var tempFile = Path.GetTempFileName();
            var extension = Path.GetExtension(relativePath);
            var tempFileWithExt = tempFile + extension;
            File.Move(tempFile, tempFileWithExt);

            try
            {
                // Добавили out string error
                var success = RunHgCommand(repoRoot, $"cat -r \"{_currentBaseRev}\" \"{relativePath}\"", out var output, out var error, tempFileWithExt);

                if (success)
                {
                    var diffService = (IVsDifferenceService)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
                    if (diffService != null)
                    {
                        diffService.OpenComparisonWindow2(
                            tempFileWithExt,
                            currentFilePath,
                            $"Diff: {relativePath}",
                            $"Hg Base ({_currentBaseRev}) vs Local",
                            "Base Version",
                            "Local Version",
                            null,
                            null,
                            0);
                    }
                }
                else
                {
                    // Показываем реальную ошибку, если hg cat упал
                    MessageBox.Show($"Could not retrieve base version.\nHG Error: {error}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening diff: " + ex.Message);
            }
        }
        
        public async void RefreshList()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (rbBranch == null || rbUncommitted == null) return;

            var isUncommittedMode = rbUncommitted.IsChecked == true;
            btnUndo.Visibility = isUncommittedMode ? Visibility.Visible : Visibility.Collapsed;

            btnRefresh.IsEnabled = false;
            btnOpenAll.IsEnabled = false;
            rbUncommitted.IsEnabled = false;
            rbBranch.IsEnabled = false;
            chkShowUntracked.IsEnabled = false;

            if (btnUndo.Visibility == Visibility.Visible) btnUndo.IsEnabled = false;

            Mouse.OverrideCursor = Cursors.Wait;

            Files.Clear();
            txtStatusInfo.Text = "Loading...";

            try
            {
                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                var solutionDir = GetSolutionDir(dte);
                if (solutionDir == null) return;

                var repoRoot = FindRepoRoot(solutionDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    txtStatusInfo.Text = "No repo found";
                    return;
                }

                var isBranchMode = rbBranch.IsChecked == true;
                var isShowUntracked = chkShowUntracked.IsChecked == true;

                _currentBaseRev = isBranchMode ? "p1(first(branch(.)))" : ".";

                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    var fileList = new System.Collections.Generic.List<HgFileItem>();
                    string errorMsg = null;
                    string hgError = null; // Переменная для сырой ошибки

                    var template = "{branch}|{node|short}|{desc|firstline}";
                    string currentInfoStr = null;
                    string baseInfoStr = null;

                    // Заглушка для ошибки, используем discard _
                    RunHgCommand(repoRoot, "log -r . --template \"" + template + "\"", out currentInfoStr, out _);
                    if (string.IsNullOrEmpty(currentInfoStr)) currentInfoStr = "Unknown|?|?";

                    // Тут может упасть, если parent нет (корневая ветка), но нам важнее ошибка на этапе status
                    RunHgCommand(repoRoot, "log -r \"" + _currentBaseRev + "\" --template \"" + template + "\"", out baseInfoStr, out _);
                    if (string.IsNullOrEmpty(baseInfoStr)) baseInfoStr = "Unknown|?|?";

                    var flags = "-ma";
                    if (isShowUntracked) flags += "u";

                    // Основной вызов STATUS
                    if (RunHgCommand(repoRoot, "status --rev \"" + _currentBaseRev + "\" " + flags, out var output, out hgError))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (line.Length > 2)
                            {
                                var status = line.Substring(0, 1);
                                var path = line.Substring(2).Trim();
                                fileList.Add(new HgFileItem { Status = status, Path = path });
                            }
                        }
                    }
                    else
                    {
                        // АНАЛИЗ ОШИБКИ
                        // Mercurial обычно пишет "abort: unknown revision" или "revision 0 has no parent"
                        if (isBranchMode && (hgError.Contains("revision 0") || hgError.Contains("unknown revision")))
                        {
                            errorMsg = "ROOT BRANCH DETECTED (No Parent). Use 'Uncommitted Only'.\nDetails: " + hgError.Trim();
                        }
                        else
                        {
                            errorMsg = "HG Error: " + hgError.Trim();
                        }
                    }

                    return new { Files = fileList, CurrentInfo = currentInfoStr, BaseInfo = baseInfoStr, Error = errorMsg };
                });

                if (result.Error != null)
                {
                    txtStatusInfo.Text = result.Error;
                    // Дублируем в тултип, чтобы можно было прочитать длинный лог ошибки
                    txtStatusInfo.ToolTip = result.Error;
                }
                else
                {
                    foreach (var item in result.Files)
                    {
                        Files.Add(item);
                    }

                    txtStatusInfo.Inlines.Clear();

                    if (isBranchMode)
                    {
                        txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run("Branch: "));
                        AppendHgInfo(result.CurrentInfo);
                        txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run(" vs Parent: "));
                        AppendHgInfo(result.BaseInfo);
                    }
                    else
                    {
                        txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run("Uncommitted in: "));
                        AppendHgInfo(result.CurrentInfo);
                    }

                    txtStatusInfo.ToolTip = txtStatusInfo.Text;
                }
            }
            catch (Exception ex)
            {
                txtStatusInfo.Text = "Error: " + ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
                btnRefresh.IsEnabled = true;
                btnOpenAll.IsEnabled = true;
                rbUncommitted.IsEnabled = true;
                rbBranch.IsEnabled = true;
                chkShowUntracked.IsEnabled = true;
                if (btnUndo.Visibility == Visibility.Visible)
                {
                    btnUndo.IsEnabled = lvFiles.SelectedItems.Count > 0;
                }
            }
        }

        private void AppendHgInfo(string rawHgInfo)
        {
            if (string.IsNullOrEmpty(rawHgInfo)) return;
            var parts = rawHgInfo.Split(new[] { '|' }, 3);
            if (parts.Length >= 3)
            {
                var branch = parts[0];
                var hash = parts[1];
                var msg = parts[2];

                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run(branch + " ") { Foreground = Brushes.DarkOrange, FontWeight = FontWeights.Bold });
                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run($"({hash}) ") { Foreground = Brushes.Silver });
                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run($"\"{msg}\"") { Foreground = Brushes.CornflowerBlue, FontStyle = FontStyles.Italic });
            }
            else
            {
                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run(rawHgInfo + " "));
            }
        }

        private bool RunHgCommand(string workDir, string args, out string output, out string error, string outputFile = null)
        {
            output = "";
            error = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "hg",
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, // Включаем перехват ошибок
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };

                using (var process = Process.Start(psi))
                {
                    if (outputFile != null)
                    {
                        // Если пишем в файл, то stderr читаем, а stdout копируем
                        // Читать stderr надо ДО WaitForExit во избежание дедлоков, 
                        // но для простых утилит типа hg можно и в конце, если буфер не переполнится.
                        // Для надежности используем асинхронное чтение или просто последовательное, т.к. hg не висит.

                        // Вариант с простым копированием (для hg cat обычно stderr пуст при успехе)
                        using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                        {
                            process.StandardOutput.BaseStream.CopyTo(fs);
                        }
                        error = process.StandardError.ReadToEnd();
                    }
                    else
                    {
                        output = process.StandardOutput.ReadToEnd();
                        error = process.StandardError.ReadToEnd();
                    }

                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        
        private string GetSolutionDir(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte == null || dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName)) return null;
            return Path.GetDirectoryName(dte.Solution.FullName);
        }

        private string FindRepoRoot(string startPath)
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".hg"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }

    public class HgFileItem
    {
        public string Status { get; set; }
        public string Path { get; set; }
    }

    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string;
            if (status == "A") return Brushes.MediumSeaGreen;
            if (status == "M") return Brushes.CornflowerBlue;
            if (status == "R") return Brushes.IndianRed;
            if (status == "?") return Brushes.MediumOrchid; // Новые (Untracked) - Фиолетовые
            return SystemColors.WindowTextBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}