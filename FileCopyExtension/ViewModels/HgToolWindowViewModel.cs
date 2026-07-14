using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using FileCopyExtension.Models;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;

namespace FileCopyExtension.ViewModels
{
    public class HgToolWindowViewModel : ObservableObject
    {
        private const string CollectionName = "FileCopyExtension_Filters";
        private readonly DispatcherTimer _debounceTimer;
        private WritableSettingsStore _settingsStore;
        private string _currentBaseRev = ".";
        private string _currentBaseRevDescription = "";

        // --- Команды ---
        public RelayCommand RefreshCommand { get; }
        public RelayCommand OpenAllCommand { get; }
        public RelayCommand UndoCommand { get; }
        public RelayCommand OpenFileCommand { get; }
        public RelayCommand DiffFileCommand { get; }

        // --- Свойства UI ---
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private ObservableCollection<StatusRun> _statusRuns = new ObservableCollection<StatusRun>();
        public ObservableCollection<StatusRun> StatusRuns
        {
            get => _statusRuns;
            set => SetProperty(ref _statusRuns, value);
        }

        private string _statusTooltip = "Ready";
        public string StatusTooltip
        {
            get => _statusTooltip;
            set => SetProperty(ref _statusTooltip, value);
        }
        
        private HgDisplayMode _selectedMode = HgDisplayMode.Uncommitted;
        public HgDisplayMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (SetProperty(ref _selectedMode, value))
                {
                    RefreshCommand.Execute(null);
                    UndoCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private bool _showUntracked;
        public bool ShowUntracked
        {
            get => _showUntracked;
            set { if (SetProperty(ref _showUntracked, value)) RefreshCommand.Execute(null); }
        }

        private string _filterText = "";
        public string FilterText
        {
            get => _filterText;
            set { if (SetProperty(ref _filterText, value)) TriggerDebounce(); }
        }

        private string _excludeText = "";
        public string ExcludeText
        {
            get => _excludeText;
            set { if (SetProperty(ref _excludeText, value)) TriggerDebounce(); }
        }

        private string _compareBranchName = "default";
        public string CompareBranchName
        {
            get => _compareBranchName;
            set { if (SetProperty(ref _compareBranchName, value)) TriggerDebounce(); }
        }

        public ObservableCollection<HgFileItem> Files { get; } = new ObservableCollection<HgFileItem>();
        public ICollectionView FilesView { get; }

        public HgToolWindowViewModel()
        {
            // Инициализация команд
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            OpenAllCommand = new RelayCommand(async _ => await OpenAllAsync());
            UndoCommand = new RelayCommand(async p => await UndoAsync((System.Collections.IList)p), p => CanUndo((System.Collections.IList)p));
            OpenFileCommand = new RelayCommand(async p => await OpenFileAsync((HgFileItem)p));
            DiffFileCommand = new RelayCommand(async p => await DiffFileAsync((HgFileItem)p));

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _debounceTimer.Tick += DebounceTimer_Tick;

            FilesView = CollectionViewSource.GetDefaultView(Files);
            FilesView.Filter = FileFilter;

            LoadSettings();

            // Первый запуск
            RefreshCommand.Execute(null);
        }

        private void TriggerDebounce()
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            SaveSettings();
            FilesView.Refresh(); // Фильтруем на лету
        }

        private void LoadSettings()
        {
            try
            {
                var settingsManager = new ShellSettingsManager(ServiceProvider.GlobalProvider);
                _settingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);

                if (!_settingsStore.CollectionExists(CollectionName))
                    _settingsStore.CreateCollection(CollectionName);

                if (_settingsStore.PropertyExists(CollectionName, "Filter")) FilterText = _settingsStore.GetString(CollectionName, "Filter");
                if (_settingsStore.PropertyExists(CollectionName, "Exclude")) ExcludeText = _settingsStore.GetString(CollectionName, "Exclude");
                if (_settingsStore.PropertyExists(CollectionName, "CompareBranch")) CompareBranchName = _settingsStore.GetString(CollectionName, "CompareBranch");
            }
            catch { /* Ignore */ }
        }

        private void SaveSettings()
        {
            try
            {
                if (_settingsStore == null) return;
                if (!_settingsStore.CollectionExists(CollectionName)) _settingsStore.CreateCollection(CollectionName);

                _settingsStore.SetString(CollectionName, "Filter", FilterText ?? "");
                _settingsStore.SetString(CollectionName, "Exclude", ExcludeText ?? "");
                _settingsStore.SetString(CollectionName, "CompareBranch", CompareBranchName ?? "");
            }
            catch { /* Ignore */ }
        }

