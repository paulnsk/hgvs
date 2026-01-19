using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;

namespace FileCopyExtension
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class FileCopyCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("e3636cf9-410e-4892-81e6-2fc4f329c0aa");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileCopyCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private FileCopyCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static FileCopyCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in FileCopyCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new FileCopyCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            //string message = string.Format(CultureInfo.CurrentCulture, "Inside {0}.MenuItemCallback()", this.GetType().FullName);
            //string title = "FileCopyCommand";

            //// Show a message box to prove we were here
            //VsShellUtilities.ShowMessageBox(
            //    this.package,
            //    message,
            //    title,
            //    OLEMSGICON.OLEMSGICON_INFO,
            //    OLEMSGBUTTON.OLEMSGBUTTON_OK,
            //    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            CopyOpenFiles();
        }



        private void CopyOpenFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Используем GetGlobalService для надежности - это безопаснее, чем вызывать .Result у асинхронного метода
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));

            if (dte == null || dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
            {
                VsShellUtilities.ShowMessageBox(this.package, "Решение не открыто.", "Ошибка", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            string solutionRoot = Path.GetDirectoryName(dte.Solution.FullName);
            string targetBaseDir = GetTargetDirectory();

            if (string.IsNullOrEmpty(targetBaseDir)) return; // Пользователь отменил

            // Создаем папку с таймстемпом сразу, чтобы было видно, что команда отработала
            string timestampFolder = DateTime.Now.ToString("yyyyMMddHHmmss");
            string sessionTargetDir = Path.Combine(targetBaseDir, timestampFolder);

            if (!Directory.Exists(sessionTargetDir))
            {
                Directory.CreateDirectory(sessionTargetDir);
            }

            int count = 0;

            // dte.Documents может быть null в редких случаях
            if (dte.Documents != null)
            {
                foreach (Document doc in dte.Documents)
                {
                    // Убрана проверка doc.Kind. Полагаемся только на наличие файла на диске.
                    string sourcePath = "";
                    try
                    {
                        // doc.FullName иногда может бросать исключение для виртуальных документов
                        sourcePath = doc.FullName;
                    }
                    catch { continue; }

                    // Игнорируем файлы, у которых нет визуальных окон (закрытые/фантомные) ---
                    if (doc.Windows.Count == 0) continue;

                    if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) continue;

                    string relativePath;
                    if (sourcePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Файл внутри папки решения: убираем корень решения
                        relativePath = sourcePath.Substring(solutionRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    else
                    {
                        // Файл вне папки решения: кладем в _External
                        relativePath = Path.Combine("_External", Path.GetFileName(sourcePath));
                    }

                    string destPath = Path.Combine(sessionTargetDir, relativePath);
                    string destDir = Path.GetDirectoryName(destPath);

                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    try
                    {
                        File.Copy(sourcePath, destPath, true);
                        count++;
                    }
                    catch
                    {
                        // Игнорируем ошибки доступа к отдельным файлам
                    }
                }
            }

            VsShellUtilities.ShowMessageBox(
                this.package,
                $"Скопировано файлов: {count}\nПуть: {sessionTargetDir}",
                "Успех",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private string GetTargetDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SettingsManager settingsManager = new ShellSettingsManager(this.package);
            WritableSettingsStore userSettingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
            string collectionName = "FileCopyExtension";
            string propertyName = "TargetDirectory";

            // Проверяем, зажат ли Shift
            bool isShiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            if (!userSettingsStore.CollectionExists(collectionName))
            {
                userSettingsStore.CreateCollection(collectionName);
            }

            string storedPath = userSettingsStore.GetString(collectionName, propertyName, "");

            // Если Shift НЕ нажат И путь существует — используем его
            if (!isShiftPressed && !string.IsNullOrEmpty(storedPath) && Directory.Exists(storedPath))
            {
                return storedPath;
            }

            // Иначе (или Shift нажат, или пути нет) — открываем диалог
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = isShiftPressed
                    ? "Смена директории (нажат Shift)"
                    : "Выберите базовую папку для копирования файлов";

                // Если был старый путь, подставим его для удобства
                if (!string.IsNullOrEmpty(storedPath) && Directory.Exists(storedPath))
                {
                    dialog.SelectedPath = storedPath;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;
                    userSettingsStore.SetString(collectionName, propertyName, selectedPath);
                    return selectedPath;
                }
            }

            return null;
        }



    }
}
