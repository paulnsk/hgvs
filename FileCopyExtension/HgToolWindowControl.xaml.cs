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
            this.InitializeComponent();
            lvFiles.ItemsSource = Files;

            // Инициализация фильтрации (по умолчанию пустая)
            ICollectionView view = CollectionViewSource.GetDefaultView(Files);
            view.Filter = FileFilter;
        }

        // --- ЛОГИКА ФИЛЬТРАЦИИ ---
        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Получаем представление коллекции и просим его обновиться
            ICollectionView view = CollectionViewSource.GetDefaultView(Files);
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

            string propertyName = headerClicked.Tag as string;
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
            ICollectionView dataView = CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);
            dataView.SortDescriptions.Clear();
            SortDescription sd = new SortDescription(sortBy, direction);
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
            string solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            string repoRoot = FindRepoRoot(solutionDir);

            foreach (HgFileItem item in filteredItems)
            {
                string fullPath = Path.Combine(repoRoot, item.Path);
                if (File.Exists(fullPath))
                {
                    dte.ItemOperations.OpenFile(fullPath);
                }
            }
        }

        private void LvFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = ((FrameworkElement)e.OriginalSource).DataContext as HgFileItem;
            if (item == null) return;

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            string solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            string repoRoot = FindRepoRoot(solutionDir);

            string fullPath = Path.Combine(repoRoot, item.Path);
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

            // Если файл Untracked (?), у него нет базовой версии в HG. Дифф невозможен (или пуст).
            // Просто откроем файл.
            if (string.IsNullOrEmpty(_currentBaseRev) || _currentBaseRev == "null")
            {
                MessageBox.Show("Cannot diff untracked file (no base version).");
                return;
            }

            string tempFile = Path.GetTempFileName();
            string extension = Path.GetExtension(relativePath);
            string tempFileWithExt = tempFile + extension;
            File.Move(tempFile, tempFileWithExt);

            try
            {
                bool success = RunHgCommand(repoRoot, $"cat -r \"{_currentBaseRev}\" \"{relativePath}\"", out string output, tempFileWithExt);

                if (success)
                {
                    IVsDifferenceService diffService = (IVsDifferenceService)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
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
                    // Для новых файлов (Added 'A'), которых еще нет в базе, hg cat может упасть.
                    // В этом случае можно создать пустой файл для сравнения, но пока просто покажем ошибку.
                    MessageBox.Show("Could not retrieve base version (file might be new in this branch).");
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

            btnRefresh.IsEnabled = false;
            btnOpenAll.IsEnabled = false;
            rbUncommitted.IsEnabled = false;
            rbBranch.IsEnabled = false;
            chkShowUntracked.IsEnabled = false;

            Mouse.OverrideCursor = Cursors.Wait;

            Files.Clear();
            txtStatusInfo.Text = "Loading...";

            try
            {
                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                string solutionDir = GetSolutionDir(dte);
                if (solutionDir == null) return;

                string repoRoot = FindRepoRoot(solutionDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    txtStatusInfo.Text = "No repo found";
                    return;
                }

                bool isBranchMode = rbBranch.IsChecked == true;
                bool isShowUntracked = chkShowUntracked.IsChecked == true;

                _currentBaseRev = isBranchMode ? "p1(first(branch(.)))" : ".";

                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    var fileList = new System.Collections.Generic.List<HgFileItem>();
                    string errorMsg = null;

                    string template = "{branch}|{node|short}|{desc|firstline}";
                    string currentInfoStr = null;
                    string baseInfoStr = null;

                    if (!RunHgCommand(repoRoot, "log -r . --template \"" + template + "\"", out currentInfoStr))
                        currentInfoStr = "Unknown|?|?";

                    if (!RunHgCommand(repoRoot, "log -r \"" + _currentBaseRev + "\" --template \"" + template + "\"", out baseInfoStr))
                        baseInfoStr = "Unknown|?|?";

                    // Формируем аргументы.
                    // -m (modified), -a (added)
                    // Если нужен untracked, добавляем -u
                    string flags = "-ma";
                    if (isShowUntracked) flags += "u";

                    // Команда: hg status --rev REV -mau
                    if (RunHgCommand(repoRoot, "status --rev \"" + _currentBaseRev + "\" " + flags, out string output))
                    {
                        string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (line.Length > 2)
                            {
                                string status = line.Substring(0, 1);
                                string path = line.Substring(2).Trim();
                                fileList.Add(new HgFileItem { Status = status, Path = path });
                            }
                        }
                    }
                    else
                    {
                        errorMsg = "Error running hg status";
                    }

                    return new { Files = fileList, CurrentInfo = currentInfoStr, BaseInfo = baseInfoStr, Error = errorMsg };
                });

                if (result.Error != null)
                {
                    txtStatusInfo.Text = result.Error;
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
            }
        }

        private void AppendHgInfo(string rawHgInfo)
        {
            if (string.IsNullOrEmpty(rawHgInfo)) return;
            var parts = rawHgInfo.Split(new[] { '|' }, 3);
            if (parts.Length >= 3)
            {
                string branch = parts[0];
                string hash = parts[1];
                string msg = parts[2];

                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run(branch + " ") { Foreground = Brushes.DarkOrange, FontWeight = FontWeights.Bold });
                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run($"({hash}) ") { Foreground = Brushes.Silver });
                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run($"\"{msg}\"") { Foreground = Brushes.CornflowerBlue, FontStyle = FontStyles.Italic });
            }
            else
            {
                txtStatusInfo.Inlines.Add(new System.Windows.Documents.Run(rawHgInfo + " "));
            }
        }

        private bool RunHgCommand(string workDir, string args, out string output, string outputFile = null)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "hg",
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Default
                };

                using (var process = Process.Start(psi))
                {
                    if (outputFile != null)
                    {
                        using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                        {
                            process.StandardOutput.BaseStream.CopyTo(fs);
                        }
                    }
                    else
                    {
                        output = process.StandardOutput.ReadToEnd();
                    }
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
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
            DirectoryInfo dir = new DirectoryInfo(startPath);
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
            string status = value as string;
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