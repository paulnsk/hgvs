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
        private string _currentBaseRev = "max(public())";

        // Для сортировки
        private GridViewColumnHeader _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        public HgToolWindowControl()
        {
            this.InitializeComponent();
            lvFiles.ItemsSource = Files;
        }

        // --- ЛОГИКА СОРТИРОВКИ ---
        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var headerClicked = e.OriginalSource as GridViewColumnHeader;
            if (headerClicked == null || headerClicked.Role == GridViewColumnHeaderRole.Padding) return;

            // Используем Tag заголовка как имя свойства для сортировки
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
            if (Files.Count > 10)
            {
                var result = MessageBox.Show(
                    $"Вы собираетесь открыть {Files.Count} файлов. Продолжить?",
                    "Подтверждение",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.OK) return;
            }

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            string solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            string repoRoot = FindRepoRoot(solutionDir);

            foreach (var item in Files)
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
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening diff: " + ex.Message);
            }
        }

        public void RefreshList()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Files.Clear();
            txtStatusInfo.Text = "Loading...";

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            string solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;

            string repoRoot = FindRepoRoot(solutionDir);
            if (string.IsNullOrEmpty(repoRoot))
            {
                MessageBox.Show("Repo root not found.");
                txtStatusInfo.Text = "No repo found";
                return;
            }

            try
            {
                RunHgCommand(repoRoot, "branch", out string currentBranch);
                currentBranch = currentBranch?.Trim() ?? "unknown";

                RunHgCommand(repoRoot, "log -r \"first(branch(.))\" --template \"{rev}\"", out string startRevStr);

                string modeDescription = "";

                if (startRevStr != null && startRevStr.Trim() == "0")
                {
                    _currentBaseRev = "max(public())";
                    modeDescription = $"Local changes in '{currentBranch}' vs Public";
                }
                else
                {
                    _currentBaseRev = "p1(first(branch(.)))";
                    modeDescription = $"Full Branch '{currentBranch}' vs Parent";
                }

                txtStatusInfo.Text = modeDescription;

                if (RunHgCommand(repoRoot, $"status --rev \"{_currentBaseRev}\" -ma", out string output))
                {
                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Length > 2)
                        {
                            string status = line.Substring(0, 1);
                            string path = line.Substring(2).Trim();
                            Files.Add(new HgFileItem { Status = status, Path = path });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                txtStatusInfo.Text = "Error: " + ex.Message;
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

    // --- КОНВЕРТЕР ЦВЕТОВ ---
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string status = value as string;
            if (status == "A") return Brushes.MediumSeaGreen; // Добавленные - Зеленые
            if (status == "M") return Brushes.CornflowerBlue; // Измененные - Голубые (видно и на темном)
            if (status == "R") return Brushes.IndianRed;      // Удаленные - Красные
            return SystemColors.WindowTextBrush;              // Остальные - цвет текста темы
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}