        private bool FileFilter(object item)
        {
            if (!(item is HgFileItem fileItem)) return false;

            if (!string.IsNullOrWhiteSpace(ExcludeText) && CheckMatch(fileItem.Path, ExcludeText)) return false;
            if (string.IsNullOrWhiteSpace(FilterText)) return true;
            return CheckMatch(fileItem.Path, FilterText);
        }

        private bool CheckMatch(string path, string patterns)
        {
            if (path.IndexOf(patterns, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var parts = patterns.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private async Task RefreshAsync()
        {
            IsBusy = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var customBranchName = CompareBranchName?.Trim() ?? "";
                if (SelectedMode == HgDisplayMode.CustomBranch && string.IsNullOrEmpty(customBranchName))
                {
                    Files.Clear();
                    SetSimpleStatus("Error: Branch name cannot be empty.");
                    return;
                }

                Files.Clear();
                SetSimpleStatus("Loading...");

                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                var solutionDir = GetSolutionDir(dte);
                if (solutionDir == null) return;

                var repoRoot = FindRepoRoot(solutionDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    SetSimpleStatus("No repo found");
                    return;
                }

                var isShowUntracked = ShowUntracked;

                var result = await Task.Run(() =>
                {
                    var fileList = new System.Collections.Generic.List<HgFileItem>();
                    string errorMsg = null;
                    string targetRev = ".";
                    switch (SelectedMode)
                    {
                        case HgDisplayMode.BaseBranchHead:
                            if (RunHgCommand(repoRoot, "log -r \"p1(first(branch(.)))\" --template \"{branch}\"", out var bName, out var hgError))
                            {
                                targetRev = bName.Trim();
                                if (string.IsNullOrEmpty(targetRev))
                                    return new { Files = fileList, CurrentInfo = "", BaseInfo = "", Error = "Could not determine base branch name.", TargetRev = "." };
                            }
                            else
                                return new { Files = fileList, CurrentInfo = "", BaseInfo = "", Error = "Root branch or error: " + hgError.Trim(), TargetRev = "." };
                            break;
                        case HgDisplayMode.CustomBranch:
                            targetRev = customBranchName;
                            break;
                        case HgDisplayMode.Branch:
                            targetRev = "p1(first(branch(.)))";
                            break;
                        // case HgDisplayMode.Uncommitted: - targetRev уже равен "." по умолчанию
                    }

                    var template = "{branch}|{node|short}|{desc|firstline}";
                    RunHgCommand(repoRoot, "log -r . --template \"" + template + "\"", out var currentInfoStr, out _);
                    if (string.IsNullOrEmpty(currentInfoStr)) currentInfoStr = "Unknown|?|?";

                    RunHgCommand(repoRoot, "log -r \"" + targetRev + "\" --template \"" + template + "\"", out var baseInfoStr, out _);
                    if (string.IsNullOrEmpty(baseInfoStr)) baseInfoStr = "Unknown|?|?";

                    var flags = "-ma";
                    if (isShowUntracked) flags += "u";
                    // Получаем реально измененные файлы 
                    var actuallyModified = GetActuallyModifiedFiles(repoRoot, targetRev);

                    if (RunHgCommand(repoRoot, "status --rev \"" + targetRev + "\" " + flags, out var output, out var statusError))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (line.Length > 2)
                            {
                                var status = line.Substring(0, 1);
                                var path = line.Substring(2).Trim();

                                var normalizedPath = path.Replace('/', '\\');
                                var isUnchanged = status == "M" && !actuallyModified.Contains(normalizedPath);

                                if (isUnchanged)
                                {
                                    status = "♦";
                                }

                                fileList.Add(new HgFileItem { Status = status, Path = path, IsUnchanged = isUnchanged });
                            }
                        }
                    }
                    else
                    {
                        if ((SelectedMode==HgDisplayMode.Branch || SelectedMode==HgDisplayMode.BaseBranchHead) && (statusError.Contains("revision 0") || statusError.Contains("unknown revision")))
                            errorMsg = "ROOT BRANCH DETECTED (No Parent). Use 'Uncommitted Only'.\nDetails: " + statusError.Trim();
                        else
                            errorMsg = "HG Error: " + statusError.Trim();
                    }

                    return new { Files = fileList, CurrentInfo = currentInfoStr, BaseInfo = baseInfoStr, Error = errorMsg, TargetRev = targetRev };
                });

                if (result.Error != null)
                {
                    SetSimpleStatus(result.Error);
                }
                else
                {
                    _currentBaseRev = result.TargetRev;
                    var bParts = result.BaseInfo.Split(new[] { '|' }, 3);
                    _currentBaseRevDescription = bParts.Length >= 2
                        ? bParts[1] + (bParts.Length >= 3 ? ": " + bParts[2] : "")
                        : result.TargetRev;
                    foreach (var item in result.Files) Files.Add(item);

                    StatusRuns.Clear();
                    if (SelectedMode != HgDisplayMode.Uncommitted)
                    {
                        StatusRuns.Add(new StatusRun { Text = "Branch: " });
                        AppendHgInfo(result.CurrentInfo);

                        string relationText = SelectedMode == HgDisplayMode.BaseBranchHead ? " vs Head of: "
                            : SelectedMode == HgDisplayMode.CustomBranch ? " vs Branch: "
                            : " vs Parent: ";
                        StatusRuns.Add(new StatusRun { Text = relationText });
                        AppendHgInfo(result.BaseInfo);
                    }
                    else
                    {
                        StatusRuns.Add(new StatusRun { Text = "Uncommitted in: " });
                        AppendHgInfo(result.CurrentInfo);
                    }

                    UpdateTooltipFromRuns();
                }
            }
            catch (Exception ex)
            {
                SetSimpleStatus("Error: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                UndoCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task OpenAllAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var filteredItems = FilesView;
            var count = 0;
            foreach (var _ in filteredItems) count++;

            if (count > 10)
            {
                var result = MessageBox.Show($"Вы собираетесь открыть {count} файлов. Продолжить?", "Подтверждение", MessageBoxButton.OKCancel, MessageBoxImage.Question);
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
                    try { dte.ItemOperations.OpenFile(fullPath); } catch { }
                }
            }
        }

        private async Task UndoAsync(System.Collections.IList items)
        {
            if (items == null || items.Count == 0) return;

            var selectedItems = new System.Collections.Generic.List<HgFileItem>();
            foreach (var item in items)
                if (item is HgFileItem hgItem) selectedItems.Add(hgItem);

            var msgBuilder = new StringBuilder();

            if (SelectedMode == HgDisplayMode.Uncommitted)
            {
                msgBuilder.AppendLine($"Вы уверены, что хотите откатить незакоммиченные изменения (revert) для {selectedItems.Count} файлов?\n");
            }
            else
            {
                string targetDesc;
                switch (SelectedMode)
                {
                    case HgDisplayMode.Branch:
                        targetDesc = $"точке ответвления ветки ({_currentBaseRevDescription})";
                        break;
                    case HgDisplayMode.BaseBranchHead:
                        targetDesc = $"голове родительской ветки ({_currentBaseRevDescription})";
                        break;
                    case HgDisplayMode.CustomBranch:
                        targetDesc = $"ветке {CompareBranchName} ({_currentBaseRevDescription})";
                        break;
                    default:
                        targetDesc = _currentBaseRevDescription;
                        break;
                }
                msgBuilder.AppendLine($"Вы уверены, что хотите вернуть {selectedItems.Count} файлов к {targetDesc}?\n");
                msgBuilder.AppendLine("Все изменения файлов с этого момента (включая незакоммиченные) будут потеряны.");
            }

            var maxDisplay = 15;
            for (var i = 0; i < selectedItems.Count; i++)
            {
                if (i < maxDisplay) msgBuilder.AppendLine($"{selectedItems[i].Path}");
                else { msgBuilder.AppendLine($"... и еще {selectedItems.Count - maxDisplay} файлов"); break; }
            }

            if (MessageBox.Show(msgBuilder.ToString(), "Подтверждение отката", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);
            if (string.IsNullOrEmpty(repoRoot)) return;

            try
            {
                IsBusy = true;
                var args = new StringBuilder("revert --no-backup");
                if (SelectedMode != HgDisplayMode.Uncommitted)
                    args.Append($" -r \"{_currentBaseRev}\"");
                foreach (var item in selectedItems) args.Append($" \"{item.Path}\"");

                await Task.Run(() =>
                {
                    RunHgCommand(repoRoot, args.ToString(), out _, out var error);
                    if (!string.IsNullOrEmpty(error)) throw new Exception(error);
                });

                await RefreshAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при откате: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanUndo(System.Collections.IList items) => items != null && items.Count > 0 && !IsBusy;

        private async Task OpenFileAsync(HgFileItem item)
        {
            if (item == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);
            var fullPath = Path.Combine(repoRoot, item.Path);
            if (File.Exists(fullPath))
            {
                try { dte.ItemOperations.OpenFile(fullPath); } catch { }
            }
        }

        private async Task DiffFileAsync(HgFileItem item)
        {
            if (item == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (string.IsNullOrEmpty(_currentBaseRev) || _currentBaseRev == "null")
            {
                MessageBox.Show("Cannot diff untracked file (no base version).");
                return;
            }

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);
            var currentFilePath = Path.Combine(repoRoot, item.Path);

            var tempFile = Path.GetTempFileName();
            var extension = Path.GetExtension(item.Path);
            var tempFileWithExt = tempFile + extension;
            File.Move(tempFile, tempFileWithExt);

            try
            {
                var success = RunHgCommand(repoRoot, $"cat -r \"{_currentBaseRev}\" \"{item.Path}\"", out _, out var error, tempFileWithExt);
                if (success)
                {
                    var diffService = (IVsDifferenceService)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
                    diffService?.OpenComparisonWindow2(tempFileWithExt, currentFilePath, $"Diff: {item.Path}", $"Hg Base ({_currentBaseRev}) vs Local", "Base Version", "Local Version", null, null, 0);
                }
                else
                    MessageBox.Show($"Could not retrieve base version.\nHG Error: {error}");
            }
            catch (Exception ex) { MessageBox.Show("Error opening diff: " + ex.Message); }
        }

        private void SetSimpleStatus(string msg)
        {
            StatusRuns.Clear();
            StatusRuns.Add(new StatusRun { Text = msg });
            StatusTooltip = msg;
        }

        private void AppendHgInfo(string rawHgInfo)
        {
            if (string.IsNullOrEmpty(rawHgInfo)) return;
            var parts = rawHgInfo.Split(new[] { '|' }, 3);
            if (parts.Length >= 3)
            {
                StatusRuns.Add(new StatusRun { Text = parts[0] + " ", Foreground = Brushes.DarkOrange, FontWeight = FontWeights.Bold });
                StatusRuns.Add(new StatusRun { Text = $"({parts[1]}) ", Foreground = Brushes.Silver });
                StatusRuns.Add(new StatusRun { Text = $"\"{parts[2]}\"", Foreground = Brushes.CornflowerBlue, FontStyle = FontStyles.Italic });
            }
            else
                StatusRuns.Add(new StatusRun { Text = rawHgInfo + " " });
        }

        private void UpdateTooltipFromRuns()
        {
            var sb = new StringBuilder();
            foreach (var run in StatusRuns) sb.Append(run.Text);
            StatusTooltip = sb.ToString();
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

                using (var process = Process.Start(psi))
                {
                    if (outputFile != null)
                    {
                        using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                            process.StandardOutput.BaseStream.CopyTo(fs);
                        using (var ms = new MemoryStream())
                        {
                            process.StandardError.BaseStream.CopyTo(ms);
                            error = HgOutputDecoder.Decode(ms.ToArray());
                        }
                    }
                    else
                    {
                        using (var ms = new MemoryStream())
                        {
                            process.StandardOutput.BaseStream.CopyTo(ms);
                            output = HgOutputDecoder.Decode(ms.ToArray());
                        }
                        using (var ms = new MemoryStream())
                        {
                            process.StandardError.BaseStream.CopyTo(ms);
                            error = HgOutputDecoder.Decode(ms.ToArray());
                        }
                    }
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private string GetSolutionDir(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName) ? Path.GetDirectoryName(dte.Solution.FullName) : null;
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

        private System.Collections.Generic.HashSet<string> GetActuallyModifiedFiles(string repoRoot, string targetRev)
        {
            var actuallyModified = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (RunHgCommand(repoRoot, $"diff --stat --rev \"{targetRev}\"", out var diffOutput, out _))
            {
                var lines = diffOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var pipeIndex = line.LastIndexOf('|');
                    if (pipeIndex > 0)
                    {
                        var path = line.Substring(0, pipeIndex).Trim();
                        // Унифицируем слеши для надежного сравнения
                        path = path.Replace('/', '\\');
                        actuallyModified.Add(path);
                    }
                }
            }
            return actuallyModified;
        }
    }

    // --- Ванильные MVVM хелперы ---
}