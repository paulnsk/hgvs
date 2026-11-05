using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace HgVs
{
    /// <summary>Глобальный переключатель полосы «изменён относительно коммита». Хранится вместе с настройками окна HgVs.</summary>
    internal static class HgDiffBarSettings
    {
        private const string CollectionName = "HgVs_Filters";
        private const string PropertyName = "ShowDiffBar";
        private static bool? _isEnabled;

        public static event EventHandler Changed;

        public static bool IsEnabled
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_isEnabled == null)
                {
                    try
                    {
                        var store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                        _isEnabled = store.GetBoolean(CollectionName, PropertyName, true);
                    }
                    catch { _isEnabled = true; }
                }
                return _isEnabled.Value;
            }
            set
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_isEnabled == value) return;
                _isEnabled = value;
                try
                {
                    var store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
                    if (!store.CollectionExists(CollectionName)) store.CreateCollection(CollectionName);
                    store.SetBoolean(CollectionName, PropertyName, value);
                }
                catch { }
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(HgDiffBarMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Top)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class HgDiffBarMarginProvider : IWpfTextViewMarginProvider
    {
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var view = wpfTextViewHost.TextView;
            // Правая часть наших диффов тоже документ: там полоса не нужна.
            if (view.Roles.Contains(DifferenceViewerRoles.DiffTextViewRole)) return null;
            if (!TextDocumentFactoryService.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document)) return null;

            var repoRoot = HgDiffBarMargin.FindRepoRoot(Path.GetDirectoryName(document.FilePath));
            return repoRoot == null ? null : new HgDiffBarMargin(view, document, repoRoot);
        }
    }

    /// <summary>
    /// Полоса над редактором: показывается, пока текст файла отличается от версии в последнем коммите (родитель рабочей копии).
    /// Кнопка открывает дифф рядом, не закрывая вкладку.
    /// </summary>
    internal sealed class HgDiffBarMargin : Border, IWpfTextViewMargin
    {
        public const string MarginName = "HgVsDiffBar";
        private static readonly Color Accent = Color.FromRgb(0x94, 0x5D, 0xB7);

        private readonly IWpfTextView _view;
        private readonly ITextDocument _document;
        private readonly string _repoRoot;
        private readonly DispatcherTimer _changeTimer;

        // Версия файла в коммите-родителе. null — версии нет (файл не отслеживается или новый).
        private string _baseParent;
        private byte[] _baseBytes;
        private string _baseText;
        private bool _isLoading;
        private bool _isDismissed;
        private bool _isDisposed;

        public HgDiffBarMargin(IWpfTextView view, ITextDocument document, string repoRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view = view;
            _document = document;
            _repoRoot = repoRoot;
            Visibility = Visibility.Collapsed;
            Child = CreateContent();

            _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _changeTimer.Tick += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _changeTimer.Stop();
                Evaluate();
            };

            _view.TextBuffer.Changed += TextBuffer_Changed;
            _view.GotAggregateFocus += View_GotAggregateFocus;
            _document.FileActionOccurred += Document_FileActionOccurred;
            HgDiffBarSettings.Changed += Settings_Changed;

            Evaluate();
        }

        private FrameworkElement CreateContent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Background = new SolidColorBrush(Color.FromArgb(0x38, Accent.R, Accent.G, Accent.B));
            BorderBrush = new SolidColorBrush(Accent);
            BorderThickness = new Thickness(3, 0, 0, 0);
            Padding = new Thickness(8, 2, 4, 2);

            var text = new TextBlock { Text = "Changed since last commit", VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.WindowTextKey);

            var viewDiff = CreateButton("View Diff", "Open the diff with the last commit next to this tab");
            viewDiff.Margin = new Thickness(10, 0, 0, 0);
            viewDiff.MouseLeftButtonUp += (_, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                e.Handled = true;
                ViewDiff();
            };

            var close = CreateButton("✕", "Hide for this tab");
            close.BorderThickness = new Thickness(0);
            close.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _isDismissed = true;
                Visibility = Visibility.Collapsed;
            };

            var panel = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(close);
            panel.Children.Add(text);
            panel.Children.Add(viewDiff);
            return panel;
        }

        private static Border CreateButton(string caption, string toolTip)
        {
            var label = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.WindowTextKey);
            var button = new Border
            {
                Child = label,
                ToolTip = toolTip,
                Cursor = Cursors.Hand,
                Padding = new Thickness(8, 0, 8, 1),
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Accent),
                Background = Brushes.Transparent
            };
            var hover = new SolidColorBrush(Color.FromArgb(0x60, Accent.R, Accent.G, Accent.B));
            button.MouseEnter += (_, __) => button.Background = hover;
            button.MouseLeave += (_, __) => button.Background = Brushes.Transparent;
            return button;
        }

        private void TextBuffer_Changed(object sender, TextContentChangedEventArgs e)
        {
            _changeTimer.Stop();
            _changeTimer.Start();
        }

        // Коммит или update снаружи меняют родителя рабочей копии: проверяем при возврате на вкладку.
        private void View_GotAggregateFocus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Evaluate();
        }

        private void Document_FileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Evaluate();
        }

        private void Settings_Changed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Evaluate();
        }

        private void Evaluate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_isDisposed) return;
            if (_isDismissed || !HgDiffBarSettings.IsEnabled)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            var parent = ReadWorkingParent(_repoRoot);
            if (parent != _baseParent)
            {
                LoadBase(parent);
                return;
            }

            Visibility = _baseText != null && _baseText != _view.TextBuffer.CurrentSnapshot.GetText()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LoadBase(string parent)
        {
            if (_isLoading) return;
            _isLoading = true;
            var filePath = _document.FilePath;
            var encoding = _document.Encoding;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var bytes = parent == null ? null : ReadCommittedFile(_repoRoot, filePath);
                var text = bytes == null ? null : Decode(bytes, encoding);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _isLoading = false;
                _baseParent = parent;
                _baseBytes = bytes;
                _baseText = text;
                Evaluate();
            }).Task.FileAndForget("HgVs/DiffBar/LoadBase");
        }

        private void ViewDiff()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_baseBytes == null) return;

            var filePath = _document.FilePath;
            var lineNumber = _view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            try
            {
                var tempPath = HgDiffWindowManager.CreateTempFilePath(Path.GetFileName(filePath));
                File.WriteAllBytes(tempPath, _baseBytes);
                var frame = HgDiffWindowManager.OpenComparison(filePath, tempPath, filePath, true,
                    $"Diff: {Path.GetFileName(filePath)}", "Last commit vs Local", "Last Commit", "Local Version");
                // Окно диффа строит разметку не сразу: переходим на строку, когда UI освободится.
                _ = ThreadHelper.JoinableTaskFactory.StartOnIdle(() => HgDiffWindowManager.GoToRightLine(frame, lineNumber));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening diff: " + ex.Message, "HgVs", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string Decode(byte[] bytes, Encoding documentEncoding)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            var preamble = documentEncoding.GetPreamble();
            var offset = preamble.Length > 0 && bytes.Length >= preamble.Length && StartsWith(bytes, preamble) ? preamble.Length : 0;
            return documentEncoding.GetString(bytes, offset, bytes.Length - offset);
        }

        private static bool StartsWith(byte[] bytes, byte[] prefix)
        {
            for (var i = 0; i < prefix.Length; i++)
                if (bytes[i] != prefix[i]) return false;
            return true;
        }

        public static string FindRepoRoot(string startPath)
        {
            if (string.IsNullOrEmpty(startPath)) return null;
            for (var dir = new DirectoryInfo(startPath); dir != null; dir = dir.Parent)
            {
                if (string.Equals(dir.Name, ".hg", StringComparison.OrdinalIgnoreCase)) return null;
                if (Directory.Exists(Path.Combine(dir.FullName, ".hg"))) return dir.FullName;
            }
            return null;
        }

        /// <summary>Родитель рабочей копии: первые 20 байт .hg/dirstate. Чтение без запуска hg.</summary>
        private static string ReadWorkingParent(string repoRoot)
        {
            try
            {
                using (var stream = new FileStream(Path.Combine(repoRoot, ".hg", "dirstate"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[20];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    return read == buffer.Length ? BitConverter.ToString(buffer) : null;
                }
            }
            catch { return null; }
        }

        /// <summary>Содержимое файла в коммите-родителе, или null, если там его нет.</summary>
        private static byte[] ReadCommittedFile(string repoRoot, string filePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "hg",
                    Arguments = $"cat -r . \"{filePath}\"",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                using (var output = new MemoryStream())
                {
                    process.StandardOutput.BaseStream.CopyTo(output);
                    process.WaitForExit();
                    return process.ExitCode == 0 ? output.ToArray() : null;
                }
            }
            catch { return null; }
        }

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualHeight;
        public bool Enabled => true;
        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _changeTimer.Stop();
            _view.TextBuffer.Changed -= TextBuffer_Changed;
            _view.GotAggregateFocus -= View_GotAggregateFocus;
            _document.FileActionOccurred -= Document_FileActionOccurred;
            HgDiffBarSettings.Changed -= Settings_Changed;
        }
    }
}
