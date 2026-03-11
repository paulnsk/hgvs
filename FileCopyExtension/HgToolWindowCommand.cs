using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using FileCopyExtension.ViewModels;
using Task = System.Threading.Tasks.Task;

namespace FileCopyExtension
{
    internal sealed class HgToolWindowCommand
    {
        public const int CommandId = 0x0102; // Новый ID для команды
        public static readonly Guid CommandSet = new Guid("e3636cf9-410e-4892-81e6-2fc4f329c0aa");

        private readonly AsyncPackage package;

        private HgToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static HgToolWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new HgToolWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = package.FindToolWindow(typeof(HgToolWindow), 0, true);
            if ((null == window) || (null == window.Frame))
            {
                throw new NotSupportedException("Cannot create tool window");
            }

            var windowFrame = (IVsWindowFrame)window.Frame;

            // Если окно только что открылось, попробуем обновить список
            var control = window.Content as Views.HgToolWindowControl;
            if (control != null && control.DataContext is HgToolWindowViewModel vm && vm.Files.Count == 0)
            {
                control.RefreshList();
            }

            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }
    }
}