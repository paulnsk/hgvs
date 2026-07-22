using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.IO;
using System.Windows.Forms;

namespace HgVs
{
    internal static class FileDumpLogic
    {
        public static string DumpOpenFiles(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));

            if (dte == null || dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
            {
                VsShellUtilities.ShowMessageBox(package, "Решение не открыто.", "Ошибка", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return null;
            }

            var solutionRoot = Path.GetDirectoryName(dte.Solution.FullName);
            var targetBaseDir = GetTargetDirectory(package);

            if (string.IsNullOrEmpty(targetBaseDir)) return null;

            var timestampFolder = DateTime.Now.ToString("yyyyMMddHHmmss");
            var sessionTargetDir = Path.Combine(targetBaseDir, timestampFolder);

            if (!Directory.Exists(sessionTargetDir))
            {
                Directory.CreateDirectory(sessionTargetDir);
            }

            var count = 0;

            if (dte.Documents != null)
            {
                foreach (Document doc in dte.Documents)
                {
                    var sourcePath = "";
                    try
                    {
                        sourcePath = doc.FullName;
                    }
                    catch { continue; }

                    if (doc.Windows.Count == 0) continue;
                    if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) continue;

                    string relativePath;
                    if (sourcePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = sourcePath.Substring(solutionRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    else
                    {
                        relativePath = Path.Combine("_External", Path.GetFileName(sourcePath));
                    }

                    var destPath = Path.Combine(sessionTargetDir, relativePath);
                    var destDir = Path.GetDirectoryName(destPath);

                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    try
                    {
                        File.Copy(sourcePath, destPath, true);
                        count++;
                    }
                    catch
                    {
                        // Игнорируем ошибки
                    }
                }
            }

            if (!string.IsNullOrEmpty(sessionTargetDir))
            {
                // Считаем файлы заново или передаем count из логики - 
                // но проще просто показать путь, так как DumpOpenFiles делает всю работу.
                // Для подсчета файлов пришлось бы менять сигнатуру, но для месседжбокса достаточно факта успеха.

                // Чтобы не менять сигнатуру метода DumpOpenFiles слишком сильно, просто проверим директорию
                var count2 = Directory.GetFiles(sessionTargetDir, "*.*", SearchOption.AllDirectories).Length;

                VsShellUtilities.ShowMessageBox(
                    package,
                    $"Скопировано файлов: {count2}\nПуть: {sessionTargetDir}",
                    "Успех",
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }



            return sessionTargetDir; // Возвращаем путь для использования в TotalCmd
        }

        private static string GetTargetDirectory(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SettingsManager settingsManager = new ShellSettingsManager(package);
            var userSettingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
            var collectionName = "HgVs";
            var propertyName = "TargetDirectory";

            var isShiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            if (!userSettingsStore.CollectionExists(collectionName))
            {
                userSettingsStore.CreateCollection(collectionName);
            }

            var storedPath = userSettingsStore.GetString(collectionName, propertyName, "");

            if (!isShiftPressed && !string.IsNullOrEmpty(storedPath) && Directory.Exists(storedPath))
            {
                return storedPath;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = isShiftPressed
                    ? "Смена директории (нажат Shift)"
                    : "Выберите базовую папку для копирования файлов";

                if (!string.IsNullOrEmpty(storedPath) && Directory.Exists(storedPath))
                {
                    dialog.SelectedPath = storedPath;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var selectedPath = dialog.SelectedPath;
                    userSettingsStore.SetString(collectionName, propertyName, selectedPath);
                    return selectedPath;
                }
            }

            return null;
        }
    }
}
