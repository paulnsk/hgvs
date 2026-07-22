using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using HgVs.Models;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;

namespace HgVs.ViewModels
{
    public class HgToolWindowViewModel : ObservableObject
    {
        private const string CollectionName = "HgVs_Filters";
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _todoRefreshTimer;
        private readonly DispatcherTimer _solutionRetryTimer;
        private readonly List<HgFileItem> _sourceFiles = new List<HgFileItem>();
        private readonly Dictionary<string, TodoFileState> _todoFileStates =
            new Dictionary<string, TodoFileState>(StringComparer.OrdinalIgnoreCase);
        private WritableSettingsStore _settingsStore;
        private string _currentBaseRev = ".";
        private string _currentBaseRevDescription = "";
        private string _repoRoot;
        private bool _isTodoRefreshRunning;
        private bool _isRefreshing;
        private int _solutionRetryAttempts;

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

        private HgListMode _selectedListMode = HgListMode.Files;
        public HgListMode SelectedListMode
        {
            get => _selectedListMode;
            set
            {
                if (!SetProperty(ref _selectedListMode, value)) return;

                OnPropertyChanged(nameof(IsTodoMode));
                UndoCommand.NotifyCanExecuteChanged();

                if (value == HgListMode.Todo)
                {
                    _todoRefreshTimer.Start();
                    if (!IsBusy)
                        QueueTodoRefresh(true);
                }
                else
                {
                    _todoRefreshTimer.Stop();
                    ShowFileItems();
                }
            }
        }

        public bool IsTodoMode => SelectedListMode == HgListMode.Todo;

        private HgFileItem _selectedItem;
        public HgFileItem SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
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

            _todoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _todoRefreshTimer.Tick += TodoRefreshTimer_Tick;

            _solutionRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _solutionRetryTimer.Tick += SolutionRetryTimer_Tick;

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

        private void TodoRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (IsBusy || !IsTodoMode) return;
            QueueTodoRefresh(false);
        }

        private void SolutionRetryTimer_Tick(object sender, EventArgs e)
        {
            if (IsBusy) return;
            if (++_solutionRetryAttempts > 60)
            {
                _solutionRetryTimer.Stop();
                SetSimpleStatus("No solution loaded");
                return;
            }

            RefreshCommand.Execute(null);
        }

