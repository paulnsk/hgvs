using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using EnvDTE;
using EnvDTE80;
using FileCopyExtension.Models;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace FileCopyExtension.ViewModels
{
    public class HgHistoryToolWindowViewModel : ObservableObject
    {
        public RelayCommand RefreshCommand { get; }
        public RelayCommand OpenFileCommand { get; }
        public RelayCommand DiffFileCommand { get; }

        private string _targetFilePath;
        public string TargetFilePath
        {
            get => _targetFilePath;
            set { if (SetProperty(ref _targetFilePath, value)) OnPropertyChanged(nameof(WindowTitle)); }
        }

        public string WindowTitle => string.IsNullOrEmpty(TargetFilePath) ? "No file selected" : $"History: {Path.GetFileName(TargetFilePath)}";

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private HgHistoryDiffMode _selectedMode = HgHistoryDiffMode.Previous;
        public HgHistoryDiffMode SelectedMode
        {
            get => _selectedMode;
            set { if (SetProperty(ref _selectedMode, value)) DiffFileCommand.NotifyCanExecuteChanged(); }
        }

        public ObservableCollection<HgHistoryItem> HistoryItems { get; } = new ObservableCollection<HgHistoryItem>();

        private IList _currentSelectedItems;

        public HgHistoryToolWindowViewModel()
        {
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            OpenFileCommand = new RelayCommand(async p => await OpenFileAsync((HgHistoryItem)p));
            DiffFileCommand = new RelayCommand(async _ => await DiffFileAsync());
        }

        private bool _isSelectedModeEnabled;
        public bool IsSelectedModeEnabled
        {
            get => _isSelectedModeEnabled;
            set => SetProperty(ref _isSelectedModeEnabled, value);
        }

        public void UpdateSelection(IList items)
        {
            _currentSelectedItems = items;
            var count = _currentSelectedItems?.Count ?? 0;

            // Включаем радиобаттон только если выбрано ровно 2 файла
            IsSelectedModeEnabled = (count == 2);

            if (count == 2)
            {
                SelectedMode = HgHistoryDiffMode.Selected;
            }
            else if (SelectedMode == HgHistoryDiffMode.Selected && count != 2)
            {
                SelectedMode = HgHistoryDiffMode.Previous;
            }

            DiffFileCommand.NotifyCanExecuteChanged();
        }
        private async Task RefreshAsync()
        {
            if (string.IsNullOrEmpty(TargetFilePath)) return;

            IsBusy = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                HistoryItems.Clear();
                StatusText = "Loading history...";

                var repoRoot = FindRepoRoot(Path.GetDirectoryName(TargetFilePath));
                if (string.IsNullOrEmpty(repoRoot))
                {
                    StatusText = "No repository found.";
                    return;
                }

                var relativePath = GetRelativePath(TargetFilePath, repoRoot);

                var result = await Task.Run(() =>
                {
                    var list = new System.Collections.Generic.List<HgHistoryItem>();
                    var template = "{rev}|{node|short}|{author|person}|{date|isodate}|{desc|firstline}";
                    if (RunHgCommand(repoRoot, $"log -f \"{relativePath}\" --template \"{template}\n\"", out var output, out var error))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            var parts = line.Split(new[] { '|' }, 5);
                            if (parts.Length == 5)
                            {
                                list.Add(new HgHistoryItem
                                {
                                    Revision = parts[0],
                                    Node = parts[1],
                                    Author = parts[2],
                                    Date = parts[3],
                                    Message = parts[4]
                                });
                            }
                        }
                    }
                    return new { Items = list, Error = string.IsNullOrWhiteSpace(error) ? null : error };
                });

                if (result.Error != null && result.Items.Count == 0)
                {
                    StatusText = $"HG Error: {result.Error}";
                }
                else
                {
                    foreach (var item in result.Items) HistoryItems.Add(item);
                    StatusText = $"Loaded {HistoryItems.Count} commits.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task OpenFileAsync(HgHistoryItem item)
        {
            if (item == null || string.IsNullOrEmpty(TargetFilePath)) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var repoRoot = FindRepoRoot(Path.GetDirectoryName(TargetFilePath));
            if (string.IsNullOrEmpty(repoRoot)) return;

            var relativePath = GetRelativePath(TargetFilePath, repoRoot);
            var tempFile = ExtractFile(item, repoRoot, relativePath, "open");

            if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
            {
                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                try { dte.ItemOperations.OpenFile(tempFile); } catch { }
            }
        }

        private async Task DiffFileAsync()
        {
            var count = _currentSelectedItems?.Count ?? 0;
            if (count > 2)
            {
                MessageBox.Show("Пожалуйста, выберите 1 или 2 файла для сравнения.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (count == 0) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var repoRoot = FindRepoRoot(Path.GetDirectoryName(TargetFilePath));
            var relativePath = GetRelativePath(TargetFilePath, repoRoot);

            var item1 = (HgHistoryItem)_currentSelectedItems[0];
            var item2 = count == 2 ? (HgHistoryItem)_currentSelectedItems[1] : null;

            string leftPath = null, rightPath = null;
            string leftLabel = "", rightLabel = "";

            if (count == 2)
            {
                var idx1 = HistoryItems.IndexOf(item1);
                var idx2 = HistoryItems.IndexOf(item2);

                // Тот что выше в списке (меньше индекс) - более новый (правая сторона)
                var newer = idx1 < idx2 ? item1 : item2;
                var older = idx1 < idx2 ? item2 : item1;

                leftPath = ExtractFile(older, repoRoot, relativePath, "diff_older");
                rightPath = ExtractFile(newer, repoRoot, relativePath, "diff_newer");
                leftLabel = $"Rev {older.Revision} ({older.Author})";
                rightLabel = $"Rev {newer.Revision} ({newer.Author})";
            }
            else if (count == 1)
            {
                if (SelectedMode == HgHistoryDiffMode.Current)
                {
                    leftPath = ExtractFile(item1, repoRoot, relativePath, "diff_base");
                    rightPath = TargetFilePath; // Текущий физический файл
                    leftLabel = $"Rev {item1.Revision}";
                    rightLabel = "Current File (Working Dir)";
                }
                else if (SelectedMode == HgHistoryDiffMode.Previous)
                {
                    var idx = HistoryItems.IndexOf(item1);
                    if (idx < HistoryItems.Count - 1)
                    {
                        var older = HistoryItems[idx + 1];
                        leftPath = ExtractFile(older, repoRoot, relativePath, "diff_prev");
                        rightPath = ExtractFile(item1, repoRoot, relativePath, "diff_rev");
                        leftLabel = $"Rev {older.Revision}";
                        rightLabel = $"Rev {item1.Revision}";
                    }
                    else
                    {
                        MessageBox.Show("Отсутствует предыдущий коммит для сравнения.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }
                else if (SelectedMode == HgHistoryDiffMode.Selected)
                {
                    MessageBox.Show("Для этого режима выберите ровно 2 коммита.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(leftPath) && !string.IsNullOrEmpty(rightPath) && File.Exists(leftPath) && File.Exists(rightPath))
            {
                var diffService = (IVsDifferenceService)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
                var title = $"Diff: {Path.GetFileName(TargetFilePath)}";
                var tooltip = $"{leftLabel} vs {rightLabel}";
                diffService?.OpenComparisonWindow2(leftPath, rightPath, title, tooltip, leftLabel, rightLabel, null, null, 0);
            }
        }

        private string ExtractFile(HgHistoryItem item, string repoRoot, string relativePath, string contextPrefix)
        {
            var fileName = Path.GetFileName(TargetFilePath);
            var ext = Path.GetExtension(TargetFilePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(TargetFilePath);

            // Санитаризация имени автора
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeAuthor = new string(item.Author.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            var safeDate = new string(item.Date.Select(c => invalidChars.Contains(c) ? '-' : c).ToArray());

            var tempFileName = $"{nameWithoutExt}_{contextPrefix}_rev{item.Revision}_{safeAuthor}_{safeDate}{ext}";
            var tempPath = Path.Combine(Path.GetTempPath(), tempFileName);

            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            var success = RunHgCommand(repoRoot, $"cat -r \"{item.Revision}\" \"{relativePath}\"", out _, out _, tempPath);
            return success ? tempPath : null;
        }

        private bool RunHgCommand(string workDir, string args, out string output, out string error, string outputFile = null)
        {
            output = ""; error = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "hg",
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (outputFile != null)
                    {
                        using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                            process.StandardOutput.BaseStream.CopyTo(fs);
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
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private string FindRepoRoot(string startPath)
        {
            if (string.IsNullOrEmpty(startPath)) return null;
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".hg"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private string GetRelativePath(string fullPath, string rootPath)
        {
            if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return Path.GetFileName(fullPath);
        }
    }
}