using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;

namespace FileCopyExtension
{
    internal sealed class HgOpenFilesCommand
    {
        public const int CommandId = 0x0101;
        public static readonly Guid CommandSet = new Guid("e3636cf9-410e-4892-81e6-2fc4f329c0aa");

        private readonly AsyncPackage package;

        private HgOpenFilesCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static HgOpenFilesCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new HgOpenFilesCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenModifiedFiles();
        }

        private void OpenModifiedFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));

            if (dte == null || dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
            {
                ShowMessage("Сначала откройте решение (Solution).", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            string solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
            string repoRoot = FindRepoRoot(solutionDir);

            if (string.IsNullOrEmpty(repoRoot))
            {
                ShowMessage("Не найден корень репозитория Mercurial (.hg) выше папки решения.", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            try
            {
                // Запускаем hg status
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "hg",
                    Arguments = "status --rev \"p1(first(branch(.)))\" --rev . -man",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.Default
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        ShowMessage("Ошибка при выполнении команды hg.", OLEMSGICON.OLEMSGICON_CRITICAL);
                        return;
                    }

                    string[] files = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (files.Length == 0)
                    {
                        ShowMessage("Нет измененных файлов в текущей ветке.", OLEMSGICON.OLEMSGICON_INFO);
                        return;
                    }

                    if (files.Length > 10)
                    {
                        int result = VsShellUtilities.ShowMessageBox(
                            this.package,
                            $"Вы собираетесь открыть {files.Length} файлов. Это может занять пространство-время. Продолжить?",
                            "Подтверждение",
                            OLEMSGICON.OLEMSGICON_QUERY,
                            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);

                        if (result == 2) // 2 = Cancel (IDCANCEL)
                        {
                            return;
                        }
                    }

                    int openedCount = 0;
                    foreach (string relativePath in files)
                    {
                        string fullPath = Path.Combine(repoRoot, relativePath);
                        if (File.Exists(fullPath))
                        {
                            dte.ItemOperations.OpenFile(fullPath);
                            openedCount++;
                        }
                    }

                    // Опционально: можно вывести сообщение сколько открыли, но обычно это лишнее, если файлы открылись.
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Произошла ошибка: {ex.Message}", OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }

        private string FindRepoRoot(string startPath)
        {
            DirectoryInfo dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".hg")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private void ShowMessage(string message, OLEMSGICON icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                "Hg Open Files",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}