        private void QueueTodoRefresh(bool force)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await RefreshTodoItemsAsync(force);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("TODO refresh failed: " + ex);
                }
            }).FileAndForget("HgVs/TodoRefresh");
        }

        private void ShowFileItems()
        {
            SynchronizeDisplayedItems(_sourceFiles);
        }

        private async Task RefreshTodoItemsAsync(bool force)
        {
            if (!IsTodoMode || string.IsNullOrEmpty(_repoRoot) || _isTodoRefreshRunning) return;

            _isTodoRefreshRunning = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                var openDocuments = GetOpenTextDocuments(dte);
                var snapshots = new List<TodoFileSnapshot>();

                foreach (var source in _sourceFiles)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(_repoRoot, source.Path));
                    var lastWriteTimeUtc = File.Exists(fullPath)
                        ? File.GetLastWriteTimeUtc(fullPath)
                        : DateTime.MinValue;

                    if (openDocuments.TryGetValue(fullPath, out var textDocument))
                    {
                        snapshots.Add(new TodoFileSnapshot
                        {
                            Source = source,
                            FullPath = fullPath,
                            Text = ReadTextDocument(textDocument),
                            IsOpen = true,
                            LastWriteTimeUtc = lastWriteTimeUtc
                        });
                        continue;
                    }

                    if (force ||
                        !_todoFileStates.TryGetValue(source.Path, out var state) ||
                        state.WasOpen ||
                        state.LastWriteTimeUtc != lastWriteTimeUtc)
                    {
                        snapshots.Add(new TodoFileSnapshot
                        {
                            Source = source,
                            FullPath = fullPath,
                            IsOpen = false,
                            LastWriteTimeUtc = lastWriteTimeUtc
                        });
                    }
                }

                var parsedSnapshots = await Task.Run(() =>
                {
                    foreach (var snapshot in snapshots)
                    {
                        var text = snapshot.Text;
                        if (text == null && File.Exists(snapshot.FullPath))
                        {
                            try { text = File.ReadAllText(snapshot.FullPath); }
                            catch { text = string.Empty; }
                        }

                        snapshot.Items = ParseTodoItems(snapshot.Source, text ?? string.Empty);
                    }
                    return snapshots;
                });

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                foreach (var snapshot in parsedSnapshots)
                {
                    _todoFileStates[snapshot.Source.Path] = new TodoFileState
                    {
                        Items = snapshot.Items,
                        LastWriteTimeUtc = snapshot.LastWriteTimeUtc,
                        WasOpen = snapshot.IsOpen
                    };
                }

                if (!IsTodoMode) return;

                var currentPaths = new HashSet<string>(
                    _sourceFiles.Select(item => item.Path),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var stalePath in _todoFileStates.Keys.Where(path => !currentPaths.Contains(path)).ToList())
                    _todoFileStates.Remove(stalePath);

                var todoItems = new List<HgFileItem>();
                var groupIndex = 0;
                foreach (var source in _sourceFiles)
                {
                    if (!_todoFileStates.TryGetValue(source.Path, out var state) || state.Items.Count == 0)
                        continue;

                    foreach (var item in state.Items)
                    {
                        item.FileGroupIndex = groupIndex;
                        todoItems.Add(item);
                    }
                    groupIndex++;
                }

                SynchronizeDisplayedItems(todoItems);
            }
            finally
            {
                _isTodoRefreshRunning = false;
            }
        }

        private static Dictionary<string, TextDocument> GetOpenTextDocuments(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new Dictionary<string, TextDocument>(StringComparer.OrdinalIgnoreCase);
            if (dte == null) return result;

            foreach (Document document in dte.Documents)
            {
                try
                {
                    if (string.IsNullOrEmpty(document.FullName)) continue;
                    var textDocument = document.Object("TextDocument") as TextDocument;
                    if (textDocument != null)
                        result[Path.GetFullPath(document.FullName)] = textDocument;
                }
                catch
                {
                    // Some Visual Studio document types do not expose a text buffer.
                }
            }
            return result;
        }

        private static string ReadTextDocument(TextDocument textDocument)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var editPoint = textDocument.StartPoint.CreateEditPoint();
                return editPoint.GetText(textDocument.EndPoint);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<HgFileItem> ParseTodoItems(HgFileItem source, string text)
        {
            var result = new List<HgFileItem>();
            if (text.IndexOf('\0') >= 0) return result;

            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var inBlockComment = false;

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!ContainsTodoInComment(line, ref inBlockComment)) continue;

                var identityText = line.Trim();
                occurrences.TryGetValue(identityText, out var occurrence);
                occurrences[identityText] = ++occurrence;

                result.Add(new HgFileItem
                {
                    Status = source.Status,
                    Path = source.Path,
                    IsUnchanged = source.IsUnchanged,
                    TodoText = FormatTodoText(line),
                    LineNumber = index + 1,
                    Identity = "todo|" + source.Path + "|" + identityText + "|" + occurrence
                });
            }

            return result;
        }

        private static bool ContainsTodoInComment(string line, ref bool inBlockComment)
        {
            var position = 0;
            while (position < line.Length)
            {
                if (inBlockComment)
                {
                    var blockEnd = line.IndexOf("*/", position, StringComparison.Ordinal);
                    var commentEnd = blockEnd < 0 ? line.Length : blockEnd;
                    if (line.IndexOf("todo", position, commentEnd - position, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (blockEnd >= 0) inBlockComment = false;
                        return true;
                    }

                    if (blockEnd < 0) return false;
                    inBlockComment = false;
                    position = blockEnd + 2;
                    continue;
                }

                var lineComment = line.IndexOf("//", position, StringComparison.Ordinal);
                var blockComment = line.IndexOf("/*", position, StringComparison.Ordinal);

                if (lineComment >= 0 && (blockComment < 0 || lineComment < blockComment))
                    return line.IndexOf("todo", lineComment + 2, StringComparison.OrdinalIgnoreCase) >= 0;

                if (blockComment < 0) return false;
                inBlockComment = true;
                position = blockComment + 2;
            }

            return false;
        }

        private static string FormatTodoText(string line)
        {
            var original = line.Trim();
            var todoIndex = original.IndexOf("todo", StringComparison.OrdinalIgnoreCase);
            if (todoIndex >= 0)
            {
                var lineComment = original.LastIndexOf("//", todoIndex, StringComparison.Ordinal);
                var blockComment = original.LastIndexOf("/*", todoIndex, StringComparison.Ordinal);
                var commentStart = Math.Max(lineComment, blockComment);
                if (commentStart > 0)
                    original = original.Substring(commentStart).Trim();
            }

            var prefixes = new[] { "//", "/*", "*", "/" };
            foreach (var prefix in prefixes)
            {
                if (!original.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var remainder = original.Substring(prefix.Length).TrimStart();
                if (!remainder.StartsWith("todo", StringComparison.OrdinalIgnoreCase)) return original;

                remainder = remainder.Substring(4).TrimStart(' ', '\t', ':', '-');
                if (remainder.EndsWith("*/", StringComparison.Ordinal))
                    remainder = remainder.Substring(0, remainder.Length - 2).TrimEnd();
                return string.IsNullOrEmpty(remainder) ? original : remainder;
            }
            return original;
        }

        private void SynchronizeDisplayedItems(IList<HgFileItem> desiredItems)
        {
            var selectedIdentity = SelectedItem?.Identity;
            var selectedPath = SelectedItem?.Path;
            var selectionCameFromOtherMode = SelectedItem != null &&
                                             SelectedItem.IsTodoItem != IsTodoMode;
            var oldViewItems = FilesView.Cast<HgFileItem>().ToList();
            var oldViewIndex = SelectedItem == null ? -1 : oldViewItems.IndexOf(SelectedItem);

            for (var index = 0; index < desiredItems.Count; index++)
            {
                var desired = desiredItems[index];
                if (index < Files.Count && Files[index].Identity == desired.Identity)
                {
                    Files[index].UpdateFrom(desired);
                    continue;
                }

                var existingIndex = -1;
                for (var candidate = index + 1; candidate < Files.Count; candidate++)
                {
                    if (Files[candidate].Identity != desired.Identity) continue;
                    existingIndex = candidate;
                    break;
                }

                if (existingIndex >= 0)
                {
                    Files.Move(existingIndex, index);
                    Files[index].UpdateFrom(desired);
                }
                else
                {
                    Files.Insert(index, desired);
                }
            }

            while (Files.Count > desiredItems.Count)
                Files.RemoveAt(Files.Count - 1);

            var newViewItems = FilesView.Cast<HgFileItem>().ToList();
            var selection = !string.IsNullOrEmpty(selectedIdentity)
                ? newViewItems.FirstOrDefault(item => item.Identity == selectedIdentity)
                : null;

            if (selection == null && selectionCameFromOtherMode && !string.IsNullOrEmpty(selectedPath))
                selection = newViewItems.FirstOrDefault(item =>
                    string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));

            if (selection == null && oldViewIndex >= 0 && newViewItems.Count > 0)
                selection = newViewItems[Math.Min(oldViewIndex, newViewItems.Count - 1)];

            SelectedItem = selection;
        }

        private sealed class TodoFileState
        {
            public List<HgFileItem> Items { get; set; } = new List<HgFileItem>();
            public DateTime LastWriteTimeUtc { get; set; }
            public bool WasOpen { get; set; }
        }

        private sealed class TodoFileSnapshot
        {
            public HgFileItem Source { get; set; }
            public string FullPath { get; set; }
            public string Text { get; set; }
            public bool IsOpen { get; set; }
            public DateTime LastWriteTimeUtc { get; set; }
            public List<HgFileItem> Items { get; set; }
        }

        private async Task RefreshAsync()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            IsBusy = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var customBranchName = CompareBranchName?.Trim() ?? "";
                if (SelectedMode == HgDisplayMode.CustomBranch && string.IsNullOrEmpty(customBranchName))
                {
                    _sourceFiles.Clear();
                    Files.Clear();
                    SetSimpleStatus("Error: Branch name cannot be empty.");
                    return;
                }

                Files.Clear();
                SetSimpleStatus("Loading...");

                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                var solutionDir = GetSolutionDir(dte);
                if (solutionDir == null)
                {
                    SetSimpleStatus("Waiting for solution...");
                    _solutionRetryTimer.Start();
                    return;
                }

                _solutionRetryTimer.Stop();
                _solutionRetryAttempts = 0;

                var repoRoot = FindRepoRoot(solutionDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    SetSimpleStatus("No repo found");
                    return;
                }
                _repoRoot = repoRoot;

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

                                fileList.Add(new HgFileItem
                                {
                                    Status = status,
                                    Path = path,
                                    IsUnchanged = isUnchanged,
                                    Identity = "file|" + path
                                });
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
                    _sourceFiles.Clear();
                    _sourceFiles.AddRange(result.Files);
                    if (IsTodoMode)
                        await RefreshTodoItemsAsync(true);
                    else
                        ShowFileItems();

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
                _isRefreshing = false;
                IsBusy = false;
                UndoCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task OpenAllAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var filteredItems = FilesView.Cast<HgFileItem>()
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var count = filteredItems.Count;

            if (count > 10)
            {
                var result = MessageBox.Show($"Вы собираетесь открыть {count} файлов. Продолжить?", "Подтверждение", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK) return;
            }

            var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
            var solutionDir = GetSolutionDir(dte);
            if (solutionDir == null) return;
            var repoRoot = FindRepoRoot(solutionDir);

            foreach (var item in filteredItems)
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

        private bool CanUndo(System.Collections.IList items) =>
            !IsTodoMode && items != null && items.Count > 0 && !IsBusy;

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
                try
                {
                    var window = dte.ItemOperations.OpenFile(fullPath, EnvDTE.Constants.vsViewKindTextView);
                    window?.Activate();
                    if (item.LineNumber > 0 && window?.Document?.Selection is TextSelection selection)
                        selection.GotoLine(item.LineNumber, false);
                }
                catch { }
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
