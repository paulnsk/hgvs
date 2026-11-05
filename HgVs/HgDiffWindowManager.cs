using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;

namespace HgVs
{
    /// <summary>
    /// Открывает диффы и старые версии файлов так, чтобы одинаковое содержимое не открывалось повторно,
    /// и удаляет временные копии версий при закрытии окна.
    /// Окно считается тем же, если совпадают рабочий файл и содержимое обеих сторон (рабочая копия сравнивается по пути).
    /// </summary>
    internal static class HgDiffWindowManager
    {
        private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "HgVs");
        private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;
        private static readonly string ProcessTempDir = Path.Combine(TempRoot, CurrentProcessId.ToString());
        private static readonly Dictionary<string, IVsWindowFrame> Frames = new Dictionary<string, IVsWindowFrame>(StringComparer.Ordinal);

        /// <summary>
        /// Удаляет временные папки экземпляров VS, которые уже не запущены.
        /// Папки живых экземпляров не трогает: у них могут быть открыты диффы.
        /// </summary>
        public static void CleanupStaleTempDirs()
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.GetDirectories(TempRoot))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == CurrentProcessId || IsProcessRunning(pid))
                    continue;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Путь для новой временной копии версии файла. Имя файла сохраняется, папка уникальна.</summary>
        public static string CreateTempFilePath(string fileName)
        {
            var dir = Path.Combine(ProcessTempDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        /// <summary>Удаляет временную копию вместе с её папкой. Файлы вне временной папки расширения не трогает.</summary>
        public static void DeleteTempFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.Equals(Path.GetDirectoryName(dir), ProcessTempDir, StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(dir, true); } catch { }
        }

        /// <summary>
        /// Открывает дифф или активирует уже открытый с тем же содержимым.
        /// Временные копии (leftPath всегда, rightPath если это не рабочая копия) переходят во владение менеджера.
        /// </summary>
        /// <returns>Окно диффа (открытое или активированное), либо null.</returns>
        public static IVsWindowFrame OpenComparison(string workingFilePath, string leftPath, string rightPath, bool rightIsWorkingCopy,
            string caption, string tooltip, string leftLabel, string rightLabel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var tempFiles = rightIsWorkingCopy ? new[] { leftPath } : new[] { leftPath, rightPath };
            var rightKey = rightIsWorkingCopy ? "WC:" + NormalizePath(rightPath) : HashFile(rightPath);
            var key = "DIFF|" + NormalizePath(workingFilePath) + "|" + HashFile(leftPath) + "|" + rightKey;

            if (TryActivate(key))
            {
                foreach (var file in tempFiles) DeleteTempFile(file);
                return Frames[key];
            }

            var diffService = (IVsDifferenceService)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
            var frame = diffService?.OpenComparisonWindow2(leftPath, rightPath, caption, tooltip, leftLabel, rightLabel, null, null, 0);
            Register(key, frame, tempFiles);
            return frame;
        }

        /// <summary>Ставит курсор правой (или inline) части диффа на строку рабочего файла и прокручивает к ней.</summary>
        public static void GoToRightLine(IVsWindowFrame frame, int lineNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame == null ||
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView) != VSConstants.S_OK ||
                !(docView is IVsDifferenceCodeWindow codeWindow) ||
                codeWindow.DifferenceViewer == null)
                return;

            var viewer = codeWindow.DifferenceViewer;
            var view = viewer.ViewMode == DifferenceViewMode.Inline ? viewer.InlineView : viewer.RightView;
            var rightSnapshot = viewer.DifferenceBuffer.RightBuffer.CurrentSnapshot;
            var line = rightSnapshot.GetLineFromLineNumber(Math.Max(0, Math.Min(lineNumber, rightSnapshot.LineCount - 1)));
            var point = view.BufferGraph.MapUpToSnapshot(line.Start, PointTrackingMode.Positive, PositionAffinity.Successor, view.TextSnapshot);
            if (point == null) return;

            view.Caret.MoveTo(point.Value);
            view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point.Value, point.Value), EnsureSpanVisibleOptions.AlwaysCenter);
        }

        /// <summary>Открывает временную копию версии файла или активирует уже открытую с тем же содержимым.</summary>
        public static void OpenTempFile(string workingFilePath, string tempPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var key = "OPEN|" + NormalizePath(workingFilePath) + "|" + HashFile(tempPath);
            if (TryActivate(key))
            {
                DeleteTempFile(tempPath);
                return;
            }

            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, tempPath, Guid.Empty, out _, out _, out var frame);
            frame?.Show();
            Register(key, frame, new[] { tempPath });
        }

        private static bool TryActivate(string key)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Frames.TryGetValue(key, out var frame)) return false;
            try
            {
                if (IsDocumentFrameOpen(frame) && frame.Show() == VSConstants.S_OK) return true;
            }
            catch { }
            Frames.Remove(key);
            return false;
        }

        private static bool IsDocumentFrameOpen(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var shell = (IVsUIShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell));
            if (shell == null || shell.GetDocumentWindowEnum(out var frames) != VSConstants.S_OK || frames == null) return false;

            var buffer = new IVsWindowFrame[1];
            while (frames.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                if (ReferenceEquals(buffer[0], frame)) return true;
            }
            return false;
        }

        private static void Register(string key, IVsWindowFrame frame, string[] tempFiles)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame == null)
            {
                foreach (var file in tempFiles) DeleteTempFile(file);
                return;
            }

            Frames[key] = frame;
            if (frame is IVsWindowFrame2 frame2)
                frame2.Advise(new FrameCloseSink(key, frame, tempFiles), out _);
        }

        private static string NormalizePath(string path) => Path.GetFullPath(path).ToLowerInvariant();

        private static string HashFile(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static bool IsProcessRunning(int pid)
        {
            try
            {
                using (Process.GetProcessById(pid)) return true;
            }
            catch (ArgumentException) { return false; }
        }

        private sealed class FrameCloseSink : IVsWindowFrameNotify
        {
            private readonly string _key;
            private readonly IVsWindowFrame _frame;
            private readonly string[] _tempFiles;

            public FrameCloseSink(string key, IVsWindowFrame frame, string[] tempFiles)
            {
                _key = key;
                _frame = frame;
                _tempFiles = tempFiles;
            }

            public int OnShow(int fShow)
            {
                if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed)
                {
                    if (Frames.TryGetValue(_key, out var registered) && ReferenceEquals(registered, _frame))
                        Frames.Remove(_key);
                    foreach (var file in _tempFiles) DeleteTempFile(file);
                }
                return VSConstants.S_OK;
            }

            public int OnMove() => VSConstants.S_OK;
            public int OnSize() => VSConstants.S_OK;
            public int OnDockableChange(int fDockable) => VSConstants.S_OK;
        }
    }
}
