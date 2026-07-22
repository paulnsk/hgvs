using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.IO;
using HgVs.ViewModels;
using Task = System.Threading.Tasks.Task;

namespace HgVs
{
    internal sealed class HgHistoryCommand
    {
        public const int CommandId = 0x0103;
        public static readonly Guid CommandSet = new Guid("0ad800ce-c2db-409b-82fc-f656ddb11660");

        private readonly AsyncPackage package;

        private HgHistoryCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static HgHistoryCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new HgHistoryCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var filePath = GetSelectedFilePath();
            if (string.IsNullOrEmpty(filePath))
            {
                VsShellUtilities.ShowMessageBox(package, "Не удалось определить целевой файл.", "Ошибка", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var window = package.FindToolWindow(typeof(HgHistoryToolWindow), 0, true);
            if ((null == window) || (null == window.Frame))
            {
                throw new NotSupportedException("Cannot create tool window");
            }

            var windowFrame = (IVsWindowFrame)window.Frame;

            var control = window.Content as Views.HgHistoryToolWindowControl;
            if (control != null && control.DataContext is HgHistoryToolWindowViewModel vm)
            {
                vm.TargetFilePath = filePath;
                vm.RefreshCommand.Execute(null);
            }

            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }

        private string GetSelectedFilePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            if (dte == null) return null;

            // Пытаемся взять файл из Solution Explorer
            if (dte.SelectedItems != null && dte.SelectedItems.Count == 1)
            {
                var item = dte.SelectedItems.Item(1);
                if (item.ProjectItem != null && item.ProjectItem.FileCount > 0)
                {
                    var path = item.ProjectItem.FileNames[1];
                    if (File.Exists(path)) return path;
                }
            }

            // Пытаемся взять из активного документа (для контекстного меню таба)
            if (dte.ActiveDocument != null)
            {
                var path = dte.ActiveDocument.FullName;
                if (File.Exists(path)) return path;
            }

            return null;
        }
    }
}
