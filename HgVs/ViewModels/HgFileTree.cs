using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using HgVs.Models;

namespace HgVs.ViewModels
{
    // A flattened tree keeps list virtualization and ordinary file commands.
    public sealed class HgFileTree : ObservableObject
    {
        private readonly ICollectionView _files;
        private readonly Func<string> _getRepository;
        private readonly Action<HgFileItem> _selectFile;
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HgTreeRow> _roots = new List<HgTreeRow>();
        private readonly TreeRowCollection _rows = new TreeRowCollection();
        private string _repository;
        private bool _active;
        private readonly DispatcherTimer _rebuildTimer;
        private HgTreeRow _selectedRow;

        public ObservableCollection<HgTreeRow> Rows => _rows;

        public HgTreeRow SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (SetProperty(ref _selectedRow, value) && value != null)
                    _selectFile(value.File);
            }
        }

        public HgFileTree(ICollectionView files, Func<string> getRepository, Action<HgFileItem> selectFile)
        {
            _files = files;
            _getRepository = getRepository;
            _selectFile = selectFile;
            _rebuildTimer = new DispatcherTimer(DispatcherPriority.DataBind) { Interval = TimeSpan.Zero };
            _rebuildTimer.Tick += RebuildTimer_Tick;
            _files.CollectionChanged += FilesChanged;
        }

        public void SetActive(bool active)
        {
            _active = active;
            _rebuildTimer.Stop();
            if (active) Rebuild();
        }

        private void FilesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_active || _rebuildTimer.IsEnabled) return;
            // Collection synchronization can produce many changes in one UI turn.
            _rebuildTimer.Start();
        }

        private void RebuildTimer_Tick(object sender, EventArgs e)
        {
            _rebuildTimer.Stop();
            Rebuild();
        }

        private void Rebuild()
        {
            var selectedPath = SelectedRow?.File?.Path;
            var selectedFolder = SelectedRow?.IsFolder == true ? SelectedRow.Key : null;
            var repository = _getRepository();
            if (!string.Equals(repository, _repository, StringComparison.OrdinalIgnoreCase))
            {
                _collapsed.Clear();
                _repository = repository;
            }

            var root = new Folder("", "");
            foreach (var file in _files.Cast<HgFileItem>())
            {
                var path = file.Path.Replace('/', '\\');
                if (Path.IsPathRooted(path) && !string.IsNullOrEmpty(repository))
                {
                    var prefix = repository.TrimEnd('\\', '/') + "\\";
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        path = path.Substring(prefix.Length);
                }
                var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                var folder = root;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (!folder.Folders.TryGetValue(parts[i], out var child))
                    {
                        child = new Folder(parts[i], folder.Key.Length == 0 ? parts[i] : folder.Key + "\\" + parts[i]);
                        folder.Folders.Add(parts[i], child);
                    }
                    folder = child;
                }
                folder.Files.Add(file);
            }

            _roots.Clear();
            AddChildren(root, _roots, null, 0);
            ReplaceVisibleRows();
            SelectedRow = AllRows(_roots).FirstOrDefault(row =>
                (selectedPath != null && string.Equals(row.File?.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ||
                (selectedFolder != null && row.IsFolder && string.Equals(row.Key, selectedFolder, StringComparison.OrdinalIgnoreCase)));
            if (SelectedRow != null && !_rows.Contains(SelectedRow)) SelectedRow = null;
            if (SelectedRow == null && (selectedPath != null || selectedFolder != null))
                _selectFile(null);
        }

        private void AddChildren(Folder folder, List<HgTreeRow> rows, HgTreeRow parent, int depth)
        {
            foreach (var child in folder.Folders.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var compact = child;
                var name = child.Name;
                while (compact.Files.Count == 0 && compact.Folders.Count == 1)
                {
                    compact = compact.Folders.Values.First();
                    name += "\\" + compact.Name;
                }
                var row = new HgTreeRow(name, compact.Key, null, parent, depth)
                {
                    IsExpanded = !_collapsed.Contains(compact.Key)
                };
                AddChildren(compact, row.Children, row, depth + 1);
                rows.Add(row);
            }
            foreach (var file in folder.Files.OrderBy(item => Path.GetFileName(item.Path.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase))
                rows.Add(new HgTreeRow(Path.GetFileName(file.Path.Replace('/', '\\')), file.Path, file, parent, depth));
        }

        public void Toggle(HgTreeRow row)
        {
            if (row == null || !row.IsFolder) return;
            row.IsExpanded = !row.IsExpanded;
            if (row.IsExpanded) _collapsed.Remove(row.Key);
            else _collapsed.Add(row.Key);
            var selected = SelectedRow;
            if (!row.IsExpanded && IsDescendant(selected, row)) selected = row;
            ReplaceVisibleRows();
            SelectedRow = selected;
        }

        /// <summary>
        /// Раскрывает папки, в которых есть нужные файлы, и сворачивает остальные.
        /// Ручные раскрытия и сворачивания при этом сбрасываются.
        /// </summary>
        public void ApplyCollapseRule(Func<HgFileItem, bool> isNeeded)
        {
            if (_rebuildTimer.IsEnabled)
            {
                _rebuildTimer.Stop();
                Rebuild();
            }
            foreach (var root in _roots) ApplyCollapseRule(root, isNeeded);

            var selected = SelectedRow;
            for (var parent = selected?.Parent; parent != null; parent = parent.Parent)
                if (!parent.IsExpanded) selected = parent;
            ReplaceVisibleRows();
            SelectedRow = selected;
        }

        private bool ApplyCollapseRule(HgTreeRow row, Func<HgFileItem, bool> isNeeded)
        {
            if (!row.IsFolder) return isNeeded(row.File);

            var hasNeeded = false;
            foreach (var child in row.Children)
                hasNeeded |= ApplyCollapseRule(child, isNeeded);
            row.IsExpanded = hasNeeded;
            if (hasNeeded) _collapsed.Remove(row.Key);
            else _collapsed.Add(row.Key);
            return hasNeeded;
        }

        public HgTreeRow SelectFile(HgFileItem file)
        {
            if (file == null) return null;
            // Locate can arrive before the queued post-refresh rebuild runs.
            if (_rebuildTimer.IsEnabled)
            {
                _rebuildTimer.Stop();
                Rebuild();
            }
            var row = AllRows(_roots).FirstOrDefault(item => item.File == file);
            if (row == null) return null;
            var changed = false;
            for (var parent = row.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.IsExpanded) continue;
                parent.IsExpanded = true;
                _collapsed.Remove(parent.Key);
                changed = true;
            }
            if (changed) ReplaceVisibleRows();
            SelectedRow = row;
            return row;
        }

        private void ReplaceVisibleRows()
        {
            _rows.Replace(AllRows(_roots, visibleOnly: true));
        }

        private static bool IsDescendant(HgTreeRow row, HgTreeRow ancestor)
        {
            for (var parent = row?.Parent; parent != null; parent = parent.Parent)
                if (parent == ancestor) return true;
            return false;
        }

        private static IEnumerable<HgTreeRow> AllRows(IEnumerable<HgTreeRow> rows, bool visibleOnly = false)
        {
            foreach (var row in rows)
            {
                yield return row;
                if (!visibleOnly || row.IsExpanded)
                    foreach (var child in AllRows(row.Children, visibleOnly)) yield return child;
            }
        }

        private sealed class Folder
        {
            public readonly string Name;
            public readonly string Key;
            public readonly Dictionary<string, Folder> Folders = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
            public readonly List<HgFileItem> Files = new List<HgFileItem>();
            public Folder(string name, string key) { Name = name; Key = key; }
        }

        private sealed class TreeRowCollection : ObservableCollection<HgTreeRow>
        {
            public void Replace(IEnumerable<HgTreeRow> rows)
            {
                Items.Clear();
                foreach (var row in rows) Items.Add(row);
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }
    }

    public sealed class HgTreeRow : ObservableObject
    {
        private bool _isExpanded;
        public string Name { get; }
        public string Key { get; }
        public HgFileItem File { get; }
        public HgTreeRow Parent { get; }
        public List<HgTreeRow> Children { get; } = new List<HgTreeRow>();
        public bool IsFolder => File == null;
        public Thickness Indent { get; }
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public HgTreeRow(string name, string key, HgFileItem file, HgTreeRow parent, int depth)
        {
            Name = name;
            Key = key;
            File = file;
            Parent = parent;
            Indent = new Thickness(depth * 16, 0, 0, 0);
        }
    }
}
